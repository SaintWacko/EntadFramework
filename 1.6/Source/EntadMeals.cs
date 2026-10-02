using System.Collections.Generic;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public static class EntadMeals
    {
        // Entad modifiers of the eating surface the pawn is using (the one they face, else any adjacent one)
        public static IEnumerable<AppliedEntadModifier> SurfaceModifiers(Pawn pawn)
        {
            Map map = pawn?.MapHeld;
            if (map == null || !pawn.Spawned) yield break;

            Thing surface = FindSurface(pawn.Position + pawn.Rotation.FacingCell, map);
            if (surface == null)
            {
                foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(pawn))
                {
                    surface = FindSurface(c, map);
                    if (surface != null) break;
                }
            }

            var comp = surface?.TryGetComp<CompEntad>();
            if (comp == null) yield break;
            foreach (var m in comp.activeModifiers)
                if (m.def.HasMealEffect) yield return m;
        }

        private static Thing FindSurface(IntVec3 cell, Map map)
        {
            if (!cell.InBounds(map)) return null;
            foreach (Thing t in cell.GetThingList(map))
                if (t.def.surfaceType == SurfaceType.Eat && t.TryGetComp<CompEntad>() != null) return t;
            return null;
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    public static class Patch_Ingested_MealNutrition
    {
        public static void Postfix(Pawn ingester, ref float __result)
        {
            foreach (var m in EntadMeals.SurfaceModifiers(ingester))
                __result *= m.mealNutritionFactor;
        }
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.ThoughtsFromIngesting))]
    public static class Patch_ThoughtsFromIngesting_Meal
    {
        public static void Postfix(Pawn ingester, ref List<FoodUtility.ThoughtFromIngesting> __result)
        {
            foreach (var m in EntadMeals.SurfaceModifiers(ingester))
            {
                if (m.def.mealThought == null) continue;
                __result = __result ?? new List<FoodUtility.ThoughtFromIngesting>();
                __result.Add(new FoodUtility.ThoughtFromIngesting { thought = m.def.mealThought });
            }
        }
    }
}
