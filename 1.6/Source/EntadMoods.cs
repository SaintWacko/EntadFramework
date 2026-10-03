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

        // Equipped items keep one memory alive: renew an existing one rather than stacking a new one
        public static void Refresh(Pawn pawn, ThoughtDef def, int durationTicks)
        {
            var memories = pawn?.needs?.mood?.thoughts?.memories;
            if (memories == null || def == null) return;
            var existing = memories.GetFirstMemoryOfDef(def);
            if (existing != null)
            {
                existing.age = 0;
                existing.durationTicksOverride = durationTicks;
                return;
            }
            Give(pawn, def, durationTicks);
        }

        // Applies thoughts and abilities of one equipped item
        public static void ApplyEquipped(Pawn pawn, Thing item)
        {
            var comp = item?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeModifiers.Count == 0) return;
            EntadAbilities.Grant(pawn, comp);
            foreach (var m in comp.activeModifiers)
                if (m.thought != null) Refresh(pawn, m.thought, EquippedRefreshTicks * 4);
        }

        public const int EquippedRefreshTicks = 150;

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

    // Weapons and apparel: refresh thoughts and abilities for equipped entad items.
    // Driven from the pawn so it doesn't depend on the item itself being ticked; renews rather than stacks.
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Tick))]
    public static class Patch_PawnTick_EquippedEntad
    {
        public static void Postfix(Pawn __instance)
        {
            if (!__instance.IsHashIntervalTick(EntadMoods.EquippedRefreshTicks)) return;
            if (__instance.needs?.mood == null) return;

            var apparel = __instance.apparel?.WornApparel;
            if (apparel != null)
                for (int i = 0; i < apparel.Count; i++) EntadMoods.ApplyEquipped(__instance, apparel[i]);

            var equipment = __instance.equipment?.AllEquipmentListForReading;
            if (equipment != null)
                for (int i = 0; i < equipment.Count; i++) EntadMoods.ApplyEquipped(__instance, equipment[i]);
        }
    }
}
