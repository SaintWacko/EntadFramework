using System.Collections.Generic;
using HarmonyLib;
using Verse;
using Verse.AI;
using RimWorld;

namespace EntadFramework
{
    public static class EntadMoods
    {
        public static void Give(Pawn pawn, ThoughtDef def, int durationTicks)
        {
            if (pawn?.needs?.mood == null || def == null) return;
            var memory = ThoughtMaker.MakeThought(def) as Thought_Memory;
            if (memory == null) return;
            memory.durationTicksOverride = durationTicks;
            pawn.needs.mood.thoughts.memories.TryGainMemory(memory);
        }

        // True while the pawn wears or wields an entad item whose trait uses this thought
        public static bool IsBackedByEquipment(Pawn pawn, ThoughtDef def)
        {
            var apparel = pawn.apparel?.WornApparel;
            if (apparel != null)
                for (int i = 0; i < apparel.Count; i++) if (ItemUses(apparel[i], def)) return true;
            var equipment = pawn.equipment?.AllEquipmentListForReading;
            if (equipment != null)
                for (int i = 0; i < equipment.Count; i++) if (ItemUses(equipment[i], def)) return true;
            return false;
        }

        private static bool ItemUses(Thing item, ThoughtDef def)
        {
            var comp = item.TryGetComp<CompEntad>();
            if (comp == null) return false;
            var mods = comp.activeTraits;
            for (int i = 0; i < mods.Count; i++) if (mods[i].thought == def) return true;
            return false;
        }

        // Brings the pawn's persistent equipment memories in line with what they currently wear or wield:
        // one memory per trait using a thought, up to that thought's stack limit.
        // 'removing' is excluded from the count, for use while an item is being taken off.
        public static void SyncEquipped(Pawn pawn, Thing removing = null)
        {
            var memories = pawn?.needs?.mood?.thoughts?.memories;
            if (memories == null) return;

            var wanted = new Dictionary<ThoughtDef, int>();
            Count(pawn.apparel?.WornApparel, removing, wanted);
            Count(pawn.equipment?.AllEquipmentListForReading, removing, wanted);

            var present = new Dictionary<ThoughtDef, List<Thought_EntadEquipped>>();
            var list = memories.Memories;
            for (int i = 0; i < list.Count; i++)
            {
                if (!(list[i] is Thought_EntadEquipped e)) continue;
                if (!present.TryGetValue(e.def, out var l)) present[e.def] = l = new List<Thought_EntadEquipped>();
                l.Add(e);
            }

            foreach (var kv in present)
            {
                wanted.TryGetValue(kv.Key, out int want);
                want = System.Math.Min(want, kv.Key.stackLimit);
                for (int i = kv.Value.Count - 1; i >= want; i--) memories.RemoveMemory(kv.Value[i]);
            }

            foreach (var kv in wanted)
            {
                int have = present.TryGetValue(kv.Key, out var l) ? System.Math.Min(l.Count, kv.Key.stackLimit) : 0;
                int want = System.Math.Min(kv.Value, kv.Key.stackLimit);
                for (int i = have; i < want; i++)
                {
                    var memory = new Thought_EntadEquipped { def = kv.Key, pawn = pawn, permanent = true };
                    memory.Init();
                    memories.Memories.Add(memory);
                }
            }
        }

        private static void Count<T>(List<T> items, Thing removing, Dictionary<ThoughtDef, int> wanted) where T : Thing
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == removing) continue;
                var entad = items[i].TryGetComp<CompEntad>();
                var mods = entad?.activeTraits;
                if (mods == null) continue;
                for (int j = 0; j < mods.Count; j++)
                {
                    ThoughtDef t = mods[j].thought;
                    if (t == null) continue;
                    mods[j].Reveal(EntadEffectKind.Mood);
                    wanted.TryGetValue(t, out int n);
                    wanted[t] = n + 1;
                }
            }
        }

        public static void OnFurnitureUsed(Pawn pawn, Thing furniture)
        {
            var comp = furniture?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty()) return;
            EntadStatReveal.Fire(comp, EntadStatTrigger.Use);
            foreach (var m in comp.activeTraits)
            {
                if (m.thought == null) continue;
                Give(pawn, m.thought, m.ThoughtDurationTicks);
                m.Reveal(EntadEffectKind.Mood);
            }
        }
    }

    // Furniture mood effects trigger when a pawn finishes (or is pulled off) a job that used the furniture.
    // CleanupCurrentJob is the single place every job ending passes through.
    // "Used" means the pawn sat or lay on it, worked a bill at it, researched at it, relaxed with it (joy jobs) or
    // ate at it. Hauling to it, repairing, refuelling, deconstructing or walking through it does not count.
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class Patch_CleanupCurrentJob_FurnitureMood
    {
        // Runs on every job end of every pawn, so it must stay allocation-free and bail out early.
        public static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn, JobCondition condition)
        {
            Job job = __instance.curJob;
            if (job == null || condition == JobCondition.Errored || condition == JobCondition.ErroredPather) return;
            if (job.def == JobDefOf.Goto) return;

            Pawn pawn = ___pawn;
            if (pawn == null || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike) return;

            Map map = pawn.MapHeld;
            if (map == null) return;

            // The building the job is about, when the job uses it: a bill at a workbench, research, or recreation
            Thing used = null;
            if (job.bill != null || job.def == JobDefOf.Research || job.def.joyKind != null) used = job.targetA.Thing;
            Check(pawn, used, null);

            // A chair or bed the pawn is sitting or lying on (not anything else on the cell, such as a door)
            List<Thing> here = pawn.Position.GetThingList(map);
            for (int i = 0; i < here.Count; i++)
            {
                Thing t = here[i];
                if (t.def.building != null && (t.def.building.isSittable || t.def.IsBed)) Check(pawn, t, used);
            }

            // The table the pawn ate at
            if (job.def == JobDefOf.Ingest) Check(pawn, EntadMeals.SurfaceThing(pawn), used);
        }

        // Applies the furniture's mood unless it was already handled as the job's own building
        private static void Check(Pawn pawn, Thing t, Thing skip)
        {
            if (t == null || t == skip || t.def.building == null) return;
            EntadMoods.OnFurnitureUsed(pawn, t);
        }
    }
}

namespace EntadFramework
{
    // A memory thought that lasts exactly as long as an equipped entad item uses it, and shows under its own name
    public class Thought_EntadEquipped : Thought_Memory
    {
        private bool computingMood;

        // Normally removed explicitly when equipment changes; this is only a safety net. MemoryThoughtHandler checks
        // ShouldDiscard on its own 150-tick interval, so a hash-tick gate here would almost never line up with it.
        // The check itself is a couple of short list scans, cheap enough at that rate.
        public override bool ShouldDiscard => pawn != null && !EntadMoods.IsBackedByEquipment(pawn, def);

        // The mood tab shows "expires in" whenever this is above a few ticks, ignoring the permanent flag,
        // so report no duration. Mood calculation still sees the real one (for lerpMoodToZero thoughts).
        public override int DurationTicks => computingMood ? def.DurationTicks : 0;

        // Never ages, so a lerpMoodToZero thought stays at full strength
        public override void ThoughtInterval()
        {
        }

        public override float MoodOffset()
        {
            computingMood = true;
            try { return base.MoodOffset(); }
            finally { computingMood = false; }
        }
    }
}
