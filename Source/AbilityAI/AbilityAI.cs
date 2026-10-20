using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

// The ability AI: lets NPC pawns (raiders, visitors, tamed animals, mechs) cast abilities with some judgement.
//
// STANDALONE ON PURPOSE. Nothing in this folder may reference an Entad type: it is meant to be copied into another
// mod (Bastle Framework) or a shared library as it is. The host mod plugs in through three things only:
// - IAbilityAIHook, registered with AbilityAIHooks.Register, which claims or vetoes abilities per pawn and hears
//   about each cast the AI starts;
// - AbilityAISettings.ExposeData / DoSettings, called from the host's own ModSettings and settings window;
// - the EF_AI_ keys in Languages/English/Keyed/AbilityAI.xml. A copy in another mod must rename that prefix (and this
//   namespace), or the two mods' keyed strings collide.
// The Harmony patch class at the bottom is picked up by whatever patching the host does ([HarmonyPatch] on the class).
//
// How an ability opts in: an AbilityAIExtension on its AbilityDef naming a worker class (AbilityAIWorkers.cs). No
// extension, no AI casts. Vanilla's own aiCanUse flag is deliberately NOT required: it is also what vanilla's fight
// AI (JobGiver_AIFightEnemy.GetAbilityJob) and JobGiver_NPCCastNonCombatAbilities read, and those cast anything
// offensive straight at the current enemy, or anything else at random. Abilities using this AI keep aiCanUse off so
// vanilla leaves them alone. Every other vanilla gate still applies: CanCast (charges, cooldown, comps), CanApplyOn,
// each effect comp's AICanTargetNow, and the verb's own ValidateTarget (range, line of sight, target rules).
//
// Two copies of this code in two mods would each patch the same method; that is harmless as long as each one only
// casts what its own hooks claim. With "unclaimed abilities" on in both, a pawn could be evaluated twice.
namespace EntadFramework.AbilityAI
{
    public enum AbilityAIVote { NoOpinion, Claim, Veto }

    /// <summary>A host mod's say over which abilities the AI may cast for which pawns.</summary>
    public interface IAbilityAIHook
    {
        /// <summary>Asked for every ready ability with an AI extension, before any targeting work. Claim = this
        /// ability is the host's to cast for this pawn; Veto = never now (an unbound item, say).</summary>
        AbilityAIVote Vote(Pawn pawn, Ability ability);

