using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

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

    // Which kills a Kill trigger counts. Any includes hunting and slaughter.
    public enum EntadKillVictims
    {
        Any,
        Hostile,     // hostile to the killer as it died (GenHostility.HostileTo): raiders, mechanoids, insects, and a
                     // manhunter or berserker killed outright. Not a hunted animal, a slaughtered one or an executed
                     // prisoner. Edge cases from vanilla's rules: downing ends most mental states, so a manhunter
                     // downed and then finished off is a plain wild animal again; and a berserk killer is hostile to
                     // everyone, so its colony mates count.
        Humanlike,   // people, whatever their side
        Fight        // Hostile, plus wild animals and wild men: no faction and nobody's prisoner or slave. Hunting and a drafted
                     // shot at a wild animal count; slaughtering livestock, executing a prisoner or killing a slave
                     // doesn't, because those have a faction or a host faction
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

    // Who an area trigger (radius > 0) reaches, as seen from the pawn the trigger fired for
    [System.Flags]
    public enum EntadTriggerTargets
    {
        Self = 1,      // the pawn the event happened to (the wearer, or whoever used the furniture)
        Allies = 2,    // other pawns of that pawn's faction, and its faction's prisoners and guests
        Enemies = 4    // pawns hostile to it
    }

    // One "when X happens, do Y" on a trait. Amounts are rolled per item and scale with rarity like stat values;
    // chance, cooldown and radius don't.
    public class EntadTrigger
    {
        public EntadTriggerEvent on;
        public EntadUseKind use = EntadUseKind.Any;   // Use only
        public List<JobDef> jobs;                     // JobDone only
        public EntadKillVictims victims = EntadKillVictims.Any; // Kill only
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
        public FloatRange stunSeconds = FloatRange.Zero; // StunHandler.StunFor, as an EMP or a stun grenade does
        public InspirationDef inspiration;            // starts this inspiration if the pawn can have it
        public bool randomInspiration;                // or a random one the pawn can have

        // Area: with a radius, every effect goes to each pawn 'targets' picks within that many tiles, instead of only
        // to the pawn the event happened to. The centre is that pawn, the furniture for a Use trigger, or with
        // atVictim (Kill only) the cell the victim died on. sameRoom also requires the target to share the centre's
        // room, when that is an indoor room. fleck and sound play at the centre when it fires.
        public float radius;
        public EntadTriggerTargets targets = EntadTriggerTargets.Self;
        public bool sameRoom;
        public bool atVictim;
        public FleckDef fleck;
        public SoundDef sound;

        public bool IsArea => radius > 0f;

        // Rolled values per trigger, in this order, in AppliedEntadTrait.triggerValues
        public const int ValueCount = 6;
        public const int Heal = 0, Rest = 1, Food = 2, Joy = 3, HediffHours = 4, StunSeconds = 5;

        public FloatRange RangeAt(int k)
        {
            switch (k)
            {
                case Heal: return heal;
                case Rest: return rest;
                case Food: return food;
                case Joy: return joy;
                case StunSeconds: return stunSeconds;
                default: return hediffHours;
            }
        }

        public bool HasInspiration => inspiration != null || randomInspiration;

        public bool HasEffect => thought != null || hediff != null || heal.max > 0f || rest.max > 0f || food.max > 0f || joy.max > 0f
            || stunSeconds.max > 0f || HasInspiration;

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
                    case EntadTriggerEvent.Kill:
                        return victims == EntadKillVictims.Any ? "EF_Trigger_Kill".Translate() : ("EF_Trigger_Kill" + victims).Translate();
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
            if (stunSeconds.max > 0f) parts.Add("EF_TriggerEffect_Stun".Translate(Amount(StunSeconds, v => v.ToString("0.#"))));
            if (inspiration != null) parts.Add("EF_TriggerEffect_Inspiration".Translate(inspiration.label));
            if (randomInspiration) parts.Add("EF_TriggerEffect_RandomInspiration".Translate());
            string effects = string.Join(", ", parts);
            if (!IsArea) return effects;
            var who = new List<string>();
            if ((targets & EntadTriggerTargets.Self) != 0) who.Add("EF_TriggerTargets_Self".Translate());
            if ((targets & EntadTriggerTargets.Allies) != 0) who.Add("EF_TriggerTargets_Allies".Translate());
            if ((targets & EntadTriggerTargets.Enemies) != 0) who.Add("EF_TriggerTargets_Enemies".Translate());
            string key = atVictim ? "EF_TriggerArea_Victim" : sameRoom ? "EF_TriggerArea_Room" : "EF_TriggerArea";
            return key.Translate(effects, who.ToCommaList(useAnd: true), radius.ToString("0.#"));
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
            if (on != EntadTriggerEvent.Kill && victims != EntadKillVictims.Any) yield return $"{owner}: 'victims' only applies to Kill triggers";
            if (chance <= 0f || chance > 1f) yield return $"{owner}: trigger chance must be above 0 and at most 1";
            if (cooldownHours < 0f) yield return $"{owner}: trigger cooldownHours can't be negative";
            for (int k = 0; k < ValueCount; k++)
            {
                var r = RangeAt(k);
                if (r.min > r.max || r.min < 0f) yield return $"{owner}: invalid trigger range ({r})";
            }
            if (rest.max > 1f || food.max > 1f || joy.max > 1f) yield return $"{owner}: rest, food and joy are fractions of the need (at most 1)";
            if (radius < 0f) yield return $"{owner}: trigger radius can't be negative";
            if (targets == 0) yield return $"{owner}: trigger targets is empty";
            if (!IsArea && targets != EntadTriggerTargets.Self) yield return $"{owner}: trigger targets other than Self need a radius";
            if (!IsArea && (sameRoom || atVictim || fleck != null || sound != null)) yield return $"{owner}: sameRoom, atVictim, fleck and sound only apply to a trigger with a radius";
            if (atVictim && on != EntadTriggerEvent.Kill) yield return $"{owner}: atVictim only applies to Kill triggers";
            if (inspiration != null && randomInspiration) yield return $"{owner}: set inspiration or randomInspiration, not both";
            // (any comp derived from it too: DisappearsPausable, DisappearsAndKills...)
            if (hediffHours.max > 0f && (hediff == null || hediff.comps == null
                || !hediff.comps.Any(c => c.compClass != null && typeof(HediffComp_Disappears).IsAssignableFrom(c.compClass))))
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
        public static void FromGear(Pawn pawn, EntadTriggerEvent ev, JobDef job = null, Pawn victim = null)
        {
            if (pawn == null || !Any(ev)) return;
            var eq = pawn.equipment?.AllEquipmentListForReading;
            if (eq != null) for (int i = 0; i < eq.Count; i++) FromItem(pawn, eq[i], ev, job, victim);
            var ap = pawn.apparel?.WornApparel;
            if (ap != null) for (int i = 0; i < ap.Count; i++) FromItem(pawn, ap[i], ev, job, victim);
        }

        // Kill victims: called from RecordsUtility.Notify_PawnKilled, which vanilla runs before the victim despawns
        // or loses its mental state, so a manhunter or berserker still reads as hostile here
        private static bool VictimMatches(EntadKillVictims victims, Pawn victim, Pawn killer)
        {
            switch (victims)
            {
                case EntadKillVictims.Hostile: return victim != null && victim.HostileTo(killer);
                case EntadKillVictims.Humanlike: return victim?.RaceProps?.Humanlike == true;
                case EntadKillVictims.Fight:
                    // Only animals and wild men count as wild: a downed refugee can also have no faction
                    return victim != null && (victim.HostileTo(killer)
                        || ((victim.RaceProps.Animal || victim.IsWildMan()) && victim.Faction == null && victim.HostFaction == null));
                default: return true;
            }
        }

        private static void FromItem(Pawn pawn, Thing item, EntadTriggerEvent ev, JobDef job, Pawn victim)
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
                    if (ev == EntadTriggerEvent.JobDone && (ts[i].jobs == null || !ts[i].jobs.Contains(job))) continue;
                    if (ev == EntadTriggerEvent.Kill && !VictimMatches(ts[i].victims, victim, pawn)) continue;
                    // The victim's cell now: by the next tick the corpse may have been moved or destroyed
                    TryQueue(pawn, m, i, null, ts[i].atVictim && victim != null ? victim.PositionHeld : IntVec3.Invalid);
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
        private static void TryQueue(Pawn pawn, AppliedEntadTrait m, int i, Thing source, IntVec3? at = null)
        {
            var t = m.def.triggers[i];
            int now = Find.TickManager.TicksGame;
            if (m.TriggerReadyTick(i) > now) return;
            if (t.chance < 1f && !Rand.Chance(t.chance)) return;
            if (t.cooldownHours > 0f) m.SetTriggerReadyTick(i, now + Mathf.RoundToInt(t.cooldownHours * GenDate.TicksPerHour));
            EntadTriggerQueue.pending.Add(new EntadTriggerQueue.Pending { pawn = pawn, trait = m, index = i, source = source, at = at ?? IntVec3.Invalid });
        }

        // Effects run on the next tick, outside whatever raised the event: healing inside MakeDowned or damage
        // handling would change the pawn's health state while vanilla is still in the middle of changing it
        public static void Apply(EntadTriggerQueue.Pending p)
        {
            Pawn pawn = p.pawn;
            if (pawn == null || pawn.Dead || pawn.Destroyed || p.trait?.def?.triggers == null || p.index >= p.trait.def.triggers.Count) return;
            var t = p.trait.def.triggers[p.index];
            var m = p.trait;

            if (!t.IsArea) ApplyTo(pawn, pawn, t, p);
            else
            {
                Map map = p.source?.MapHeld ?? pawn.MapHeld;
                if (map == null)
                {
                    // In a caravan or a transporter: nobody around, so only the pawn itself, if it is a target
                    if ((t.targets & EntadTriggerTargets.Self) != 0) ApplyTo(pawn, pawn, t, p);
                }
                else
                {
                    IntVec3 center = p.at.IsValid ? p.at : p.source != null ? p.source.PositionHeld : pawn.PositionHeld;
                    // Only an enclosed room limits: outdoors (or a room open to the sky) the radius alone does
                    Room room = t.sameRoom ? center.GetRoom(map) : null;
                    if (room != null && room.PsychologicallyOutdoors) room = null;
                    float r2 = t.radius * t.radius;
                    // Once per firing, over the spawned pawns: triggers fire on events (a kill, waking up), never per tick
                    areaTargets.Clear();
                    var all = map.mapPawns.AllPawnsSpawned;
                    for (int i = 0; i < all.Count; i++)
                    {
                        Pawn other = all[i];
                        if (other.Dead || (other.Position - center).LengthHorizontalSquared > r2) continue;
                        if (room != null && other.GetRoom() != room) continue;
                        if (IsTarget(pawn, other, t.targets)) areaTargets.Add(other);
                    }
                    // A carried or caravan-bound pawn isn't in the spawned list; it still counts as being where it is
                    if ((t.targets & EntadTriggerTargets.Self) != 0 && !pawn.Spawned && pawn.MapHeld == map && !areaTargets.Contains(pawn)
                        && (pawn.PositionHeld - center).LengthHorizontalSquared <= r2)
                        areaTargets.Add(pawn);
                    if (t.fleck != null && center.InBounds(map)) FleckMaker.Static(center, map, t.fleck, t.radius);
                    if (t.sound != null && center.InBounds(map)) t.sound.PlayOneShot(new TargetInfo(center, map));
                    // Copied first: a stun or heal can't change the list, but an effect from another mod might
                    var hit = areaTargets.ToArray();
                    areaTargets.Clear();
                    for (int i = 0; i < hit.Length; i++) ApplyTo(hit[i], pawn, t, p);
                }
            }
            m.Reveal(EntadEffectKind.Trigger);
        }

        private static readonly List<Pawn> areaTargets = new List<Pawn>();

        internal static bool IsTarget(Pawn pawn, Pawn other, EntadTriggerTargets targets)
        {
            if (other == pawn) return (targets & EntadTriggerTargets.Self) != 0;
            if ((targets & EntadTriggerTargets.Enemies) != 0 && other.HostileTo(pawn)) return true;
            if ((targets & EntadTriggerTargets.Allies) != 0 && pawn.Faction != null && !other.HostileTo(pawn)
                && (other.Faction == pawn.Faction || other.HostFaction == pawn.Faction)) return true;
            return false;
        }

        // Every effect of the trigger on one pawn. 'user' is the pawn the event happened to: it stuns as the
        // instigator, and the inspiration letter names its item.
        private static void ApplyTo(Pawn target, Pawn user, EntadTrigger t, EntadTriggerQueue.Pending p)
        {
            if (target.Dead || target.Destroyed) return;
            var m = p.trait;
            float heal = m.TriggerValue(p.index, EntadTrigger.Heal);
            if (heal > 0f) HealWorstFirst(target, heal);
            Restore(target.needs?.rest, m.TriggerValue(p.index, EntadTrigger.Rest));
            Restore(target.needs?.food, m.TriggerValue(p.index, EntadTrigger.Food));
            Restore(target.needs?.joy, m.TriggerValue(p.index, EntadTrigger.Joy));
            if (t.hediff != null) AddOrExtend(target, t.hediff, m.TriggerValue(p.index, EntadTrigger.HediffHours));
            if (t.thought != null && target.needs?.mood != null)
            {
                // From furniture: tagged with the building, so using it again renews the memory instead of stacking
                if (p.source != null) EntadMoods.Give(target, t.thought, t.thought.DurationTicks, p.source);
                else target.needs.mood.thoughts.memories.TryGainMemory(t.thought);
            }
            float stun = m.TriggerValue(p.index, EntadTrigger.StunSeconds);
            if (stun > 0f && target.Spawned && target.stances?.stunner != null)
                target.stances.stunner.StunFor(UnityEngine.Mathf.RoundToInt(stun * GenTicks.TicksPerRealSecond), user);
            if (t.HasInspiration && target.mindState?.inspirationHandler != null && !target.Inspired)
            {
                var handler = target.mindState.inspirationHandler;
                var def = t.inspiration ?? handler.GetRandomAvailableInspirationDef();
                Thing item = p.source ?? m.owner?.parent;
                if (def != null) handler.TryStartInspiration(def, item != null ? "EF_InspirationReason".Translate(item.LabelShort).ToString() : null);
            }
        }

        internal static void Restore(Need need, float fraction)
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
        internal static void AddOrExtend(Pawn pawn, HediffDef def, float hours)
        {
            int ticks = hours > 0f ? Mathf.RoundToInt(hours * GenDate.TicksPerHour) : -1;
            var existing = pawn.health.hediffSet.GetFirstHediffOfDef(def);
            if (existing != null)
            {
                var d = existing.TryGetComp<HediffComp_Disappears>();
                if (d == null) return;
                // With the hediff's own duration, a repeat trigger gives it a fresh one (if that's longer than what's left)
                if (ticks < 0) ticks = d.Props.disappearsAfterTicks.RandomInRange;
                if (ticks > d.ticksToDisappear) d.ticksToDisappear = ticks;
                return;
            }
            var hediff = HediffMaker.MakeHediff(def, pawn);
            if (ticks > 0) hediff.TryGetComp<HediffComp_Disappears>()?.SetDuration(ticks);
            pawn.health.AddHediff(hediff);
        }

        private static readonly AccessTools.FieldRef<Need_Rest, int> lastRestTick = AccessTools.FieldRefAccess<Need_Rest, int>("lastRestTick");

        // A LayDown job that lasted an hour and in which the pawn fell asleep at some point. Vanilla only counts rest
        // (and sets lastRestTick) while asleep, so a patient lying awake on bed rest doesn't count. A downed pawn
        // doesn't either, though they may doze: they're recovering, not waking up.
        public static bool Slept(Pawn pawn, Job job)
        {
            if (job.def != JobDefOf.LayDown || job.startTick < 0 || pawn.Downed) return false;
            if (Find.TickManager.TicksGame - job.startTick < MinSleepTicks) return false;
            var rest = pawn.needs?.rest;
            return rest != null && lastRestTick(rest) >= job.startTick;
        }

        // From EntadMoods' CleanupCurrentJob prefix, for every pawn's job end; returns at once for pawns without gear
        public static void OnJobEnded(Pawn pawn, Job job, JobCondition condition)
        {
            if ((pawn.apparel == null && pawn.equipment == null) || pawn.Dead) return;
            bool succeeded = condition == JobCondition.Succeeded;
            if (Any(EntadTriggerEvent.WakeUp) && Slept(pawn, job))
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
            public IntVec3 at;   // an atVictim Kill trigger's centre; Invalid otherwise
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
        public static void Postfix(Pawn killed, Pawn killer) => EntadTriggers.FromGear(killer, EntadTriggerEvent.Kill, victim: killed);
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

    // A prefix: MakeDowned drops the pawn's weapon, so the gear has to be read before it runs.
    // Going under anaesthetic for surgery isn't being downed in any sense a trait cares about (and would use up
    // Last Stand's cooldown on the operating table).
    [HarmonyPatch(typeof(Pawn_HealthTracker), "MakeDowned")]
    public static class Patch_HealthTracker_MakeDowned_EntadTrigger
    {
        public static void Prefix(Pawn_HealthTracker __instance, Pawn ___pawn, Hediff hediff)
        {
            if (__instance.Downed || hediff?.def == HediffDefOf.Anesthetic) return;
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
