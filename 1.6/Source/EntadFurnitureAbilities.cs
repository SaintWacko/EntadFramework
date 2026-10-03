using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // Furniture can't be equipped, so its abilities are offered in the right-click menu instead.
    // The pawn walks to the furniture and performs the ability; the cooldown belongs to the furniture.
    public static class EntadFurnitureAbilities
    {
        public static IEnumerable<KeyValuePair<AppliedEntadModifier, int>> Entries(CompEntad comp)
        {
            foreach (var m in comp.activeModifiers)
            {
                if (m.def.abilities == null) continue;
                for (int i = 0; i < m.def.abilities.Count; i++)
                    yield return new KeyValuePair<AppliedEntadModifier, int>(m, i);
            }
        }

        public static bool TryGet(CompEntad comp, int flatIndex, out AppliedEntadModifier modifier, out int abilityIndex)
        {
            int n = 0;
            foreach (var e in Entries(comp))
            {
                if (n++ == flatIndex) { modifier = e.Key; abilityIndex = e.Value; return true; }
            }
            modifier = null;
            abilityIndex = -1;
            return false;
        }

        public static void Perform(Pawn pawn, CompEntad comp, int flatIndex, LocalTargetInfo target)
        {
            if (!TryGet(comp, flatIndex, out var modifier, out int idx)) return;
            if (Find.TickManager.TicksGame < modifier.AbilityReadyTick(idx)) return;

            AbilityDef def = modifier.def.abilities[idx];
            Ability ability = AbilityUtility.MakeAbility(def, pawn);
            LocalTargetInfo t = target.IsValid ? target : (LocalTargetInfo)pawn;
            ability.Activate(t, t);
            modifier.Reveal();

            int cooldown = def.cooldownTicksRange.RandomInRange;
            modifier.SetAbilityReadyTick(idx, Find.TickManager.TicksGame + cooldown);
        }
    }

    public class JobDriver_UseEntadFurniture : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        public override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.InteractionCell);

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
            if (parent.def.building == null || !parent.Spawned || activeModifiers.NullOrEmpty()) yield break;

            int flat = 0;
            foreach (var entry in EntadFurnitureAbilities.Entries(this))
            {
                int flatIndex = flat++;
                AppliedEntadModifier modifier = entry.Key;
                int idx = entry.Value;
                AbilityDef def = modifier.def.abilities[idx];
                string label = "Activate " + (modifier.IsHidden ? "???" : def.LabelCap.ToString()) + " (" + parent.LabelNoCount + ")";

                int ready = modifier.AbilityReadyTick(idx);
                if (selPawn.Downed || !selPawn.Spawned || !selPawn.IsColonistPlayerControlled)
                    continue;
                if (!selPawn.CanReach(parent, PathEndMode.InteractionCell, Danger.Deadly))
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
