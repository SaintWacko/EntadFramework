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
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class Patch_CleanupCurrentJob_FurnitureMood
    {
        // Runs on every job end of every pawn, so it must stay allocation-free and bail out early.
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Job job = __instance.curJob;
            if (job == null || condition == JobCondition.Errored || condition == JobCondition.ErroredPather) return;
            if (job.def == JobDefOf.Goto) return;

            Pawn pawn = __instance.pawn;
            if (pawn == null || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike) return;

            Map map = pawn.MapHeld;
            if (map == null) return;

            Check(pawn, job.targetA.Thing, null);
            Check(pawn, job.targetB.Thing, job.targetA.Thing);
            Check(pawn, job.targetC.Thing, job.targetA.Thing, job.targetB.Thing);

            // Chairs, beds etc. the pawn is sitting/lying on
            List<Thing> here = pawn.Position.GetThingList(map);
            for (int i = 0; i < here.Count; i++) Check(pawn, here[i], job.targetA.Thing, job.targetB.Thing, job.targetC.Thing);

            // Tables the pawn is eating at
            if (job.def == JobDefOf.Ingest)
                foreach (Thing t in EntadMeals.SurfaceThings(pawn)) Check(pawn, t, job.targetA.Thing, job.targetB.Thing, job.targetC.Thing);
        }

        // Applies the furniture's mood unless it was already handled via one of the earlier targets (skip1..3)
        private static void Check(Pawn pawn, Thing t, Thing skip1, Thing skip2 = null, Thing skip3 = null)
        {
            if (t == null || t.def.building == null) return;
            if (t == skip1 || t == skip2 || t == skip3) return;
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

        // Normally removed explicitly when equipment changes; this is only a cheap periodic safety net
        public override bool ShouldDiscard => pawn != null && pawn.IsHashIntervalTick(250) && !EntadMoods.IsBackedByEquipment(pawn, def);

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
