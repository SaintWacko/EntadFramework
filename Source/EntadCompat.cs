using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Hooks for compat assemblies (Mods/<Mod>/, loaded by LoadFolders.xml only while that mod is active). The code that
    // names another mod's types lives there, so this assembly loads without them; a module tells the framework about
    // its traits through these and never the other way round.
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

        // Raised as each game is created or loaded, before anything spawns: the moment to clear static registries
        public static event Action GameStarted;

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
            applies = check(td);
            return true;
        }

        internal static bool EffectAppliesTo(string key, ThingDef td) => effectChecks.TryGetValue(key, out var check) && check(td);

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

        internal static void RaiseGameStarted() => Raise(GameStarted);

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
