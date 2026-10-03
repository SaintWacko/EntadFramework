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

        // True while the pawn wears or wields an entad item whose modifier uses this thought
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
            var mods = comp.activeModifiers;
            for (int i = 0; i < mods.Count; i++) if (mods[i].thought == def) return true;
            return false;
        }

        // Gives the pawn one persistent memory per thought used by their equipped items
        public static void SyncEquipped(Pawn pawn, Thing item)
        {
            var memories = pawn?.needs?.mood?.thoughts?.memories;
            var comp = item?.TryGetComp<CompEntad>();
            if (memories == null || comp == null) return;
            foreach (var m in comp.activeModifiers)
            {
                if (m.thought == null || memories.GetFirstMemoryOfDef(m.thought) != null) continue;
                var memory = new Thought_EntadEquipped { def = m.thought, pawn = pawn };
                memory.Init();
                memories.TryGainMemory(memory);
            }
        }

        public static void OnFurnitureUsed(Pawn pawn, Thing furniture)
        {
            var comp = furniture?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return;
            foreach (var m in comp.activeModifiers)
                if (m.thought != null) Give(pawn, m.thought, m.ThoughtDurationTicks);
        }
    }

    // Furniture mood effects trigger when a pawn finishes (or is pulled off) a job that used the furniture.
    // CleanupCurrentJob is the single place every job ending passes through.
    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class Patch_CleanupCurrentJob_FurnitureMood
    {
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Job job = __instance.curJob;
            Pawn pawn = __instance.pawn;
            if (job == null || pawn == null || pawn.Faction != Faction.OfPlayer || !pawn.RaceProps.Humanlike) return;
            if (condition == JobCondition.Errored || condition == JobCondition.ErroredPather) return;
            if (job.def == JobDefOf.Goto) return;

            Map map = pawn.MapHeld;
            if (map == null) return;

            var used = new HashSet<Thing>();
            foreach (LocalTargetInfo target in new[] { job.targetA, job.targetB, job.targetC })
            {
                Thing t = target.Thing;
                if (t != null && t.def.building != null) used.Add(t);
            }

            // Chairs, beds etc. the pawn is sitting/lying on
            foreach (Thing t in pawn.Position.GetThingList(map))
                if (t.def.building != null) used.Add(t);

            // Tables the pawn is eating at
            if (job.def == JobDefOf.Ingest)
                foreach (Thing t in EntadMeals.SurfaceThings(pawn)) used.Add(t);

            foreach (Thing t in used) EntadMoods.OnFurnitureUsed(pawn, t);
        }
    }
}

namespace EntadFramework
{
    // A memory thought that lasts exactly as long as an equipped entad item uses it, and shows under its own name
    public class Thought_EntadEquipped : Thought_Memory
    {
        public override bool ShouldDiscard => !EntadMoods.IsBackedByEquipment(pawn, def);
    }
}