        /// <summary>The AI has just handed vanilla a job to cast this ability. Vanilla may still discard it without
        /// starting it (a job override check with a minimum priority, say), and a started cast resolves later
        /// (Ability.Activate) or not at all if interrupted: hook anything that must only happen on a real cast to
        /// Ability.Activate instead.</summary>
        void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest);
    }

    public static class AbilityAIHooks
    {
        private static readonly List<IAbilityAIHook> hooks = new List<IAbilityAIHook>();

        public static void Register(IAbilityAIHook hook)
        {
            if (hook != null && !hooks.Contains(hook)) hooks.Add(hook);
        }

        // Any veto wins; otherwise a claim, or the "unclaimed abilities" setting, lets it through
        internal static bool Allowed(Pawn pawn, Ability ability)
        {
            bool claimed = false;
            for (int i = 0; i < hooks.Count; i++)
            {
                AbilityAIVote v;
                try { v = hooks[i].Vote(pawn, ability); }
                catch (Exception e)
                {
                    Log.ErrorOnce($"[Ability AI] {hooks[i].GetType().Name}.Vote threw: {e}", hooks[i].GetType().GetHashCode() ^ 0x5A1);
                    continue;
                }
                if (v == AbilityAIVote.Veto) return false;
                if (v == AbilityAIVote.Claim) claimed = true;
            }
            return claimed || AbilityAISettings.Unclaimed;
        }

        internal static void NotifyCast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest)
        {
            for (int i = 0; i < hooks.Count; i++)
            {
                try { hooks[i].Notify_AICast(pawn, ability, target, dest); }
                catch (Exception e) { Log.ErrorOnce($"[Ability AI] {hooks[i].GetType().Name}.Notify_AICast threw: {e}", hooks[i].GetType().GetHashCode() ^ 0x5A2); }
            }
        }
    }

    /// <summary>Optional, on an ability's effect comp properties: what the AI can't read from vanilla comps. Lets a
    /// mod's own comp tell the workers its area and the hediff it gives without the AI knowing the comp's type.</summary>
    public interface IAbilityAIInfo
    {
        /// <summary>Radius of the affected area around the target, or 0 for one target.</summary>
        float AIRadius { get; }
        /// <summary>The hediff the ability gives, so the AI doesn't recast it on pawns that still have it. Null for none.</summary>
        HediffDef AIHediff { get; }
    }

    public enum AbilityAICombat
    {
        /// <summary>The worker's own default.</summary>
        Default,
        /// <summary>Only while the caster is fighting.</summary>
        InCombat,
        /// <summary>Only while it isn't.</summary>
        OutOfCombat,
        /// <summary>Either.</summary>
        Any,
    }

    [Flags]
    public enum AbilityAIMoveModes
    {
        None = 0,
        /// <summary>Close the distance to the enemy (melee pawns).</summary>
        GapClose = 1,
        /// <summary>Get away: badly hurt, fleeing, or a ranged pawn caught in melee.</summary>
        Escape = 2,
        Both = GapClose | Escape,
    }

    /// <summary>Put on an AbilityDef to let the AI cast it. Only workerClass is required; the rest are tuning a
    /// worker reads if it cares (see each worker's notes in AbilityAIWorkers.cs).</summary>
    public class AbilityAIExtension : DefModExtension
    {
        public Type workerClass;
        /// <summary>Higher is tried first when a pawn has several ready abilities.</summary>
        public int priority;
        public AbilityAICombat combat = AbilityAICombat.Default;
        /// <summary>Charges the AI never spends (abilities with charges only).</summary>
        public int reserveCharges;
        /// <summary>Seconds after an AI cast of this ability before the AI considers it again, on top of its cooldown.</summary>
        public float minGapSeconds;

        /// <summary>Area affected around the target; negative = read it from the effect comps.</summary>
        public float radius = -1f;
        /// <summary>Pawns the cast must affect (enemies for hostile workers, allies for friendly ones).</summary>
        public int minTargets = 1;
        /// <summary>Distance band from the caster to the target or threat; maxRange negative = the verb's range.</summary>
        public float minRange;
        public float maxRange = -1f;
        /// <summary>Summary health below which a pawn counts as badly hurt.</summary>
        public float healthThreshold = 0.5f;
        /// <summary>Recovery: the need to watch and the level below which to cast.</summary>
        public NeedDef need;
        public float needThreshold = 0.3f;
        /// <summary>Skip pawns that already have this; null = read it from the effect comps.</summary>
        public HediffDef hediff;
        /// <summary>Movement: which situations to move in.</summary>
        public AbilityAIMoveModes modes = AbilityAIMoveModes.Both;
        /// <summary>Movement gap-close: don't bother below this distance to the enemy.</summary>
        public float minGapDistance = 6f;
        /// <summary>Cover: how far from the caster toward the threat to place the cover; 0 = on the caster.</summary>
        public float offsetTowardThreat;
        /// <summary>Cover: ranged enemies aiming at the caster before it takes cover (being shot recently also counts).</summary>
        public int minThreats = 1;
        /// <summary>Hostile area workers: don't pick a centre with an ally of the caster inside the radius.</summary>
        public bool avoidAllies;

        [Unsaved] private AbilityAIWorker worker;

        // One instance per extension. Psycast copies share their source's extension list by reference, so one worker
        // serves every copy: workers must keep no per-pawn state.
        public AbilityAIWorker Worker
        {
            get
            {
                if (worker == null && workerClass != null && typeof(AbilityAIWorker).IsAssignableFrom(workerClass) && !workerClass.IsAbstract)
                {
                    worker = (AbilityAIWorker)Activator.CreateInstance(workerClass);
                    worker.ext = this;
                }
                return worker;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;
            if (workerClass == null) yield return "AbilityAIExtension needs a workerClass";
            else if (!typeof(AbilityAIWorker).IsAssignableFrom(workerClass)) yield return $"workerClass {workerClass} is not an AbilityAIWorker";
            else if (workerClass.IsAbstract) yield return $"workerClass {workerClass} is abstract";
            if (reserveCharges < 0) yield return "reserveCharges can't be negative";
            if (minTargets < 1) yield return "minTargets must be at least 1";
        }
    }

    /// <summary>What a worker can ask about the caster's situation. Lists are built on first use and shared by
    /// every worker tried in one evaluation, so a pawn with three ready abilities scans the map once.</summary>
    public class AbilityAIContext
    {
        public Pawn pawn;
        public Map map;
        public int tick;

        private bool combatKnown, inCombat;
        private bool enemiesBuilt, alliesBuilt;
        private readonly List<Pawn> enemies = new List<Pawn>();
        private readonly List<Pawn> allies = new List<Pawn>();
        private Thing enemyTarget;
        private bool enemyTargetKnown;

        internal void Begin(Pawn p, int t)
        {
            pawn = p;
            map = p.Map;
            tick = t;
            combatKnown = enemiesBuilt = alliesBuilt = enemyTargetKnown = false;
        }

        internal void End()
        {
            enemies.Clear();
            allies.Clear();
            enemyTarget = null;
            pawn = null;
            map = null;
        }

        public const int RecentTicks = 600;

        /// <summary>Has a hostile target, or attacked or was hurt in the last 10 seconds.</summary>
        public bool InCombat
        {
            get
            {
                if (!combatKnown)
                {
                    var mind = pawn.mindState;
                    inCombat = EnemyTarget != null
                        || (mind != null && (tick - mind.lastHarmTick < RecentTicks || tick - mind.lastAttackTargetTick < RecentTicks
                            || (mind.lastRangedHarmTick > 0 && tick - mind.lastRangedHarmTick < RecentTicks)));
                    combatKnown = true;
                }
                return inCombat;
            }
        }

        /// <summary>The pawn's current hostile target: the fight job givers' enemy target, the target of an attack
        /// job (manhunters), or whoever is attacking it in melee.</summary>
        public Thing EnemyTarget
        {
            get
            {
                if (!enemyTargetKnown)
                {
                    enemyTargetKnown = true;
                    Thing t = pawn.mindState?.enemyTarget;
                    var job = pawn.CurJob;
                    if (t == null && job != null && (job.def == JobDefOf.AttackMelee || job.def == JobDefOf.AttackStatic)) t = job.targetA.Thing;
                    if (t == null) t = pawn.mindState?.meleeThreat;
                    if (t != null && (!t.Spawned || t.Map != map || (t is Pawn p && (p.Dead || p.Downed)))) t = null;
                    // A wild animal hit by a hunter isn't hostile to it, but is fighting back
                    if (t != null && !t.HostileTo(pawn) && t != pawn.mindState?.meleeThreat) t = null;
                    enemyTarget = t;
                }
                return enemyTarget;
            }
        }

        /// <summary>Hostile pawns on the map that are a threat now: standing, visible and not threat-disabled.
        /// From the attack-target cache, so turrets and other buildings are left out.</summary>
        public List<Pawn> Enemies
        {
            get
            {
                if (!enemiesBuilt)
                {
                    enemiesBuilt = true;
                    // The cache hands back one shared list that its next caller clears, so copy out of it at once
                    var targets = map.attackTargetsCache.GetPotentialTargetsFor(pawn);
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (!(targets[i].Thing is Pawn p) || !p.Spawned || p.Dead || p.Downed) continue;
                        if (targets[i].ThreatDisabled(pawn) || p.IsPsychologicallyInvisible() || !p.HostileTo(pawn)) continue;
                        enemies.Add(p);
                    }
                }
                return enemies;
            }
        }

        /// <summary>Standing pawns of the caster's faction on the map, the caster included. A pawn with no faction
        /// has only itself.</summary>
        public List<Pawn> Allies
        {
            get
            {
                if (!alliesBuilt)
                {
                    alliesBuilt = true;
                    if (pawn.Faction == null) allies.Add(pawn);
                    else
                    {
                        var list = map.mapPawns.SpawnedPawnsInFaction(pawn.Faction);
                        // A captured raider keeps its faction: not someone its friends can buff from across the map
                        for (int i = 0; i < list.Count; i++)
                            if (!list[i].Dead && !list[i].Downed && !list[i].IsPrisoner) allies.Add(list[i]);
                    }
                }
                return allies;
            }
        }

        /// <summary>Whether the pawn fights at range: its primary weapon, or for a pawn without one (animals, some
        /// mechs) its first ranged verb.</summary>
        public bool UsesRanged => FightsAtRange(pawn);

        public static bool FightsAtRange(Pawn p)
        {
            var primary = p.equipment?.Primary;
            if (primary != null) return primary.def.IsRangedWeapon;
            var verbs = p.verbTracker?.AllVerbs;
            if (verbs != null) for (int i = 0; i < verbs.Count; i++) if (!verbs[i].IsMeleeAttack) return true;
            return false;
        }

        public float HealthFraction => pawn.health?.summaryHealth?.SummaryHealthPercent ?? 1f;

        /// <summary>The nearest enemy and its squared distance, or null.</summary>
        public Pawn NearestEnemy(out float distSq)
        {
            Pawn best = null;
            distSq = float.MaxValue;
            var list = Enemies;
            for (int i = 0; i < list.Count; i++)
            {
                float d = (list[i].Position - pawn.Position).LengthHorizontalSquared;
                if (d < distSq) { distSq = d; best = list[i]; }
            }
            return best;
        }
    }

    public static class AbilityAIDriver
    {
        // Between evaluations of one pawn. Vanilla asks the constant think tree every 30 ticks and again each time
        // the pawn picks a job, so without this a pawn changing jobs often would be evaluated more than that.
        public const int EvalIntervalTicks = 30;

        private class PawnState
        {
            public int nextEvalTick;
            public int nextCastTick;
            public Dictionary<AbilityDef, int> abilityNext;
        }

        // Not saved: after a load every pawn may cast again at once, limited by the ability's own cooldown and charges
        private static readonly ConditionalWeakTable<Pawn, PawnState> states = new ConditionalWeakTable<Pawn, PawnState>();
        private static readonly AbilityAIContext ctx = new AbilityAIContext();
        private static readonly List<Ability> candidates = new List<Ability>();
        private static bool evaluating;

        /// <summary>Whether any of these abilities has an AI extension. The first check that touches defs.</summary>
        internal static bool AnyWithExtension(List<Ability> abilities)
        {
            for (int i = 0; i < abilities.Count; i++)
                if (abilities[i].def.GetModExtension<AbilityAIExtension>() != null) return true;
            return false;
        }

        /// <summary>Whether the AI acts for this kind of pawn at all, by the settings. Never the player's own
        /// humanlikes (colonists, slaves, ghouls: the player has their gizmos) or prisoners.</summary>
        public static bool Covered(Pawn pawn)
        {
            if (pawn.Faction != null && pawn.Faction.IsPlayer && pawn.RaceProps.Humanlike) return false;
            if (pawn.IsPrisoner) return false;
            // The player's animals and mechs in a lord are forming a caravan, roped or the like: a constant-tree job
            // interrupts whatever they are doing, so leave them to it
            if (pawn.Faction != null && pawn.Faction.IsPlayer && pawn.GetLord() != null) return false;
            if (pawn.RaceProps.IsMechanoid) return AbilityAISettings.Mechs;
            if (pawn.RaceProps.Animal) return AbilityAISettings.Animals;
            return pawn.HostileTo(Faction.OfPlayer) ? AbilityAISettings.Hostiles : AbilityAISettings.NonHostiles;
        }

        /// <summary>Awake, standing, not on fire, not drafted, not stunned, not already casting, and not in a mental
        /// state other than an aggressive one or panic (a fleeing pawn may still blink away).</summary>
        public static bool CanActNow(Pawn pawn)
        {
            if (!pawn.Spawned || pawn.Dead || pawn.Downed || !pawn.Awake() || pawn.IsBurning()) return false;
            if (pawn.Drafted) return false;
            if (pawn.InMentalState && !pawn.InAggroMentalState && pawn.MentalStateDef != MentalStateDefOf.PanicFlee) return false;
            if (pawn.CurJob?.ability != null) return false;
            if (pawn.stances?.stunner?.Stunned == true) return false;
            return true;
        }

        /// <summary>Every vanilla check a player's cast would pass, minus aiCanUse (see the file header), plus each
        /// effect comp's AI veto. Workers call it on each target they consider; it is also run once more on the
        /// target they return.</summary>
        public static bool Valid(Ability ability, LocalTargetInfo target)
        {
            if (!target.IsValid || !ability.CanApplyOn(target)) return false;
            var comps = ability.EffectComps;
            if (comps != null) for (int i = 0; i < comps.Count; i++) if (!comps[i].AICanTargetNow(target)) return false;
            return ability.verb.ValidateTarget(target, false);
        }

        internal static Job TryGiveJob(Pawn pawn, List<Ability> abilities)
        {
            if (evaluating) return null;
            int tick = Find.TickManager.TicksGame;
            var state = states.GetValue(pawn, _ => new PawnState());
            if (tick < state.nextEvalTick || tick < state.nextCastTick) return null;
            state.nextEvalTick = tick + EvalIntervalTicks;
            if (!CanActNow(pawn)) return null;

            // Ready abilities with a worker, highest priority first (insertion sort: a pawn holds a handful)
            candidates.Clear();
            for (int i = 0; i < abilities.Count; i++)
            {
                Ability a = abilities[i];
                var ext = a.def.GetModExtension<AbilityAIExtension>();
                if (ext?.Worker == null || a.verb == null || !a.CanCast) continue;
                if (ext.reserveCharges > 0 && a.UsesCharges && a.RemainingCharges <= ext.reserveCharges) continue;
                if (state.abilityNext != null && state.abilityNext.TryGetValue(a.def, out int next) && tick < next) continue;
                if (!AbilityAIHooks.Allowed(pawn, a)) continue;
                int at = candidates.Count;
                while (at > 0 && candidates[at - 1].def.GetModExtension<AbilityAIExtension>().priority < ext.priority) at--;
                candidates.Insert(at, a);
            }
            if (candidates.Count == 0) return null;

            evaluating = true;
            ctx.Begin(pawn, tick);
            try
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    Ability a = candidates[i];
                    var ext = a.def.GetModExtension<AbilityAIExtension>();
                    var worker = ext.Worker;
                    switch (worker.Combat)
                    {
                        case AbilityAICombat.InCombat: if (!ctx.InCombat) continue; break;
                        case AbilityAICombat.OutOfCombat: if (ctx.InCombat) continue; break;
                    }
                    if (!worker.TryFindTarget(ctx, a, out LocalTargetInfo target, out LocalTargetInfo dest)) continue;
                    if (!dest.IsValid) dest = target;
                    if (!Valid(a, target)) continue;

                    state.nextCastTick = tick + Mathf.RoundToInt(AbilityAISettings.MinGapSeconds * GenTicks.TicksPerRealSecond);
                    if (ext.minGapSeconds > 0f)
                    {
                        if (state.abilityNext == null) state.abilityNext = new Dictionary<AbilityDef, int>();
                        state.abilityNext[a.def] = tick + Mathf.RoundToInt(ext.minGapSeconds * GenTicks.TicksPerRealSecond);
                    }
                    if (AbilityAISettings.DebugLog && Prefs.DevMode)
                        Log.Message($"[Ability AI] {pawn.LabelShort} casts {a.def.label} ({worker.GetType().Name}) on {target}");
                    AbilityAIHooks.NotifyCast(pawn, a, target, dest);
                    return a.GetJob(target, dest);
                }
                return null;
            }
            finally
            {
                ctx.End();
                candidates.Clear();
                evaluating = false;
            }
        }
    }

    /// <summary>The AI's own settings. The host mod saves them inside its ModSettings (ExposeData) and draws them
    /// in its settings window (DoSettings).</summary>
    public static class AbilityAISettings
    {
        public static bool Enabled = true;
        public static bool Hostiles = true;
        public static bool NonHostiles = true;
        public static bool Animals = true;
        public static bool Mechs = true;
        public static bool Unclaimed;
        public const float DefaultMinGapSeconds = 2.5f;
        public static float MinGapSeconds = DefaultMinGapSeconds;
        public static bool DebugLog;

        public static void Reset()
        {
            Enabled = Hostiles = NonHostiles = Animals = Mechs = true;
            Unclaimed = false;
            MinGapSeconds = DefaultMinGapSeconds;
            DebugLog = false;
        }

        public static void ExposeData()
        {
            Scribe_Values.Look(ref Enabled, "abilityAI_enabled", true);
            Scribe_Values.Look(ref Hostiles, "abilityAI_hostiles", true);
            Scribe_Values.Look(ref NonHostiles, "abilityAI_nonHostiles", true);
            Scribe_Values.Look(ref Animals, "abilityAI_animals", true);
            Scribe_Values.Look(ref Mechs, "abilityAI_mechs", true);
            Scribe_Values.Look(ref Unclaimed, "abilityAI_unclaimed", false);
            Scribe_Values.Look(ref MinGapSeconds, "abilityAI_minGapSeconds", DefaultMinGapSeconds);
            MinGapSeconds = Mathf.Clamp(MinGapSeconds, 0f, 30f);
            Scribe_Values.Look(ref DebugLog, "abilityAI_debugLog", false);
        }

        public static void DoSettings(Listing_Standard list)
        {
            list.CheckboxLabeled("EF_AI_Enabled".Translate(), ref Enabled, "EF_AI_Enabled_Tip".Translate());
            list.Gap(6f);
            list.Label("EF_AI_Who".Translate());
            list.CheckboxLabeled("  " + "EF_AI_Hostiles".Translate(), ref Hostiles, "EF_AI_Hostiles_Tip".Translate());
            list.CheckboxLabeled("  " + "EF_AI_NonHostiles".Translate(), ref NonHostiles, "EF_AI_NonHostiles_Tip".Translate());
            list.CheckboxLabeled("  " + "EF_AI_Animals".Translate(), ref Animals, "EF_AI_Animals_Tip".Translate());
            list.CheckboxLabeled("  " + "EF_AI_Mechs".Translate(), ref Mechs, "EF_AI_Mechs_Tip".Translate());
            list.Label("EF_AI_Who_Desc".Translate());
            list.Gap();
            list.CheckboxLabeled("EF_AI_Unclaimed".Translate(), ref Unclaimed, "EF_AI_Unclaimed_Tip".Translate());
            list.Gap();
            list.Label("EF_AI_MinGap".Translate(MinGapSeconds.ToString("0.#")));
            MinGapSeconds = Mathf.Round(list.Slider(MinGapSeconds, 0f, 30f) * 2f) / 2f;
            list.Label("EF_AI_MinGap_Desc".Translate());
            if (Prefs.DevMode)
            {
                list.Gap();
                list.CheckboxLabeled("EF_AI_DebugLog".Translate(), ref DebugLog, "EF_AI_DebugLog_Tip".Translate());
            }
        }
    }

    /// <summary>Runs the ability AI whenever the constant think tree has nothing to do, as Bastle Framework's
    /// BastleAI does. Pawns with no abilities return after three field reads; pawns whose abilities have no AI
    /// extension after one def lookup per ability.</summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "DetermineNextConstantThinkTreeJob")]
    internal static class Patch_AbilityAI_ConstantThinkTree
    {
        static void Postfix(Pawn ___pawn, ref ThinkResult __result)
        {
            if (__result.IsValid || !AbilityAISettings.Enabled) return;
            var tracker = ___pawn?.abilities;
            if (tracker == null) return;
            var abilities = tracker.AllAbilitiesForReading;
            if (abilities.Count == 0 || !AbilityAIDriver.AnyWithExtension(abilities) || !AbilityAIDriver.Covered(___pawn)) return;
            // Vanilla starts these jobs against the constant tree and saves the job giver's key in it; with no
            // constant tree a saved job would throw on load.
            var root = ___pawn.thinker?.ConstantThinkTree?.thinkRoot;
            if (root == null) return;
            try
            {
                Job job = AbilityAIDriver.TryGiveJob(___pawn, abilities);
                // The tree's root as the source: the AI has no think node of its own, so a pawn saved mid-cast would
                // otherwise log a missing think node on load
                if (job != null) __result = new ThinkResult(job, root);
            }
            catch (Exception e)
            {
                // Vanilla's constant-tree call has its own try/catch; this runs after it
                Log.ErrorOnce($"[Ability AI] threw for {___pawn.def.defName}: {e}", ___pawn.def.shortHash ^ 0x7A12);
            }
        }
    }
}
