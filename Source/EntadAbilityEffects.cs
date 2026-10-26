using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace EntadFramework
{
    // Ability effects for entad abilities that need no DLC. Vanilla's own comps cover some (Smokescreen uses
    // CompAbilityEffect_Smokepop as it is), but the ones that are Core code still often reach for Royalty defs:
    // CompAbilityEffect_Teleport plays Psycast_Skip_Exit, which is null without Royalty and logs an error on every
    // cast. These two comps only touch Core defs, and optional ones only when present.

    // One pulse of effects: on the pawn the ability targets (the caster, for an untargeted ability), or with a radius,
    // on every pawn 'targets' picks around the target. Values are fixed per ability, unlike triggers, whose values
    // are rolled per item. Uses the trigger code's target rules and effects, so 'Allies' means the same thing in both.
    // Ability AI (an optional mod) reads radius, hediff and stunSeconds through a registration in the compat
    // assembly (Mods/AbilityAI/Source/EntadAbilityAICompat.cs). Nothing here may name an Ability AI type: the main
    // assembly has to load without it.
    public class CompProperties_AbilityEntadPulse : CompProperties_AbilityEffect
    {
        public float radius;
        public EntadTriggerTargets targets = EntadTriggerTargets.Self;
        public float heal;
        public float rest;
        public float food;
        public float joy;
        public HediffDef hediff;
        // In-game hours; 0 keeps the hediff's own disappearsAfterTicks
        public float hediffHours;
        public float stunSeconds;
        public FleckDef fleck;

        public CompProperties_AbilityEntadPulse() => compClass = typeof(CompAbilityEffect_EntadPulse);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (radius < 0f) yield return "radius can't be negative";
            if (targets == 0) yield return "targets needs at least one of Self, Allies, Enemies";
            if (hediffHours > 0f && hediff == null) yield return "hediffHours without a hediff";
        }
    }

    /// <summary>The verb for abilities cast on the user (Sprint, Battle Cry, Second Wind). Vanilla's
    /// Verb_CastAbility.CanHitTarget is true for ANY target at range 0, so vanilla's fight AI
    /// (JobGiver_AIFightEnemy.GetAbilityJob, first loop) hands the ability the enemy it is fighting. The cast then
    /// dies in Verb.TryStartCastOn: with a warmup, TryFindShootLineFromTo treats a range under 1.42 as melee and
    /// wants the target adjacent, so the job ends at once, the pawn thinks again, gets the same job, and spends the
    /// fight twitching in place without shooting. Seen in game without Ability AI (build 255). Accepting only the
    /// caster at range 0 makes that loop skip the ability and vanilla's third loop cast it on the caster
    /// (canTargetSelf). The player's gizmo and Ability AI both target the caster already.</summary>
    public class Verb_EntadSelfCast : Verb_CastAbility
    {
        public override bool CanHitTarget(LocalTargetInfo targ)
        {
            if (EffectiveRange <= 0f) return targ.Thing != null && targ.Thing == caster;
            return base.CanHitTarget(targ);
        }
    }

    public class CompAbilityEffect_EntadPulse : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadPulse Props => (CompProperties_AbilityEntadPulse)props;

        private static readonly List<Pawn> hit = new List<Pawn>();

        // Vanilla's fight AI casts a self buff whenever it is ready (GetAbilityJob's third loop asks nothing more), so
        // an NPC with two charges of Sprint would spend the second while the first still runs
        public override bool AICanTargetNow(LocalTargetInfo target)
        {
            if (Props.hediff != null && !parent.def.targetRequired && parent.pawn?.health.hediffSet.HasHediff(Props.hediff) == true) return false;
            return base.AICanTargetNow(target);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            Pawn caster = parent.pawn;
            // An untargeted ability is always about the caster. Verb_EntadSelfCast already keeps vanilla's fight AI
            // from handing it the enemy; this covers another mod's def that uses the pulse with plain
            // Verb_CastAbility at range 0. Before base.Apply, which uses the target for goodwill, clamor and sound.
            if (!parent.def.targetRequired) target = caster;
            base.Apply(target, dest);
            Map map = caster.MapHeld;
            if (Props.radius <= 0f)
            {
                if (target.Pawn != null) ApplyTo(target.Pawn, caster);
                else ApplyTo(caster, caster);
                return;
            }
            if (map == null) return;
            IntVec3 center = target.Cell;
            float r2 = Props.radius * Props.radius;
            // Once per cast, over the spawned pawns; casts are player or AI decisions, never per tick
            hit.Clear();
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn other = all[i];
                if (other.Dead || (other.Position - center).LengthHorizontalSquared > r2) continue;
                if (EntadTriggers.IsTarget(caster, other, Props.targets)) hit.Add(other);
            }
            if (Props.fleck != null && center.InBounds(map)) FleckMaker.Static(center, map, Props.fleck, Props.radius);
            var copy = hit.ToArray();
            hit.Clear();
            for (int i = 0; i < copy.Length; i++) ApplyTo(copy[i], caster);
        }

        private void ApplyTo(Pawn pawn, Pawn caster)
        {
            if (pawn.Dead || pawn.Destroyed) return;
            if (Props.heal > 0f) EntadTriggers.HealWorstFirst(pawn, Props.heal);
            EntadTriggers.Restore(pawn.needs?.rest, Props.rest);
            EntadTriggers.Restore(pawn.needs?.food, Props.food);
            EntadTriggers.Restore(pawn.needs?.joy, Props.joy);
            if (Props.hediff != null) EntadTriggers.AddOrExtend(pawn, Props.hediff, Props.hediffHours);
            if (Props.stunSeconds > 0f && pawn.Spawned && pawn.stances?.stunner != null)
                pawn.stances.stunner.StunFor(Mathf.RoundToInt(Props.stunSeconds * GenTicks.TicksPerRealSecond), caster);
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            // Not while the trait is hidden: the ring would give the ability away, as the cursor label would
            if (Props.radius > 0f && !Patch_Ability_GetGizmos_Hide.IsHidden(parent)) GenDraw.DrawRadiusRing(target.Cell, Props.radius);
        }
    }

    // A short-range self teleport to a cell the caster can see. Vanilla's skip needs a pawn target and plays a
    // Royalty sound; this moves the caster only, with the same Core skip effecters, and the exit sound only when
    // Royalty is loaded.
    public class CompProperties_AbilityEntadBlink : CompProperties_AbilityEffect
    {
        public CompProperties_AbilityEntadBlink() => compClass = typeof(CompAbilityEffect_EntadBlink);
    }

    public class CompAbilityEffect_EntadBlink : CompAbilityEffect
    {
        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn pawn = parent.pawn;
            Map map = pawn.Map;
            IntVec3 cell = target.Cell;
            // Checked again: something may have moved there during the warmup
            if (map == null || !CanLandAt(cell, map)) return;
            parent.AddEffecterToMaintain(EffecterDefOf.Skip_Entry.Spawn(pawn, map), pawn.Position, 60);
            parent.AddEffecterToMaintain(EffecterDefOf.Skip_Exit.Spawn(cell, map), cell, 60);
            SoundDefOf.Psycast_Skip_Entry?.PlayOneShot(new TargetInfo(pawn.Position, map));
            SoundDefOf.Psycast_Skip_Exit?.PlayOneShot(new TargetInfo(cell, map));
            // No unfogging needed: ValidJumpTarget refuses fogged cells
            pawn.Position = cell;
            pawn.Notify_Teleported();
        }

        private bool CanLandAt(IntVec3 cell, Map map)
        {
            Pawn pawn = parent.pawn;
            if (cell == pawn.Position || !JumpUtility.ValidJumpTarget(pawn, map, cell)) return false;
            // Another pawn standing there would end up sharing the cell
            var first = cell.GetFirstPawn(map);
            return first == null || first == pawn;
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Map map = parent.pawn.Map;
            if (map == null || !CanLandAt(target.Cell, map)) return false;
            return base.Valid(target, throwMessages);
        }
    }
}
