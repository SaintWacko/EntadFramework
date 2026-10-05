using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // Reloadable entad abilities. Vanilla's reload system (IReloadableComp, JobDriver_Reload, the reload float menu)
    // only looks for its own two comp classes, and its float menu assumes the reloadable is a ThingComp, so entads
    // get their own small copies of each piece: a job, a float menu provider and a hook on the auto-reload job giver.
    //
    // While the gear is worn or wielded the charges are the pawn's Ability.RemainingCharges; otherwise they are stored
    // on the trait (AppliedEntadTrait.StoredCharges). EntadAbilities moves them across on equip and unequip.
    public static class EntadReload
    {
        public struct Slot
        {
            public CompEntad comp;
            public AppliedEntadTrait trait;
            public int index;

            public AbilityDef Ability => trait.def.abilities[index];
            public ThingDef Ammo => trait.def.abilityAmmo;
            public int Max => trait.def.abilityCharges;
            public int PerCharge => trait.def.abilityAmmoPerCharge;
        }

        private static bool? anyReloadable;

        // Def data: with no reloadable trait loaded, every hook here is one branch
        public static bool AnyReloadable =>
            anyReloadable ?? (anyReloadable = DefDatabase<EntadTraitDef>.AllDefsListForReading.Any(d => d.IsReloadable)).Value;

        public static IEnumerable<Slot> SlotsOf(CompEntad comp)
        {
            if (comp == null) yield break;
            foreach (var m in comp.activeTraits)
            {
                if (!m.def.IsReloadable) continue;
                for (int i = 0; i < m.def.abilities.Count; i++)
                    yield return new Slot { comp = comp, trait = m, index = i };
            }
        }

        // Reloadable slots on the gear a pawn is using whose traits work for that pawn
        public static IEnumerable<Slot> SlotsFor(Pawn pawn)
        {
            if (pawn == null) yield break;
            if (pawn.equipment != null)
                foreach (var eq in pawn.equipment.AllEquipmentListForReading)
                    foreach (var s in ActiveSlots(eq, pawn)) yield return s;
            if (pawn.apparel != null)
                foreach (var ap in pawn.apparel.WornApparel)
                    foreach (var s in ActiveSlots(ap, pawn)) yield return s;
        }

        private static IEnumerable<Slot> ActiveSlots(Thing gear, Pawn pawn)
        {
            var comp = gear.TryGetComp<CompEntad>();
            if (comp == null || !comp.ActiveFor(pawn)) return Enumerable.Empty<Slot>();
            return SlotsOf(comp);
        }

        private static Ability HeldAbility(Slot s)
        {
            Pawn holder = s.comp.Holder;
            return holder?.abilities?.GetAbility(s.Ability);
        }

        public static int Charges(Slot s) => HeldAbility(s)?.RemainingCharges ?? s.trait.StoredCharges(s.index);

        public static void SetCharges(Slot s, int c)
        {
            c = Mathf.Clamp(c, 0, s.Max);
            var held = HeldAbility(s);
            if (held != null) held.RemainingCharges = c;
            s.trait.SetStoredCharges(s.index, c);
        }

        // forced: the player asked, so any missing charge counts; otherwise only an empty ability is worth a trip
        public static bool NeedsReload(Slot s, bool forced) => forced ? Charges(s) < s.Max : Charges(s) == 0;

        public static int MaxAmmoNeeded(Slot s) => (s.Max - Charges(s)) * s.PerCharge;

        // Loads as many charges as the carried stack pays for into every slot on the gear that takes this ammo
        public static void ReloadFrom(CompEntad comp, Thing ammo)
        {
            if (ammo == null || ammo.Destroyed) return;
            bool any = false;
            foreach (var s in SlotsOf(comp).ToList())
            {
                if (s.Ammo != ammo.def || !NeedsReload(s, true)) continue;
                int n = Mathf.Min(ammo.stackCount / s.PerCharge, s.Max - Charges(s));
                if (n <= 0) continue;
                ammo.SplitOff(n * s.PerCharge).Destroy();
                SetCharges(s, Charges(s) + n);
                any = true;
                if (ammo.Destroyed) break;
            }
            if (any) SoundDefOf.Standard_Reload?.PlayOneShot(new TargetInfo(comp.parent.PositionHeld, comp.parent.MapHeld));
        }

        public static List<Thing> FindAmmo(Pawn pawn, IntVec3 root, Slot s, bool forced)
        {
            int max = MaxAmmoNeeded(s);
            if (max <= 0) return null;
            return RefuelWorkGiverUtility.FindEnoughReservableThings(pawn, root, new IntRange(s.PerCharge, max), t => t.def == s.Ammo);
        }

        public static Job MakeJob(Slot s, List<Thing> ammo)
        {
            Job job = JobMaker.MakeJob(EntadJobDefOf.Entad_Reload, s.comp.parent);
            job.targetQueueB = ammo.Select(t => new LocalTargetInfo(t)).ToList();
            job.count = Mathf.Min(ammo.Sum(t => t.stackCount), MaxAmmoNeeded(s));
            return job;
        }
    }

    // Vanilla JobDriver_Reload with the entad slots in place of IReloadableComp
    public class JobDriver_EntadReload : JobDriver
    {
        private Thing Gear => job.GetTarget(TargetIndex.A).Thing;
        private CompEntad Comp => Gear?.TryGetComp<CompEntad>();

        private bool AnyNeeds() => EntadReload.SlotsOf(Comp).Any(s => EntadReload.NeedsReload(s, true));

        private int ReloadTicks() => EntadReload.SlotsOf(Comp).Select(s => s.trait.def.abilityReloadTicks).DefaultIfEmpty(60).Max();

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.B), job);
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => Comp == null || Comp.Holder != pawn || !Comp.ActiveFor(pawn));
            this.FailOn(() => !AnyNeeds());
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnIncapable(PawnCapacityDefOf.Manipulation);
            Toil getNext = Toils_General.Label();
            yield return getNext;
            foreach (Toil t in ReloadAsMuchAsPossible()) yield return t;
            yield return Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, false, true).FailOnDestroyedNullOrForbidden(TargetIndex.B);
            yield return Toils_Jump.JumpIf(getNext, () => !job.GetTargetQueue(TargetIndex.B).NullOrEmpty());
            foreach (Toil t in ReloadAsMuchAsPossible()) yield return t;
            Toil drop = ToilMaker.MakeToil("EntadReloadDrop");
            drop.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried != null && !carried.Destroyed) pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            };
            drop.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return drop;
        }

        private IEnumerable<Toil> ReloadAsMuchAsPossible()
        {
            Toil done = Toils_General.Label();
            yield return Toils_Jump.JumpIf(done, () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                return carried == null || !EntadReload.SlotsOf(Comp).Any(s => s.Ammo == carried.def && carried.stackCount >= s.PerCharge && EntadReload.NeedsReload(s, true));
            });
            yield return Toils_General.Wait(ReloadTicks()).WithProgressBarToilDelay(TargetIndex.A);
            Toil load = ToilMaker.MakeToil("EntadReloadLoad");
            load.initAction = () => EntadReload.ReloadFrom(Comp, pawn.carryTracker.CarriedThing);
            load.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return load;
            yield return done;
        }
    }

    // Right-click the ammo: "Reload <gear> with <ammo> (1 / 3)", mirroring vanilla's reload option
    public class FloatMenuOptionProvider_EntadReload : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => true;

        public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
        {
            if (!EntadReload.AnyReloadable) yield break;
            Pawn pawn = context.FirstSelectedPawn;
            foreach (var s in EntadReload.SlotsFor(pawn).ToList())
            {
                if (s.Ammo != clickedThing.def) continue;
                string text = "Reload".Translate(s.comp.parent.Named("GEAR"), NamedArgumentUtility.Named(s.Ammo, "AMMO"))
                    + " (" + EntadReload.Charges(s) + " / " + s.Max + ")";
                if (!pawn.CanReach(clickedThing, PathEndMode.ClosestTouch, Danger.Deadly))
                {
                    yield return new FloatMenuOption(text + ": " + "NoPath".Translate().CapitalizeFirst(), null);
                    continue;
                }
                if (!EntadReload.NeedsReload(s, true))
                {
                    yield return new FloatMenuOption(text + ": " + "ReloadFull".Translate(), null);
                    continue;
                }
                var ammo = EntadReload.FindAmmo(pawn, clickedThing.Position, s, true);
                if (ammo == null)
                {
                    yield return new FloatMenuOption(text + ": " + "ReloadNotEnough".Translate(), null);
                    continue;
                }
                if (pawn.carryTracker.AvailableStackSpace(s.Ammo) < s.PerCharge)
                {
                    yield return new FloatMenuOption(text + ": " + "ReloadCannotCarryEnough".Translate(NamedArgumentUtility.Named(s.Ammo, "AMMO")), null);
                    continue;
                }
                var slot = s;
                yield return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(text,
                    () => pawn.jobs.TryTakeOrderedJob(EntadReload.MakeJob(slot, ammo), JobTag.Misc)), pawn, clickedThing);
            }
        }
    }

    // Auto-reload: when vanilla finds nothing of its own to reload, an entad ability that has run dry is next.
    // JobGiver_Reload sits in the colonist think tree, so this runs on think-tree passes, not every tick.
    [HarmonyPatch(typeof(JobGiver_Reload), "TryGiveJob")]
    public static class Patch_JobGiver_Reload_Entad
    {
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            if (__result != null || !EntadReload.AnyReloadable) return;
            if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return;
            foreach (var s in EntadReload.SlotsFor(pawn))
            {
                if (!EntadReload.NeedsReload(s, false)) continue;
                if (pawn.carryTracker.AvailableStackSpace(s.Ammo) < s.PerCharge) continue;
                var ammo = EntadReload.FindAmmo(pawn, pawn.Position, s, false);
                if (ammo.NullOrEmpty()) continue;
                __result = EntadReload.MakeJob(s, ammo);
                return;
            }
        }
    }
}
