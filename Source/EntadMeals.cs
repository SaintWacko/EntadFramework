using System.Collections.Generic;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public static class EntadMeals
    {
        public static Thing SurfaceThing(Pawn pawn)
        {
            Map map = pawn?.MapHeld;
            if (map == null || !pawn.Spawned) return null;

            Thing surface = FindSurface(pawn.Position + pawn.Rotation.FacingCell, map);
            if (surface != null) return surface;
            foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(pawn))
            {
                surface = FindSurface(c, map);
                if (surface != null) return surface;
            }
            return null;
        }

        // Entad traits of the eating surface the pawn is using (the one they face, else any adjacent one)
        public static IEnumerable<AppliedEntadTrait> SurfaceTraits(Pawn pawn)
        {
            var comp = SurfaceThing(pawn)?.TryGetComp<CompEntad>();
            if (comp == null || !comp.ActiveFor(pawn)) yield break;
            foreach (var m in comp.activeTraits)
                if (m.def.HasMealEffect) yield return m;
        }

        public static bool IsMeal(ThingDef def) => def?.ingestible != null && (def.ingestible.foodType & FoodTypeFlags.Meal) != 0;

        // True only while a pawn is actually eating (inside Thing.Ingested). ThoughtsFromIngesting is also called
        // for every candidate while pawns pick food and pack food, and table effects must not run, reveal or skew
        // food choice there.
        [System.ThreadStatic] internal static bool eating;

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
        public static void Prefix(out bool __state)
        {
            __state = EntadMeals.eating;
            EntadMeals.eating = true;
        }

        public static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            EntadMeals.eating = __state;
            return __exception;
        }

        // Table nutrition applies to meals only, not drugs, raw food or corpses eaten at a table
        public static void Postfix(Thing __instance, Pawn ingester, ref float __result)
        {
            if (!EntadMeals.IsMeal(__instance.def)) return;
            foreach (var m in EntadMeals.SurfaceTraits(ingester))
            {
                __result *= m.mealNutritionFactor;
                if (m.def.mealNutritionFactor.min != 1f || m.def.mealNutritionFactor.max != 1f) m.Reveal(EntadEffectKind.Meal);
            }
        }
    }

    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.ThoughtsFromIngesting))]
    public static class Patch_ThoughtsFromIngesting_Meal
    {
        // Meal quality ladder, lowest first; a simple meal gives no quality thought (null)
        private static ThoughtDef[] ladder;

        private static ThoughtDef[] Ladder => ladder ?? (ladder = new[]
        {
            null,
            DefDatabase<ThoughtDef>.GetNamedSilentFail("AteFineMeal"),
            DefDatabase<ThoughtDef>.GetNamedSilentFail("AteLavishMeal"),
        });

        // Paste and survival meals have no quality thought to shift
        private static ThingDef paste, survival;
        private static bool resolved;

        private static bool IsQualityMeal(ThingDef foodDef)
        {
            if (!EntadMeals.IsMeal(foodDef)) return false;
            if (!resolved)
            {
                paste = DefDatabase<ThingDef>.GetNamedSilentFail("MealNutrientPaste");
                survival = DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack");
                resolved = true;
            }
            return foodDef != paste && foodDef != survival;
        }

        public static void Postfix(Pawn ingester, ThingDef foodDef, ref List<FoodUtility.ThoughtFromIngesting> __result)
        {
            if (!EntadMeals.eating) return;
            int qualityOffset = 0;
            foreach (var m in EntadMeals.SurfaceTraits(ingester))
            {
                qualityOffset += m.def.mealQualityOffset;
                if (m.def.mealQualityOffset != 0 && IsQualityMeal(foodDef)) m.Reveal(EntadEffectKind.Meal);
                if (m.def.mealThought == null) continue;
                m.Reveal(EntadEffectKind.Meal);
                __result = __result ?? new List<FoodUtility.ThoughtFromIngesting>();
                __result.Add(new FoodUtility.ThoughtFromIngesting { thought = m.def.mealThought });
            }

            if (qualityOffset != 0 && IsQualityMeal(foodDef)) ShiftQuality(ref __result, qualityOffset);
        }

        private static void ShiftQuality(ref List<FoodUtility.ThoughtFromIngesting> thoughts, int offset)
        {
            var steps = Ladder;
            int current = 0;
            int found = -1;
            if (thoughts != null)
            {
                for (int i = 0; i < thoughts.Count && found < 0; i++)
                {
                    int rung = System.Array.IndexOf(steps, thoughts[i].thought);
                    if (rung > 0) { current = rung; found = i; }
                }
            }

            int target = UnityEngine.Mathf.Clamp(current + offset, 0, steps.Length - 1);
            if (target == current || (target > 0 && steps[target] == null)) return;

            if (found >= 0) thoughts.RemoveAt(found);
            if (target > 0)
            {
                thoughts = thoughts ?? new List<FoodUtility.ThoughtFromIngesting>();
                thoughts.Add(new FoodUtility.ThoughtFromIngesting { thought = steps[target] });
            }
        }
    }
}
