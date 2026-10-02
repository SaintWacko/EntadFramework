using System.Linq;
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

        public static void OnFurnitureUsed(Pawn pawn, Thing furniture)
        {
            var comp = furniture?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return;
            foreach (var m in comp.activeModifiers)
                if (m.thought != null) Give(pawn, m.thought, m.ThoughtDurationTicks);
        }
    }

    // Furniture mood effects trigger when a pawn finishes a job that used the furniture
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_EndCurrentJob_FurnitureMood
    {
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Job job = __instance.curJob;
            Pawn pawn = __instance.pawn;
            if (job == null || pawn == null || !pawn.IsColonist || condition != JobCondition.Succeeded) return;
            if (job.def == JobDefOf.Goto) return;

            Map map = pawn.MapHeld;
            if (map == null) return;

            LocalTargetInfo[] targets = { job.targetA, job.targetB, job.targetC };
            foreach (LocalTargetInfo target in targets)
            {
                Thing t = target.Thing;
                if (t != null && t.def.building != null) EntadMoods.OnFurnitureUsed(pawn, t);
            }

            // Chairs, beds etc. the pawn is sitting/lying on
            foreach (Thing t in pawn.Position.GetThingList(map))
                if (t.def.building != null && !targets.Any(x => x.Thing == t)) EntadMoods.OnFurnitureUsed(pawn, t);
        }
    }
}
