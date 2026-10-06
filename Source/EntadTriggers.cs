using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // What sets a trigger off. Every event but Use is about the pawn wearing or wielding the item; Use is furniture,
    // fired for whoever used it (EntadMoods' job-end hook decides what counts as use).
    public enum EntadTriggerEvent
    {
        Kill,        // the holder killed something (every kill, hunting and slaughter included, like vanilla kill thoughts)
        WakeUp,      // a sleep of an hour or more ended (woken by a raid counts too)
        BillDone,    // finished one iteration of a production bill (crafting, cooking, smelting...)
        JobDone,     // finished one of the jobs in 'jobs'
        TakeDamage,  // took damage that got through
        Downed,      // was downed
        Eat,         // finished eating something nourishing
        Use          // furniture: someone used it (see EntadUseKind)
    }

    // Furniture use, as the job-end hook tells it apart
    [System.Flags]
    public enum EntadUseKind
    {
        Sleep = 1,        // lay in the bed for an hour or more
        Eat = 2,          // ate at the table, or seated on the chair
        Work = 4,         // a bill or research at it, or seated on its chair while doing one
        Recreation = 8,   // a joy job at it
        Other = 16,       // anything else it's used for (toilet, shower, throne, a short lie-down, lovin')
        Any = Sleep | Eat | Work | Recreation | Other
    }

    // One "when X happens, do Y" on a trait. Amounts are rolled per item and scale with rarity like stat values;
    // chance and cooldown don't.
    public class EntadTrigger
    {
        public EntadTriggerEvent on;
        public EntadUseKind use = EntadUseKind.Any;   // Use only
        public List<JobDef> jobs;                     // JobDone only
        public float chance = 1f;
        public float cooldownHours;                   // per item: shared by everyone who uses it

        // Effects
        public ThoughtDef thought;                    // plain memory with its own duration
        public HediffDef hediff;
        public FloatRange hediffHours = FloatRange.Zero; // 0: the hediff's own duration (needs HediffComp_Disappears otherwise)
        public FloatRange heal = FloatRange.Zero;     // HP healed, worst injuries first
        public FloatRange rest = FloatRange.Zero;     // fraction of the need restored (0.3 = 30%)
        public FloatRange food = FloatRange.Zero;
        public FloatRange joy = FloatRange.Zero;

        // Rolled values per trigger, in this order, in AppliedEntadTrait.triggerValues
        public const int ValueCount = 5;
        public const int Heal = 0, Rest = 1, Food = 2, Joy = 3, HediffHours = 4;

        public FloatRange RangeAt(int k)
        {
            switch (k)
            {
                case Heal: return heal;
                case Rest: return rest;
                case Food: return food;
                case Joy: return joy;
                default: return hediffHours;
            }
        }

        public bool HasEffect => thought != null || hediff != null || heal.max > 0f || rest.max > 0f || food.max > 0f || joy.max > 0f;

        public string EventLabel
        {
            get
            {
                switch (on)
                {
                    case EntadTriggerEvent.JobDone:
                        return "EF_Trigger_JobDone".Translate(jobs.NullOrEmpty() ? "?" : string.Join(", ", jobs.ConvertAll(j => j.label.NullOrEmpty() ? j.defName : j.label)));
                    case EntadTriggerEvent.Use:
                        if (use == EntadUseKind.Any) return "EF_Trigger_Use".Translate();
                        var kinds = new List<string>();
                        foreach (EntadUseKind k in new[] { EntadUseKind.Sleep, EntadUseKind.Eat, EntadUseKind.Work, EntadUseKind.Recreation, EntadUseKind.Other })
                            if ((use & k) != 0) kinds.Add(("EF_UseKind_" + k).Translate());
                        return "EF_Trigger_UseKinds".Translate(string.Join(", ", kinds));
                    default:
                        return ("EF_Trigger_" + on).Translate();
                }
            }
        }

        // "heals 32 HP, restores 20% rest", with this item's rolls (values null: the def's ranges, for the settings list)
        public string EffectsLabel(System.Func<int, float> value)
        {
            var parts = new List<string>();
            string Amount(int k, System.Func<float, string> fmt)
            {
                if (value != null) return fmt(value(k));
                var r = RangeAt(k);
                return r.min == r.max ? fmt(r.min) : fmt(r.min) + "~" + fmt(r.max);
            }
            if (heal.max > 0f) parts.Add("EF_TriggerEffect_Heal".Translate(Amount(Heal, v => v.ToString("0"))));
            if (rest.max > 0f) parts.Add("EF_TriggerEffect_Rest".Translate(Amount(Rest, v => v.ToStringPercent())));
            if (food.max > 0f) parts.Add("EF_TriggerEffect_Food".Translate(Amount(Food, v => v.ToStringPercent())));
            if (joy.max > 0f) parts.Add("EF_TriggerEffect_Joy".Translate(Amount(Joy, v => v.ToStringPercent())));
            if (hediff != null)
                parts.Add(hediffHours.max > 0f
                    ? "EF_TriggerEffect_HediffFor".Translate(hediff.label, Amount(HediffHours, v => v.ToString("0.#"))).ToString()
                    : "EF_TriggerEffect_Hediff".Translate(hediff.label).ToString());
            if (thought != null) parts.Add("EF_TriggerEffect_Thought".Translate(thought.stages?.FirstOrDefault()?.label ?? thought.defName));
            return string.Join(", ", parts);
        }

        public string LimitsLabel()
        {
            var parts = new List<string>();
            if (chance < 1f) parts.Add("EF_Trigger_Chance".Translate(chance.ToStringPercent()));
            if (cooldownHours > 0f) parts.Add("EF_Trigger_Cooldown".Translate(cooldownHours.ToString("0.#")));
            return string.Join(", ", parts);
        }

        public string Line(System.Func<int, float> value)
        {
            string limits = LimitsLabel();
            return limits.NullOrEmpty()
                ? "EF_Card_Trigger".Translate(EventLabel, EffectsLabel(value))
                : "EF_Card_TriggerLimited".Translate(EventLabel, EffectsLabel(value), limits);
        }

        public IEnumerable<string> ConfigErrors(string owner)
        {
            if (!HasEffect) yield return $"{owner}: a trigger on {on} has no effect";
            if (on == EntadTriggerEvent.JobDone && jobs.NullOrEmpty()) yield return $"{owner}: a JobDone trigger needs jobs";
            if (on != EntadTriggerEvent.Use && use != EntadUseKind.Any) yield return $"{owner}: 'use' only applies to Use triggers";
            if (chance <= 0f || chance > 1f) yield return $"{owner}: trigger chance must be above 0 and at most 1";
            if (cooldownHours < 0f) yield return $"{owner}: trigger cooldownHours can't be negative";
            for (int k = 0; k < ValueCount; k++)
            {
                var r = RangeAt(k);
                if (r.min > r.max || r.min < 0f) yield return $"{owner}: invalid trigger range ({r})";
            }
            if (rest.max > 1f || food.max > 1f || joy.max > 1f) yield return $"{owner}: rest, food and joy are fractions of the need (at most 1)";
            if (hediffHours.max > 0f && (hediff == null || !hediff.HasComp(typeof(HediffComp_Disappears))))
                yield return $"{owner}: hediffHours needs a hediff with HediffComp_Disappears";
            // Gained with no other pawn, so social memories can't work (vanilla logs an error every time)
            if (thought != null && (!thought.IsMemory || !typeof(Thought_Memory).IsAssignableFrom(thought.ThoughtClass)
                || typeof(Thought_MemorySocial).IsAssignableFrom(thought.ThoughtClass)))
                yield return $"{owner}: trigger thought {thought.defName} must be a plain (non-social) memory thought";
            if (thought != null && (!thought.requiredTraits.NullOrEmpty() || !thought.requiredGenes.NullOrEmpty()
                || !thought.requiredHediffs.NullOrEmpty() || thought.gender != Gender.None || thought.minExpectation != null))
                yield return $"{owner}: trigger thought {thought.defName} has requirements most pawns won't meet, so it would usually do nothing";
        }
    }

    public static class EntadTriggers
    {
        // Ticks a lie-down must last to count as sleep (WakeUp, and Sleep use of a bed)
        public const int MinSleepTicks = GenDate.TicksPerHour;

        // Def data: which events any loaded trait listens for. Every hook checks its event here first, so with no
        // trait using an event its hook is one array read.
        private static bool[] anyEvent;

        public static bool Any(EntadTriggerEvent ev)
        {
            if (anyEvent == null)
            {
                var a = new bool[System.Enum.GetValues(typeof(EntadTriggerEvent)).Length];
                foreach (var d in DefDatabase<EntadTraitDef>.AllDefsListForReading)
                    if (d.triggers != null) foreach (var t in d.triggers) a[(int)t.on] = true;
                anyEvent = a;
            }
            return anyEvent[(int)ev];
        }

        public static bool AnyJobEvent => Any(EntadTriggerEvent.WakeUp) || Any(EntadTriggerEvent.Eat) || Any(EntadTriggerEvent.JobDone);

        // Gear events: every worn or wielded entad that works for this pawn, checked now so a weapon dropped by the
        // event itself (downing drops it) still counts
        public static void FromGear(Pawn pawn, EntadTriggerEvent ev, JobDef job = null)
        {
            if (pawn == null || !Any(ev)) return;
            var eq = pawn.equipment?.AllEquipmentListForReading;
            if (eq != null) for (int i = 0; i < eq.Count; i++) FromItem(pawn, eq[i], ev, job);
            var ap = pawn.apparel?.WornApparel;
            if (ap != null) for (int i = 0; i < ap.Count; i++) FromItem(pawn, ap[i], ev, job);
        }

        private static void FromItem(Pawn pawn, Thing item, EntadTriggerEvent ev, JobDef job)
        {
            var comp = item.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0 || !comp.ActiveFor(pawn)) return;
            foreach (var m in comp.activeTraits)
            {
                var ts = m.def.triggers;
                if (ts == null) continue;
                for (int i = 0; i < ts.Count; i++)
                {
                    if (ts[i].on != ev) continue;
                    if (ev == EntadTriggerEvent.JobDone && !ts[i].jobs.Contains(job)) continue;
                    TryQueue(pawn, m, i, null);
                }
            }
        }

        // Furniture: called by EntadMoods.OnFurnitureUsed once the user is known to be one the item works for
        public static void FromFurniture(Pawn pawn, CompEntad comp, EntadUseKind kind)
        {
            if (!Any(EntadTriggerEvent.Use)) return;
            foreach (var m in comp.activeTraits)
            {
                var ts = m.def.triggers;
                if (ts == null) continue;
                for (int i = 0; i < ts.Count; i++)
                    if (ts[i].on == EntadTriggerEvent.Use && (ts[i].use & kind) != 0) TryQueue(pawn, m, i, comp.parent);
            }
        }

        // Chance and cooldown are settled here, so two events in the same tick can't both get past a cooldown
        private static void TryQueue(Pawn pawn, AppliedEntadTrait m, int i, Thing source)
        {
            var t = m.def.triggers[i];
            int now = Find.TickManager.TicksGame;
            if (m.TriggerReadyTick(i) > now) return;
            if (t.chance < 1f && !Rand.Chance(t.chance)) return;
            if (t.cooldownHours > 0f) m.SetTriggerReadyTick(i, now + Mathf.RoundToInt(t.cooldownHours * GenDate.TicksPerHour));
            EntadTriggerQueue.pending.Add(new EntadTriggerQueue.Pending { pawn = pawn, trait = m, index = i, source = source });
        }

        // Effects run on the next tick, outside whatever raised the event: healing inside MakeDowned or damage
        // handling would change the pawn's health state while vanilla is still in the middle of changing it
        public static void Apply(EntadTriggerQueue.Pending p)
        {
            Pawn pawn = p.pawn;
            if (pawn == null || pawn.Dead || pawn.Destroyed || p.trait?.def?.triggers == null || p.index >= p.trait.def.triggers.Count) return;
            var t = p.trait.def.triggers[p.index];
            var m = p.trait;

            float heal = m.TriggerValue(p.index, EntadTrigger.Heal);
            if (heal > 0f) HealWorstFirst(pawn, heal);
            Restore(pawn.needs?.rest, m.TriggerValue(p.index, EntadTrigger.Rest));
            Restore(pawn.needs?.food, m.TriggerValue(p.index, EntadTrigger.Food));
            Restore(pawn.needs?.joy, m.TriggerValue(p.index, EntadTrigger.Joy));
            if (t.hediff != null) AddOrExtend(pawn, t.hediff, m.TriggerValue(p.index, EntadTrigger.HediffHours));
            if (t.thought != null && pawn.needs?.mood != null)
            {
                // From furniture: tagged with the building, so using it again renews the memory instead of stacking
                if (p.source != null) EntadMoods.Give(pawn, t.thought, t.thought.DurationTicks, p.source);
                else pawn.needs.mood.thoughts.memories.TryGainMemory(t.thought);
            }
            m.Reveal(EntadEffectKind.Trigger);
        }

        private static void Restore(Need need, float fraction)
        {
            if (need != null && fraction > 0f) need.CurLevelPercentage += fraction;
        }

        private static List<Hediff_Injury> injuries = new List<Hediff_Injury>();

        // Closes the worst injuries first; scars (permanent injuries) aren't touched
        public static void HealWorstFirst(Pawn pawn, float amount)
        {
            injuries.Clear();
            pawn.health.hediffSet.GetHediffs(ref injuries, h => !h.IsPermanent());
            injuries.SortByDescending(h => h.Severity);
            for (int i = 0; i < injuries.Count && amount > 0f; i++)
            {
                float h = Mathf.Min(amount, injuries[i].Severity);
                injuries[i].Heal(h);
                amount -= h;
            }
            injuries.Clear();
        }

        // A hediff already there has its time topped up to the new length rather than a second copy added
        private static void AddOrExtend(Pawn pawn, HediffDef def, float hours)
        {
            int ticks = hours > 0f ? Mathf.RoundToInt(hours * GenDate.TicksPerHour) : -1;
            var existing = pawn.health.hediffSet.GetFirstHediffOfDef(def);
            if (existing != null)
            {
                var d = existing.TryGetComp<HediffComp_Disappears>();
                if (d != null && ticks > d.ticksToDisappear) d.ticksToDisappear = ticks;
                return;
            }
            var hediff = HediffMaker.MakeHediff(def, pawn);
            if (ticks > 0)
            {
                var d = hediff.TryGetComp<HediffComp_Disappears>();
                if (d != null) d.ticksToDisappear = ticks;
            }
            pawn.health.AddHediff(hediff);
        }

        // From EntadMoods' CleanupCurrentJob prefix, for every pawn's job end; returns at once for pawns without gear
        public static void OnJobEnded(Pawn pawn, Job job, JobCondition condition)
        {
            if ((pawn.apparel == null && pawn.equipment == null) || pawn.Dead) return;
            bool succeeded = condition == JobCondition.Succeeded;
            if (job.def == JobDefOf.LayDown && !pawn.Downed && Find.TickManager.TicksGame - job.startTick >= MinSleepTicks)
                FromGear(pawn, EntadTriggerEvent.WakeUp);
            if (!succeeded) return;
            if (job.def == JobDefOf.Ingest && job.targetA.Thing?.def?.IsNutritionGivingIngestible == true)
                FromGear(pawn, EntadTriggerEvent.Eat);
            FromGear(pawn, EntadTriggerEvent.JobDone, job.def);
        }
    }

    // Trigger effects waiting for the next tick (see EntadTriggers.Apply). Not saved: at most one tick's worth.
    public class EntadTriggerQueue : GameComponent
    {
        public struct Pending
        {
            public Pawn pawn;
            public AppliedEntadTrait trait;
            public int index;
            public Thing source;
        }

        internal static readonly List<Pending> pending = new List<Pending>();

        public EntadTriggerQueue(Game game) { pending.Clear(); }

        public override void GameComponentTick()
        {
            if (pending.Count == 0) return;
            var batch = pending.ToArray();
            pending.Clear();
            for (int i = 0; i < batch.Length; i++)
            {
                try { EntadTriggers.Apply(batch[i]); }
                catch (System.Exception e) { Log.Error($"[Entad Framework] trigger effect failed: {e}"); }
            }
        }
    }

    // Kill: vanilla's record keeping sees every kill with its killer, wielded weapon or not
    [HarmonyPatch(typeof(RecordsUtility), nameof(RecordsUtility.Notify_PawnKilled))]
    public static class Patch_RecordsUtility_PawnKilled_EntadTrigger
    {
        public static void Postfix(Pawn killer) => EntadTriggers.FromGear(killer, EntadTriggerEvent.Kill);
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Pawn_PostApplyDamage_EntadTrigger
    {
        public static void Postfix(Pawn __instance, float totalDamageDealt)
        {
            if (totalDamageDealt <= 0f || __instance.Dead) return;
            EntadTriggers.FromGear(__instance, EntadTriggerEvent.TakeDamage);
        }
    }

    // A prefix: MakeDowned drops the pawn's weapon, so the gear has to be read before it runs
    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    public static class Patch_HealthTracker_MakeDowned_EntadTrigger
    {
        public static void Prefix(Pawn_HealthTracker __instance, Pawn ___pawn)
        {
            if (__instance.Downed) return;
            EntadTriggers.FromGear(___pawn, EntadTriggerEvent.Downed);
        }
    }

    // One iteration of a production bill (Bill_ProductionWithUft calls into this too)
    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.Notify_IterationCompleted))]
    public static class Patch_BillProduction_IterationCompleted_EntadTrigger
    {
        public static void Postfix(Pawn billDoer) => EntadTriggers.FromGear(billDoer, EntadTriggerEvent.BillDone);
    }
}
