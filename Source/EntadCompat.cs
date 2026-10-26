using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Hooks for compat assemblies (Mods/<Mod>/, loaded by LoadFolders.xml only while that mod is active). The code that
    // names another mod's types lives there, so this assembly loads without them; a module tells the framework about
    // its traits through these and never the other way round.
    //
    // Abilities from another mod's ability system go in EntadTraitDef.externalAbilities by name: the module registers
    // each name it can grant, draws the gizmos (RegisterGizmos) and keeps its per-item state in the item's save
    // (Exposing). Charges and cooldowns live on the trait, as for vanilla abilities.
    //
    // A module's traits are plain stats (rolled, scaled and shown like any other) or customEffects keys with their own
    // card lines. Both need a registered check saying which buildings they can go on: a stat nobody registered falls
    // through to the framework's own rules, which leave a modded building stat out, and an unregistered effect key never
    // rolls. Register from a StaticConstructorOnStartup: nothing asks before a game is running.
    public static class EntadCompat
    {
        private static readonly Dictionary<StatDef, Func<ThingDef, bool>> statChecks = new Dictionary<StatDef, Func<ThingDef, bool>>();
        private static readonly Dictionary<string, Func<ThingDef, bool>> effectChecks = new Dictionary<string, Func<ThingDef, bool>>();

        // Raised whenever an entad's traits or spawned state change (spawn, despawn, trait added or removed, game
        // loaded), the moment for a module to list or drop it
        public static event Action<CompEntad> Refreshed;

        // Raised every 250 ticks in play, for effects that act over time; no game component of the module's own, so
        // removing the other mod never leaves a save naming a class that is gone
        public static event Action Tick250;

        // Raised once as each game is created or loaded, before anything spawns: the moment to clear static registries.
        // Current.Game may not point at the new game yet, so a handler should only clear.
        public static event Action GameStarted;

        // Raised from CompEntad.PostExposeData in every pass, after the traits have loaded: a module saves what it keeps
        // per item under node names of its own. Only the module reads those nodes, so once it is gone they are skipped
        // and the save loads clean; that holds as long as no class of the module's own is saved where something else
        // reads it, and nothing outside the module keeps a reference into what it saved.
        public static event Action<CompEntad> Exposing;

        // Vanilla constructs game components twice on a load (in new Game() and again reading the save), with the same game
        private static System.WeakReference<Game> startedFor;

        private static readonly Dictionary<string, Func<string>> externalAbilities = new Dictionary<string, Func<string>>();
        private static readonly List<Func<CompEntad, IEnumerable<Gizmo>>> gizmoProviders = new List<Func<CompEntad, IEnumerable<Gizmo>>>();
        private static readonly List<Func<EntadTraitDef, float>> weights = new List<Func<EntadTraitDef, float>>();
        private static readonly List<KeyValuePair<EntadSettingsTab, Action<Listing_Standard>>> settings = new List<KeyValuePair<EntadSettingsTab, Action<Listing_Standard>>>();

        // A name traits can list in externalAbilities, with its label for the card and the settings list
        public static void RegisterExternalAbility(string name, Func<string> label)
        {
            if (name.NullOrEmpty() || label == null) throw new ArgumentNullException(name.NullOrEmpty() ? nameof(name) : nameof(label));
            externalAbilities[name] = label;
        }

        // Gizmos for the holder of a worn or wielded entad, asked each time the holder's gizmos are drawn. Return null
        // or nothing when the item has nothing of the module's.
        public static void RegisterGizmos(Func<CompEntad, IEnumerable<Gizmo>> provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            gizmoProviders.Add(provider);
        }

        // A multiplier on a trait's weight when picking traits (1 = unchanged, 0 = never picked). Several multiply.
        public static void RegisterWeight(Func<EntadTraitDef, float> weight)
        {
            if (weight == null) throw new ArgumentNullException(nameof(weight));
            weights.Add(weight);
        }

        // Drawn at the bottom of a settings tab. Keep values with EntadSettings.ModuleValue / SetModuleValue.
        public static void RegisterSettings(EntadSettingsTab tab, Action<Listing_Standard> draw)
        {
            if (draw == null) throw new ArgumentNullException(nameof(draw));
            settings.Add(new KeyValuePair<EntadSettingsTab, Action<Listing_Standard>>(tab, draw));
        }

        internal static bool KnowsExternalAbility(string name) => name != null && externalAbilities.ContainsKey(name);

        internal static string ExternalAbilityLabel(string name)
        {
            if (name == null || !externalAbilities.TryGetValue(name, out var label)) return name;
            try { return label() ?? name; }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] label of external ability {name} failed: {e}", name.GetHashCode());
                return name;
            }
        }

        internal static bool HasGizmoProviders => gizmoProviders.Count > 0;

        // Materialised per provider, so one that throws partway loses only its own gizmos
        internal static List<Gizmo> GizmosFor(CompEntad comp)
        {
            List<Gizmo> all = null;
            for (int i = 0; i < gizmoProviders.Count; i++)
            {
                var provider = gizmoProviders[i];
                try
                {
                    var gizmos = provider(comp);
                    if (gizmos == null) continue;
                    if (all == null) all = new List<Gizmo>();
                    all.AddRange(gizmos);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] {provider.Method.DeclaringType}.{provider.Method.Name} failed on {comp.parent}: {e}", provider.Method.GetHashCode());
                }
            }
            return all;
        }

        internal static bool HasWeights => weights.Count > 0;

        internal static float WeightOf(EntadTraitDef def)
        {
            float w = 1f;
            for (int i = 0; i < weights.Count; i++)
            {
                try
                {
                    float f = weights[i](def);
                    w *= float.IsNaN(f) ? 1f : Math.Max(0f, f);
                }
                catch (Exception e) { Log.ErrorOnce($"[Entad Framework] {weights[i].Method.DeclaringType}.{weights[i].Method.Name} failed on {def}: {e}", weights[i].Method.GetHashCode()); }
            }
            return w;
        }

        internal static void DrawSettings(EntadSettingsTab tab, Listing_Standard list)
        {
            foreach (var s in settings.Where(s => s.Key == tab))
            {
                list.Gap();
                try { s.Value(list); }
                catch (Exception e) { Log.ErrorOnce($"[Entad Framework] {s.Value.Method.DeclaringType}.{s.Value.Method.Name} failed: {e}", s.Value.Method.GetHashCode()); }
            }
        }

        internal static void RaiseExposing(CompEntad comp)
        {
            if (Exposing == null) return;
            foreach (Action<CompEntad> handler in Exposing.GetInvocationList())
            {
                try { handler(comp); }
                catch (Exception e) { Log.ErrorOnce($"[Entad Framework] {handler.Method.DeclaringType}.{handler.Method.Name} failed on {comp.parent} ({Scribe.mode}): {e}", handler.Method.GetHashCode()); }
            }
        }

        public static void RegisterStat(StatDef stat, Func<ThingDef, bool> appliesTo)
        {
            if (stat == null || appliesTo == null) throw new ArgumentNullException(stat == null ? nameof(stat) : nameof(appliesTo));
            statChecks[stat] = appliesTo;
        }

        public static void RegisterEffect(string key, Func<ThingDef, bool> appliesTo)
        {
            if (key.NullOrEmpty() || appliesTo == null) throw new ArgumentNullException(key.NullOrEmpty() ? nameof(key) : nameof(appliesTo));
            effectChecks[key] = appliesTo;
        }

        internal static bool TryStatAppliesTo(StatDef stat, ThingDef td, out bool applies)
        {
            applies = false;
            if (statChecks.Count == 0 || !statChecks.TryGetValue(stat, out var check)) return false;
            applies = Ask(check, td);
            return true;
        }

        internal static bool EffectAppliesTo(string key, ThingDef td) => effectChecks.TryGetValue(key, out var check) && Ask(check, td);

        // A check that throws (the other mod changed under the module) counts as "doesn't apply" rather than breaking
        // every roll and card that asks
        private static bool Ask(Func<ThingDef, bool> check, ThingDef td)
        {
            try { return check(td); }
            catch (Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] {check.Method.DeclaringType}.{check.Method.Name} failed on {td}: {e}", check.Method.GetHashCode());
                return false;
            }
        }

        // Refreshed runs inside spawn, despawn and load: one module's exception must not stop the item spawning or the
        // other modules hearing about it
        internal static void RaiseRefreshed(CompEntad comp)
        {
            if (Refreshed == null) return;
            foreach (Action<CompEntad> handler in Refreshed.GetInvocationList())
            {
                try { handler(comp); }
                catch (Exception e) { Log.ErrorOnce($"[Entad Framework] {handler.Method.DeclaringType}.{handler.Method.Name} failed on {comp.parent}: {e}", handler.Method.GetHashCode()); }
            }
        }

        internal static void RaiseTick250() => Raise(Tick250);

        internal static void RaiseGameStarted(Game game)
        {
            if (startedFor != null && startedFor.TryGetTarget(out var last) && last == game) return;
            startedFor = new System.WeakReference<Game>(game);
            Raise(GameStarted);
        }

        private static void Raise(Action action)
        {
            if (action == null) return;
            foreach (Action handler in action.GetInvocationList())
            {
                try { handler(); }
                catch (Exception e) { Log.ErrorOnce($"[Entad Framework] {handler.Method.DeclaringType}.{handler.Method.Name} failed: {e}", handler.Method.GetHashCode()); }
            }
        }
    }
}
