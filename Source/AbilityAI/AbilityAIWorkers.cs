using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

// Workers decide WHETHER an ability is worth casting now and AT WHAT. Each is a category, not an ability: an
// AbilityDef picks one with AbilityAIExtension.workerClass and tunes it with the extension's fields. Workers are
// shared between pawns and must keep no per-pawn state. Standalone like the rest of this folder: no Entad types.
//
// Cost: a worker only runs for a pawn that is not a colonist, holds an ability with an extension, that ability is
// ready (CanCast, reserve, gap) and a host hook claims it, at most once per 30 ticks per pawn and then not again for
// the global gap after a cast. Each worker notes its own cost below; k = hostile pawns on the map.
namespace EntadFramework.AbilityAI
{
    public abstract class AbilityAIWorker
    {
        public AbilityAIExtension ext;

        protected virtual AbilityAICombat DefaultCombat => AbilityAICombat.InCombat;

        public AbilityAICombat Combat => ext.combat == AbilityAICombat.Default ? DefaultCombat : ext.combat;

        /// <summary>A target worth casting at now, or false. The driver runs AbilityAIDriver.Valid on the result
        /// once more, so a worker only needs to call it where it is choosing between candidates.</summary>
        public abstract bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest);
    }

    public static class AbilityAIUtility
    {
        /// <summary>The extension's radius, else the first effect comp that states one.</summary>
        public static float Radius(Ability ability, AbilityAIExtension ext)
        {
            if (ext.radius >= 0f) return ext.radius;
            var comps = ability.def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is IAbilityAIInfo info) return info.AIRadius;
                    if (comps[i] is CompProperties_AbilitySmokepop smoke) return smoke.smokeRadius;
                }
            return 0f;
        }

        /// <summary>The extension's hediff, else the first effect comp that gives one.</summary>
        public static HediffDef Hediff(Ability ability, AbilityAIExtension ext)
        {
            if (ext.hediff != null) return ext.hediff;
            var comps = ability.def.comps;
            if (comps != null)
                for (int i = 0; i < comps.Count; i++)
                {
                    if (comps[i] is IAbilityAIInfo info && info.AIHediff != null) return info.AIHediff;
                    if (comps[i] is CompProperties_AbilityGiveHediff give && give.hediffDef != null) return give.hediffDef;
                }
            return null;
        }

        /// <summary>How far the ability reaches: its verb's range, capped by the extension's maxRange.</summary>
        public static float Range(Ability ability, AbilityAIExtension ext)
        {
            float r = ability.verb.EffectiveRange;
            return ext.maxRange >= 0f ? Mathf.Min(r, ext.maxRange) : r;
        }

        /// <summary>Cast on the caster only: no range, or an untargetable verb that may target itself.</summary>
        public static bool IsSelfCast(Ability ability)
        {
            var vp = ability.def.verbProperties;
            if (vp == null || !ability.def.targetRequired) return true;
            return ability.verb.EffectiveRange <= 0f || (!vp.targetable && vp.targetParams?.canTargetSelf == true);
        }

        public static bool Has(Pawn pawn, HediffDef hediff) => hediff != null && pawn.health?.hediffSet?.HasHediff(hediff) == true;

        /// <summary>The pawn itself if the ability accepts it, else the cell it stands on.</summary>
        public static LocalTargetInfo PawnOrCell(Ability ability, Pawn target)
        {
            var pawnTarget = new LocalTargetInfo(target);
            if (AbilityAIDriver.Valid(ability, pawnTarget)) return pawnTarget;
            var cellTarget = new LocalTargetInfo(target.Position);
            return AbilityAIDriver.Valid(ability, cellTarget) ? cellTarget : LocalTargetInfo.Invalid;
        }

        /// <summary>Running away: panicking, on a flee job, or leaving the map while fighting.</summary>
        public static bool IsFleeing(AbilityAIContext ctx)
        {
            Pawn pawn = ctx.pawn;
            if (pawn.MentalStateDef == MentalStateDefOf.PanicFlee) return true;
            var job = pawn.CurJob;
            if (job == null) return false;
            if (job.def == JobDefOf.Flee || job.def == JobDefOf.FleeAndCower) return true;
            return job.exitMapOnArrival && ctx.InCombat;
        }

        /// <summary>The nearest enemy within 1.5 tiles that is fighting the pawn in melee.</summary>
        public static bool InMelee(AbilityAIContext ctx)
        {
            Pawn threat = ctx.pawn.mindState?.meleeThreat;
            if (threat == null || !threat.Spawned || threat.Downed || threat.Map != ctx.map) return false;
            return ctx.tick - ctx.pawn.mindState.lastMeleeThreatHarmTick < AbilityAIContext.RecentTicks
                && threat.Position.InHorDistOf(ctx.pawn.Position, 1.5f);
        }
    }

    /// <summary>
    /// AoE hostile: an area ability at the spot that catches the most enemies (Flashbang, Artillery).
    /// Each enemy in range is a candidate centre, scored by enemies within the radius of it; at least minTargets,
    /// between minRange and the ability's range, no ally inside with avoidAllies. The best five are checked against
    /// the verb (line of sight). Cost: k² distance checks, then up to 10 target validations.
    /// </summary>
    public class AbilityAIWorker_AoEHostile : AbilityAIWorker
    {
        private struct Centre { public Pawn pawn; public int count; public float distSq; }
        private static readonly List<Centre> centres = new List<Centre>();
        private const int MaxTries = 5;

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            var enemies = ctx.Enemies;
            if (enemies.Count < ext.minTargets) return false;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range, minSq = ext.minRange * ext.minRange;
            IntVec3 from = ctx.pawn.Position;

            centres.Clear();
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                float d = (e.Position - from).LengthHorizontalSquared;
                if (d > maxSq || d < minSq) continue;
                int count = 0;
                for (int j = 0; j < enemies.Count; j++)
                    if (j == i || (enemies[j].Position - e.Position).LengthHorizontalSquared <= r2) count++;
                if (count < ext.minTargets) continue;
                if (ext.avoidAllies && radius > 0f && AnyAllyNear(ctx, e.Position, r2)) continue;
                centres.Add(new Centre { pawn = e, count = count, distSq = d });
            }
            // Most enemies caught first, then the nearest (more likely to be in sight)
            centres.Sort((a, b) => a.count != b.count ? b.count.CompareTo(a.count) : a.distSq.CompareTo(b.distSq));
            for (int i = 0; i < centres.Count && i < MaxTries; i++)
            {
                target = AbilityAIUtility.PawnOrCell(ability, centres[i].pawn);
                if (target.IsValid) break;
            }
            centres.Clear();
            return target.IsValid;
        }

        private static bool AnyAllyNear(AbilityAIContext ctx, IntVec3 c, float r2)
        {
            var allies = ctx.Allies;
            for (int i = 0; i < allies.Count; i++)
                if ((allies[i].Position - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }
    }

    /// <summary>
    /// AoE friendly: an area buff over allies (Battle Cry). Self-cast abilities centre on the caster; targeted ones
    /// on whichever ally gathers the most. Counts allies (the caster included) within the radius that don't yet have
    /// the ability's hediff; casts when at least minTargets would gain it. Cost: allies² distance checks at most.
    /// </summary>
    public class AbilityAIWorker_AoEFriendly : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            float radius = AbilityAIUtility.Radius(ability, ext);
            float r2 = radius * radius;
            HediffDef hediff = AbilityAIUtility.Hediff(ability, ext);
            var allies = ctx.Allies;

            if (AbilityAIUtility.IsSelfCast(ability))
            {
                if (Count(allies, ctx.pawn.Position, r2, hediff) < ext.minTargets) return false;
                target = ctx.pawn;
                return true;
            }

            float range = AbilityAIUtility.Range(ability, ext);
            float maxSq = range * range;
            Pawn best = null;
            int bestCount = ext.minTargets - 1;
            for (int i = 0; i < allies.Count; i++)
            {
                if ((allies[i].Position - ctx.pawn.Position).LengthHorizontalSquared > maxSq) continue;
                int n = Count(allies, allies[i].Position, r2, hediff);
                if (n > bestCount) { bestCount = n; best = allies[i]; }
            }
            if (best == null) return false;
            target = AbilityAIUtility.PawnOrCell(ability, best);
            return target.IsValid;
        }

        private static int Count(List<Pawn> allies, IntVec3 c, float r2, HediffDef hediff)
        {
            int n = 0;
            for (int i = 0; i < allies.Count; i++)
                if ((allies[i].Position - c).LengthHorizontalSquared <= r2 && !AbilityAIUtility.Has(allies[i], hediff)) n++;
            return n;
        }
    }

    /// <summary>
    /// Self buff: cast on the caster while an enemy is within maxRange (default 30) and the caster doesn't already
    /// have the ability's hediff. Cost: k distance checks.
    /// </summary>
    public class AbilityAIWorker_SelfBuff : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            if (AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;
            float reach = ext.maxRange >= 0f ? ext.maxRange : 30f;
            if (ctx.NearestEnemy(out float d) == null || d > reach * reach) return false;
            target = ctx.pawn;
            return true;
        }
    }

    /// <summary>
    /// Movement: close on the enemy or get away from it. Self-cast abilities (Sprint) are cast when either applies;
    /// targeted ones (Blink) pick a cell.
    /// Gap-close (melee pawns only): the current enemy target is further than minGapDistance (default 6) and within
    /// maxRange (default 40; a targeted ability also needs it in reach); a targeted ability lands next to it.
    /// Escape: health under healthThreshold, fleeing, or a ranged pawn caught in melee, with an enemy within 12 tiles;
    /// a targeted ability tries 16 directions at full and 60% range for the cell furthest from every enemy, and only
    /// goes if that gains at least 4 tiles. Cost: escape is 32 × k distance checks, then up to 6 validations;
    /// gap-close with a targeted ability up to 8 validations, only while the enemy is within its reach.
    /// </summary>
    public class AbilityAIWorker_Movement : AbilityAIWorker
    {
        private const float EscapeDangerRange = 12f;
        private const float EscapeMinGain = 4f;
        private const int Directions = 16;
        private const int MaxTries = 6;

        private struct Spot { public IntVec3 cell; public float score; }
        private static readonly List<Spot> spots = new List<Spot>();

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            bool self = AbilityAIUtility.IsSelfCast(ability);
            if (self && AbilityAIUtility.Has(ctx.pawn, AbilityAIUtility.Hediff(ability, ext))) return false;

            if ((ext.modes & AbilityAIMoveModes.Escape) != 0 && WantsEscape(ctx))
            {
                if (self) { target = ctx.pawn; return true; }
                if (TryEscapeCell(ctx, ability, out IntVec3 cell)) { target = cell; return true; }
            }
            if ((ext.modes & AbilityAIMoveModes.GapClose) != 0 && !ctx.UsesRanged && ctx.EnemyTarget is Pawn enemy)
            {
                float d = (enemy.Position - ctx.pawn.Position).LengthHorizontal;
                float reach = ext.maxRange >= 0f ? ext.maxRange : 40f;
                if (d < ext.minGapDistance || d > reach) return false;
                if (self) { target = ctx.pawn; return true; }
                // Even the cell next to the enemy nearest the caster is out of reach: skip the eight validations
                if (d > AbilityAIUtility.Range(ability, ext) + 1.5f) return false;
                if (TryCellNear(ctx, ability, enemy, out IntVec3 cell)) { target = cell; return true; }
            }
            return false;
        }

        private bool WantsEscape(AbilityAIContext ctx)
        {
            bool reason = ctx.HealthFraction < ext.healthThreshold || AbilityAIUtility.IsFleeing(ctx)
                || (ctx.UsesRanged && AbilityAIUtility.InMelee(ctx));
            if (!reason) return false;
            return ctx.NearestEnemy(out float d) != null && d <= EscapeDangerRange * EscapeDangerRange;
        }

        // The standable cell next to the enemy nearest the caster that the ability accepts
        private static bool TryCellNear(AbilityAIContext ctx, Ability ability, Pawn enemy, out IntVec3 best)
        {
            best = IntVec3.Invalid;
            float bestD = float.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 c = enemy.Position + GenAdj.AdjacentCells[i];
                if (!c.InBounds(ctx.map)) continue;
                float d = (c - ctx.pawn.Position).LengthHorizontalSquared;
                if (d >= bestD || !AbilityAIDriver.Valid(ability, c)) continue;
                bestD = d;
                best = c;
            }
            return best.IsValid;
        }

        private bool TryEscapeCell(AbilityAIContext ctx, Ability ability, out IntVec3 result)
        {
            result = IntVec3.Invalid;
            var enemies = ctx.Enemies;
            if (enemies.Count == 0) return false;
            float range = AbilityAIUtility.Range(ability, ext);
            if (range < 2f) return false;
            float now = MinEnemyDist(enemies, ctx.pawn.Position);

            spots.Clear();
            for (int i = 0; i < Directions; i++)
            {
                float angle = i * (360f / Directions);
                Vector3 dir = Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward;
                for (int s = 0; s < 2; s++)
                {
                    float dist = s == 0 ? range - 0.5f : range * 0.6f;
                    IntVec3 c = ctx.pawn.Position + new IntVec3(Mathf.RoundToInt(dir.x * dist), 0, Mathf.RoundToInt(dir.z * dist));
                    if (!c.InBounds(ctx.map)) continue;
                    float score = MinEnemyDist(enemies, c);
                    if (score - now < EscapeMinGain) continue;
                    spots.Add(new Spot { cell = c, score = score });
                }
            }
            spots.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < spots.Count && i < MaxTries; i++)
                if (AbilityAIDriver.Valid(ability, spots[i].cell)) { result = spots[i].cell; break; }
            spots.Clear();
            return result.IsValid;
        }

        private static float MinEnemyDist(List<Pawn> enemies, IntVec3 c)
        {
            float best = float.MaxValue;
            for (int i = 0; i < enemies.Count; i++)
            {
                float d = (enemies[i].Position - c).LengthHorizontalSquared;
                if (d < best) best = d;
            }
            return Mathf.Sqrt(best);
        }
    }

    /// <summary>
    /// Cover: when the caster is under fire, put something between it and the shooters (Smokescreen; Wallraise
    /// later). Under fire = at least minThreats ranged enemies aiming at the caster, counting being shot in the last
    /// 10 seconds as one. Placed on the caster (offsetTowardThreat 0) or that many tiles toward the nearest threat.
    /// Smoke isn't cast while the caster already stands in smoke. Cost: k checks of each enemy's target.
    /// </summary>
    public class AbilityAIWorker_Cover : AbilityAIWorker
    {
        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Pawn pawn = ctx.pawn;
            if (IsSmoke(ability) && pawn.Position.AnyGas(ctx.map, GasType.BlindSmoke)) return false;

            int threats = 0;
            Pawn nearest = null;
            float nearestD = float.MaxValue;
            var mind = pawn.mindState;
            if (mind != null && mind.lastRangedHarmTick > 0 && ctx.tick - mind.lastRangedHarmTick < AbilityAIContext.RecentTicks) threats++;
            var enemies = ctx.Enemies;
            for (int i = 0; i < enemies.Count; i++)
            {
                Pawn e = enemies[i];
                if (!AimsAt(e, pawn)) continue;
                threats++;
                float d = (e.Position - pawn.Position).LengthHorizontalSquared;
                if (d < nearestD) { nearestD = d; nearest = e; }
            }
            if (threats < ext.minThreats) return false;

            if (ext.offsetTowardThreat <= 0f || nearest == null)
            {
                target = pawn.Position;
                return true;
            }
            Vector3 dir = (nearest.Position - pawn.Position).ToVector3().normalized;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            // Straight toward the threat first, then a tile to either side
            for (int s = 0; s < 3; s++)
            {
                Vector3 off = dir * ext.offsetTowardThreat + side * (s == 0 ? 0f : s == 1 ? 1f : -1f);
                IntVec3 c = pawn.Position + new IntVec3(Mathf.RoundToInt(off.x), 0, Mathf.RoundToInt(off.z));
                if (c.InBounds(ctx.map) && AbilityAIDriver.Valid(ability, c)) { target = c; return true; }
            }
            return false;
        }

        // A ranged enemy (mechs and animals by their own verbs) whose fight target or current attack job is the caster
        private static bool AimsAt(Pawn e, Pawn pawn)
        {
            if (!AbilityAIContext.FightsAtRange(e)) return false;
            if (e.mindState?.enemyTarget == pawn) return true;
            var job = e.CurJob;
            return job != null && job.targetA.Thing == pawn;
        }

        private static bool IsSmoke(Ability ability)
        {
            var comps = ability.def.comps;
            if (comps != null) for (int i = 0; i < comps.Count; i++) if (comps[i] is CompProperties_AbilitySmokepop) return true;
            return false;
        }
    }

    /// <summary>
    /// Recovery: out of combat, cast on the caster when a need is low (Second Wind). need (default rest) under
    /// needThreshold (default 30%). Cost: one need lookup.
    /// </summary>
    public class AbilityAIWorker_Recovery : AbilityAIWorker
    {
        protected override AbilityAICombat DefaultCombat => AbilityAICombat.OutOfCombat;

        public override bool TryFindTarget(AbilityAIContext ctx, Ability ability, out LocalTargetInfo target, out LocalTargetInfo dest)
        {
            target = dest = LocalTargetInfo.Invalid;
            Need need = ctx.pawn.needs?.TryGetNeed(ext.need ?? NeedDefOf.Rest);
            if (need == null || need.CurLevelPercentage >= ext.needThreshold) return false;
            target = ctx.pawn;
            return true;
        }
    }
}
