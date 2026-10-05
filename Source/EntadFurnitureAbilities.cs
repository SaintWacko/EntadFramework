using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;
using Verse.AI;

namespace EntadFramework
{
    // Furniture can't be equipped, so its abilities are offered in the right-click menu instead.
    // The pawn walks to the furniture and performs the ability; the cooldown belongs to the furniture.
    public static class EntadFurnitureAbilities
    {
        public static IEnumerable<KeyValuePair<AppliedEntadTrait, int>> Entries(CompEntad comp)
        {
            foreach (var m in comp.activeTraits)
            {
                if (m.def.abilities == null) continue;
                for (int i = 0; i < m.def.abilities.Count; i++)
                    yield return new KeyValuePair<AppliedEntadTrait, int>(m, i);
            }
        }

        public static bool TryGet(CompEntad comp, int flatIndex, out AppliedEntadTrait trait, out int abilityIndex)
        {
            int n = 0;
            foreach (var e in Entries(comp))
            {
                if (n++ == flatIndex) { trait = e.Key; abilityIndex = e.Value; return true; }
            }
            trait = null;
            abilityIndex = -1;
            return false;
        }

        public static void Perform(Pawn pawn, CompEntad comp, int flatIndex, LocalTargetInfo target)
        {
            if (!TryGet(comp, flatIndex, out var trait, out int idx)) return;
            AbilityDef def = trait.def.abilities[idx];
            int maxCharges = trait.def.abilityCharges;
            int cooldown = Cooldown(trait, def);
            if (maxCharges > 0 ? trait.ChargesLeft(idx, maxCharges, cooldown) <= 0 : Find.TickManager.TicksGame < trait.AbilityReadyTick(idx)) return;

            Ability ability = AbilityUtility.MakeAbility(def, pawn);
            LocalTargetInfo t = target.IsValid ? target : (LocalTargetInfo)pawn;
            ability.Activate(t, t);
            trait.Reveal(EntadEffectKind.Ability);

            if (maxCharges > 0) trait.UseCharge(idx, maxCharges, cooldown);
            else trait.SetAbilityReadyTick(idx, Find.TickManager.TicksGame + def.cooldownTicksRange.RandomInRange);
        }

        // Ticks a pawn works at the furniture before the ability goes off
        public static PathEndMode PathMode(ThingDef def) => def.hasInteractionCell ? PathEndMode.InteractionCell : PathEndMode.Touch;

        public static int CastTicks(AppliedEntadTrait trait, AbilityDef def)
        {
            if (trait.def.abilityCastTicks > 0) return trait.def.abilityCastTicks;
            return def.verbProperties != null ? Mathf.RoundToInt(def.verbProperties.warmupTime * 60f) : 0;
        }

        public static int Cooldown(AppliedEntadTrait trait, AbilityDef def)
        {
            int c = trait.def.abilityCooldownTicks > 0 ? trait.def.abilityCooldownTicks : (int)def.cooldownTicksRange.Average;
            return System.Math.Max(1, c);
        }
    }

    public class JobDriver_UseEntadFurniture : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, EntadFurnitureAbilities.PathMode(job.targetA.Thing.def));

            var comp0 = job.targetA.Thing?.TryGetComp<CompEntad>();
            int castTicks = comp0 != null && EntadFurnitureAbilities.TryGet(comp0, job.count, out var trait, out int idx)
                ? EntadFurnitureAbilities.CastTicks(trait, trait.def.abilities[idx]) : 0;
            if (castTicks > 0)
            {
                Toil wait = Toils_General.Wait(castTicks).WithProgressBarToilDelay(TargetIndex.A);
                wait.FailOnDespawnedOrNull(TargetIndex.A);
                yield return wait;
            }

            Toil use = ToilMaker.MakeToil("UseEntadFurniture");
            use.initAction = () =>
            {
                var comp = job.targetA.Thing?.TryGetComp<CompEntad>();
                if (comp != null) EntadFurnitureAbilities.Perform(pawn, comp, job.count, job.targetB);
            };
            use.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return use;
        }
    }

    public partial class CompEntad
    {
        public override IEnumerable<FloatMenuOption> CompFloatMenuOptions(Pawn selPawn)
        {
            if (parent.def.building == null || !parent.Spawned || activeTraits.NullOrEmpty()) yield break;

            int flat = 0;
            foreach (var entry in EntadFurnitureAbilities.Entries(this))
            {
                int flatIndex = flat++;
                AppliedEntadTrait trait = entry.Key;
                int idx = entry.Value;
                AbilityDef def = trait.def.abilities[idx];
                string label = "Activate " + (!trait.IsRevealed(EntadEffectKind.Ability) ? "???" : def.LabelCap.ToString()) + " (" + parent.LabelNoCount + ")";

                int maxCharges = trait.def.abilityCharges;
                int ready = trait.AbilityReadyTick(idx);
                int charges = maxCharges > 0 ? trait.ChargesLeft(idx, maxCharges, EntadFurnitureAbilities.Cooldown(trait, def)) : 1;
                if (maxCharges > 0) ready = charges > 0 ? 0 : trait.AbilityReadyTick(idx);
                if (maxCharges > 0 && trait.IsRevealed(EntadEffectKind.Ability)) label += " [" + charges + "/" + maxCharges + "]";
                if (selPawn.Downed || !selPawn.Spawned || !selPawn.IsColonistPlayerControlled)
                    continue;
                if (!selPawn.CanReach(parent, EntadFurnitureAbilities.PathMode(parent.def), Danger.Deadly))
                {
                    yield return new FloatMenuOption(label + " (" + "NoPath".Translate() + ")", null);
                    continue;
                }
                if (Find.TickManager.TicksGame < ready)
                {
                    yield return new FloatMenuOption(label + " (on cooldown: " + (ready - Find.TickManager.TicksGame).ToStringTicksToPeriod() + ")", null);
                    continue;
                }

                Pawn pawn = selPawn;
                yield return new FloatMenuOption(label, () =>
                {
                    if (def.targetRequired)
                    {
                        var parms = new TargetingParameters { canTargetPawns = true, canTargetSelf = true, canTargetLocations = true, canTargetBuildings = false };
                        Find.Targeter.BeginTargeting(parms, target => StartJob(pawn, flatIndex, target));
                    }
                    else StartJob(pawn, flatIndex, LocalTargetInfo.Invalid);
                });
            }
        }

        private void StartJob(Pawn pawn, int flatIndex, LocalTargetInfo target)
        {
            Job job = JobMaker.MakeJob(EntadJobDefOf.Entad_UseFurnitureAbility, parent, target);
            job.count = flatIndex;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }

    [DefOf]
    public static class EntadJobDefOf
    {
        public static JobDef Entad_UseFurnitureAbility;

        static EntadJobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadJobDefOf)); }
    }
}
