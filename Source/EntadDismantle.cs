using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace EntadFramework
{
    // Dismantling (build 152): a researcher takes an entad apart for insight, a colony-wide pool of points that other
    // mods spend (Dungeons on scouting a dungeon, a forge mod on making or changing entads). The framework itself
    // only fills the pool and tells listeners what was destroyed. Dismantling always destroys the entad: designate
    // it with the gizmo, and a pawn with Research enabled walks over and does the work.

    /// <summary>One trait of a dismantled entad, as it was on the item.</summary>
    public struct EntadTraitRecord
    {
        public EntadTraitDef def;
        public EntadRarity rarity;

        public EntadTraitRecord(EntadTraitDef def, EntadRarity rarity) { this.def = def; this.rarity = rarity; }
    }

    /// <summary>What <see cref="EntadApi.EntadDismantled"/> reports. Raised just before the entad is destroyed:
    /// <see cref="thing"/> is still intact while the handlers run, so a handler can read anything off it, but it is
    /// destroyed straight after and must not be kept.</summary>
    public class EntadDismantleResult
    {
        public Thing thing;
        public ThingDef def;
        public ThingDef stuff;
        public QualityCategory? quality;
        /// <summary>Every trait on the item, hidden or not, at the rarity it had.</summary>
        public List<EntadTraitRecord> traits;
        /// <summary>Insight added to the colony's pool (already added when handlers run).</summary>
        public float insight;
        /// <summary>The pawn who did it; null when a mod dismantled it from code.</summary>
        public Pawn pawn;
        public Map map;
    }

    // The colony's insight pool. Saved with the game.
    public class EntadInsight : GameComponent
    {
        private float points;

        public EntadInsight(Game game) { }

        public static EntadInsight Instance => Current.Game?.GetComponent<EntadInsight>();

        public float Points => points;

        public void Add(float amount) => points = Mathf.Max(0f, points + amount);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref points, "points", 0f);
        }
    }

    public static partial class EntadApi
    {
        /// <summary>Raised when an entad is dismantled, after its insight is added to the pool and just before the
        /// item is destroyed. Handlers run one by one; an exception in one is logged and the rest still run.</summary>
        public static event Action<EntadDismantleResult> EntadDismantled;

        /// <summary>The colony's insight: points gained by dismantling entads, for other mods to spend. 0 outside a game.</summary>
        public static float Insight => EntadInsight.Instance?.Points ?? 0f;

        /// <summary>Adds (or, with a negative amount, removes) insight. The pool never goes below 0.</summary>
        public static void AddInsight(float amount) => EntadInsight.Instance?.Add(amount);

        /// <summary>Spends <paramref name="amount"/> insight if the colony has that much.</summary>
        /// <returns>False, spending nothing, when there isn't enough or no game is running.</returns>
        public static bool TrySpendInsight(float amount)
        {
            var pool = EntadInsight.Instance;
            if (pool == null || amount < 0f || pool.Points + 0.0001f < amount) return false;
            pool.Add(-amount);
            return true;
        }

        /// <summary>The insight dismantling <paramref name="thing"/> would give: its traits' generation points at
        /// their rarities, drawbacks included, never below 0. Binding makes no difference.</summary>
        public static float InsightFor(Thing thing)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty()) return 0f;
            float total = 0f;
            foreach (var m in comp.activeTraits) total += m.def.PointsAt(m.Rarity);
            return Mathf.Max(0f, total);
        }

        /// <summary>Dismantles <paramref name="thing"/> at once: adds its insight to the pool, raises
        /// <see cref="EntadDismantled"/> and destroys it (a portable space inside spills as usual). Works on a held
        /// item too. <paramref name="pawn"/> is only reported to listeners and named in the message.</summary>
        /// <returns>What was dismantled, or null when the thing is not an entad with traits.</returns>
        public static EntadDismantleResult Dismantle(Thing thing, Pawn pawn = null)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty() || thing.Destroyed) return null;

            var result = new EntadDismantleResult
            {
                thing = thing,
                def = thing.def,
                stuff = thing.Stuff,
                quality = thing.TryGetQuality(out QualityCategory q) ? q : (QualityCategory?)null,
                traits = comp.activeTraits.Select(m => new EntadTraitRecord(m.def, m.Rarity)).ToList(),
                insight = InsightFor(thing),
                pawn = pawn,
                map = thing.MapHeld,
            };
            EntadInsight.Instance?.Add(result.insight);

            var handlers = EntadDismantled;
            if (handlers != null)
                foreach (Action<EntadDismantleResult> h in handlers.GetInvocationList())
                {
                    try { h(result); }
                    catch (Exception e) { Log.Error($"[Entad Framework] An EntadDismantled handler threw: {e}"); }
                }

            string label = thing.LabelCap;
            // Carried to a bench, the item is in the pawn's hands: burst where the pawn stands
            if (thing.MapHeld != null) EntadDismantle.Burst(thing.PositionHeld, thing.MapHeld);
            // A casket (sarcophagus) only drops what it holds on Deconstruct or KillFinalize, and deletes it on Vanish
            if (thing is Building_Casket casket && thing.Spawned) casket.EjectContents();
            if (!thing.Destroyed) thing.Destroy(DestroyMode.Vanish);

            if (pawn != null)
            {
                string traits = string.Join(", ", result.traits.Select(t => "EF_DismantleTraitEntry".Translate(t.def.LabelCap, t.rarity.Label()).Resolve()));
                Messages.Message("EF_DismantleMessage".Translate(pawn.LabelShort, label, result.insight.ToString("0.#"), Insight.ToString("0.#"), traits),
                    new LookTargets(pawn), MessageTypeDefOf.NeutralEvent);
            }
            return result;
        }
    }

    [DefOf]
    public static class EntadDismantleDefOf
    {
        public static DesignationDef Entad_Dismantle;
        public static JobDef Entad_DismantleJob;

        static EntadDismantleDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadDismantleDefOf)); }
    }

    [StaticConstructorOnStartup]
    public static class EntadDismantle
    {
        public static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/Commands/EntadDismantle");

        // Work in ticks at research speed 1: an hour or so for an everyday entad, longer for a strong one
        public const float BaseWork = 1000f, WorkPerPoint = 500f;

        public static float WorkFor(Thing thing) => BaseWork + WorkPerPoint * EntadApi.InsightFor(thing);

        // Inspect line while marked or part-done: progress, and the colony's insight so the player can see the pool
        public static string InspectLine(CompEntad comp)
        {
            Thing thing = comp.parent;
            if (!IsDesignated(thing) && comp.dismantleWorkDone <= 0f) return null;
            float pct = Mathf.Clamp01(comp.dismantleWorkDone / WorkFor(thing));
            return (IsDesignated(thing) ? "EF_DismantleInspect" : "EF_DismantlePartial").Translate(pct.ToStringPercent(), EntadApi.InsightFor(thing).ToString("0.#"), EntadApi.Insight.ToString("0.#"));
        }

        // Loose items anywhere; buildings only the colony's own
        public static bool CanDesignate(Thing thing) =>
            thing.Spawned && (thing.def.category == ThingCategory.Item || (thing.def.category == ThingCategory.Building && thing.Faction == Faction.OfPlayer));

        public static bool IsDesignated(Thing thing) =>
            thing.Spawned && thing.Map.designationManager.DesignationOn(thing, EntadDismantleDefOf.Entad_Dismantle) != null;

        public static void Toggle(Thing thing)
        {
            if (!thing.Spawned) return;
            var dm = thing.Map.designationManager;
            var existing = dm.DesignationOn(thing, EntadDismantleDefOf.Entad_Dismantle);
            if (existing != null) dm.RemoveDesignation(existing);
            else dm.AddDesignation(new Designation(thing, EntadDismantleDefOf.Entad_Dismantle));
        }

        public static Command_Toggle Gizmo(CompEntad comp)
        {
            Thing thing = comp.parent;
            return new Command_Toggle
            {
                defaultLabel = "EF_Dismantle".Translate(),
                defaultDesc = "EF_DismantleDesc".Translate(EntadApi.InsightFor(thing).ToString("0.#"), EntadApi.Insight.ToString("0.#")),
                icon = Icon,
                isActive = () => IsDesignated(thing),
                toggleAction = () => Toggle(thing),
            };
        }

        public static void Burst(IntVec3 cell, Map map)
        {
            SoundDefOf.Building_Deconstructed.PlayOneShot(new TargetInfo(cell, map));
            FleckMaker.Static(cell, map, FleckDefOf.PsycastSkipFlashEntry, 0.8f);
            for (int i = 0; i < 4; i++)
                FleckMaker.Static(cell.ToVector3Shifted() + new Vector3(Rand.Range(-0.5f, 0.5f), 0f, Rand.Range(-0.5f, 0.5f)), map,
                    EntadSpaceFleckDefOf.Entad_SpaceShimmer, Rand.Range(0.5f, 0.9f));
        }
    }

    // Research work. A designated item is carried to the nearest free research bench and dismantled there, faster at a
    // better bench (ResearchSpeedFactor, as research). An entad building is dismantled where it stands.
    public class WorkGiver_DismantleEntad : WorkGiver_Scanner
    {
        public static bool NeedsBench(Thing t) => t.def.category == ThingCategory.Item;

        // Nearest to the item, since that is where the carry starts
        public static Building_ResearchBench FindBench(Pawn pawn, Thing item, bool forced) =>
            (Building_ResearchBench)GenClosest.ClosestThingReachable(item.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.ResearchBench),
                PathEndMode.InteractionCell, TraverseParms.For(pawn), validator: b => b is Building_ResearchBench && b.Faction == Faction.OfPlayer
                    && !b.IsForbidden(pawn) && !b.IsBurning() && pawn.CanReserve(b, 1, -1, null, forced)
                    && (!b.def.hasInteractionCell || pawn.CanReserveSittableOrSpot(b.InteractionCell, forced)));

        public override PathEndMode PathEndMode => PathEndMode.Touch;

        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
            !pawn.Map.designationManager.AnySpawnedDesignationOfDef(EntadDismantleDefOf.Entad_Dismantle);

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
            foreach (var d in pawn.Map.designationManager.SpawnedDesignationsOfDef(EntadDismantleDefOf.Entad_Dismantle))
                yield return d.target.Thing;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (pawn.Map.designationManager.DesignationOn(t, EntadDismantleDefOf.Entad_Dismantle) == null) return false;
            if (t.IsForbidden(pawn) || t.IsBurning()) return false;
            if (!pawn.CanReserve(t, 1, -1, null, forced)) return false;
            // The list check is a count read, sparing the region search on maps with no bench at all
            if (NeedsBench(t) && (pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.ResearchBench).Count == 0 || FindBench(pawn, t, forced) == null))
            {
                JobFailReason.Is("EF_DismantleNoBench".Translate());
                return false;
            }
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!NeedsBench(t)) return JobMaker.MakeJob(EntadDismantleDefOf.Entad_DismantleJob, t);
            var bench = FindBench(pawn, t, forced);
            if (bench == null) return null;
            var job = JobMaker.MakeJob(EntadDismantleDefOf.Entad_DismantleJob, t, bench);
            job.count = 1;
            return job;
        }
    }

    public class JobDriver_DismantleEntad : JobDriver
    {
        private float totalWork;

        private CompEntad Comp => TargetThingA?.TryGetComp<CompEntad>();

        private bool AtBench => job.targetB.IsValid;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed)) return false;
            if (!AtBench) return true;
            if (!pawn.Reserve(job.targetB, job, 1, -1, null, errorOnFailed)) return false;
            var bench = job.targetB.Thing;
            return !bench.def.hasInteractionCell || pawn.ReserveSittableOrSpot(bench.InteractionCell, job, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // Carried, the item is no longer spawned, so these check only that it still exists
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnThingMissingDesignation(TargetIndex.A, EntadDismantleDefOf.Entad_Dismantle);
            // A dev action or another mod stripped its traits meanwhile: nothing left to dismantle
            this.FailOn(() => TargetThingA.TryGetComp<CompEntad>()?.activeTraits.NullOrEmpty() ?? true);
            TargetIndex workAt = AtBench ? TargetIndex.B : TargetIndex.A;
            if (AtBench)
            {
                this.FailOnDespawnedNullOrForbidden(TargetIndex.B);
                this.FailOnBurningImmobile(TargetIndex.B);
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                    .FailOnSomeonePhysicallyInteracting(TargetIndex.A).FailOnForbidden(TargetIndex.A);
                yield return Toils_Haul.StartCarryThing(TargetIndex.A);
                yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.InteractionCell);
            }
            else
            {
                this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            }

            Toil work = ToilMaker.MakeToil("DismantleEntad");
            // Progress lives on the item (CompEntad.dismantleWorkDone), so a pawn who stops to eat or sleep drops it and
            // whoever picks it up next carries on from there, like an unfinished thing at a workbench
            work.initAction = () => totalWork = EntadDismantle.WorkFor(TargetThingA);
            work.tickIntervalAction = delta =>
            {
                var comp = Comp;
                if (comp == null) return;
                if (totalWork <= 0f) totalWork = EntadDismantle.WorkFor(TargetThingA);
                float speed = pawn.GetStatValue(StatDefOf.ResearchSpeed);
                // Floored so a modded bench at factor 0 can't hold the pawn here forever (the toil has no time limit)
                if (AtBench) speed *= Mathf.Max(0.1f, TargetThingB.GetStatValue(StatDefOf.ResearchSpeedFactor));
                comp.dismantleWorkDone += speed * delta;
                pawn.skills?.Learn(SkillDefOf.Intellectual, 0.1f * delta);
                if (AtBench) pawn.GainComfortFromCellIfPossible(delta, chairsOnly: true);
                // Finished in the same tick, rather than handing over to a separate toil, so nothing can leave the pawn
                // sitting at a full bar
                if (comp.dismantleWorkDone >= totalWork)
                {
                    EntadApi.Dismantle(TargetThingA, pawn);
                    EndJobWith(JobCondition.Succeeded);
                }
            };
            work.defaultCompleteMode = ToilCompleteMode.Never;
            work.WithEffect(EffecterDefOf.Research, workAt);
            work.WithProgressBar(workAt, () => totalWork <= 0f ? 0f : Mathf.Clamp01((Comp?.dismantleWorkDone ?? 0f) / totalWork));
            // At a bench the item stays in the pawn's hands while it works
            if (AtBench)
            {
                work.FailOn(() => pawn.carryTracker.CarriedThing != TargetThingA);
                work.FailOnCannotTouch(TargetIndex.B, PathEndMode.InteractionCell);
            }
            else work.FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            work.activeSkill = () => SkillDefOf.Intellectual;
            yield return work;
        }
    }
}
