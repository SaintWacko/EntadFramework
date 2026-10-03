using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Applies EntadBuildingProperty factors. Comp properties are shared by every item of a def, so rather than
    // editing them we scale the value where each component reads it.
    public static class EntadBuildingProperties
    {
        public static float Scale(float value, ThingComp comp, int property)
        {
            var entad = comp?.parent?.GetComp<CompEntad>();
            return entad == null || entad.activeModifiers.Count == 0 ? value : value * entad.PropertyFactor((EntadBuildingProperty)property);
        }

        private static readonly MethodInfo scale = AccessTools.Method(typeof(EntadBuildingProperties), nameof(Scale));

        // After every read of 'field' in an instance method of the comp, multiply the value by the item's factor
        public static IEnumerable<CodeInstruction> ScaleFieldReads(IEnumerable<CodeInstruction> instructions, FieldInfo field, EntadBuildingProperty property)
        {
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.opcode == OpCodes.Ldfld && ins.operand as FieldInfo == field)
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldc_I4, (int)property);
                    yield return new CodeInstruction(OpCodes.Call, scale);
                }
            }
        }

        public static IEnumerable<MethodBase> InstanceMethodsReading(System.Type comp, FieldInfo field)
        {
            foreach (var m in AccessTools.GetDeclaredMethods(comp))
            {
                if (m.IsStatic || m.IsAbstract) continue;
                var body = m.GetMethodBody();
                if (body == null) continue;
                var il = PatchProcessor.GetCurrentInstructions(m);
                foreach (var ins in il)
                    if (ins.opcode == OpCodes.Ldfld && ins.operand as FieldInfo == field) { yield return m; break; }
            }
        }
    }

    [HarmonyPatch]
    public static class Patch_FuelConsumptionRate
    {
        private static readonly FieldInfo field = AccessTools.Field(typeof(CompProperties_Refuelable), nameof(CompProperties_Refuelable.fuelConsumptionRate));
        public static IEnumerable<MethodBase> TargetMethods() => EntadBuildingProperties.InstanceMethodsReading(typeof(CompRefuelable), field);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins) => EntadBuildingProperties.ScaleFieldReads(ins, field, EntadBuildingProperty.FuelConsumptionRate);
    }

    [HarmonyPatch]
    public static class Patch_FuelCapacity
    {
        private static readonly FieldInfo field = AccessTools.Field(typeof(CompProperties_Refuelable), nameof(CompProperties_Refuelable.fuelCapacity));
        public static IEnumerable<MethodBase> TargetMethods() => EntadBuildingProperties.InstanceMethodsReading(typeof(CompRefuelable), field);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins) => EntadBuildingProperties.ScaleFieldReads(ins, field, EntadBuildingProperty.FuelCapacity);
    }

    [HarmonyPatch]
    public static class Patch_HeatOutput
    {
        private static readonly FieldInfo field = AccessTools.Field(typeof(CompProperties_HeatPusher), nameof(CompProperties_HeatPusher.heatPerSecond));
        public static IEnumerable<MethodBase> TargetMethods() => EntadBuildingProperties.InstanceMethodsReading(typeof(CompHeatPusher), field);
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins) => EntadBuildingProperties.ScaleFieldReads(ins, field, EntadBuildingProperty.HeatOutput);
    }

    [HarmonyPatch(typeof(CompGlower), nameof(CompGlower.GlowRadius), MethodType.Getter)]
    public static class Patch_LightRadius
    {
        public static void Postfix(CompGlower __instance, ref float __result)
        {
            __result = EntadBuildingProperties.Scale(__result, __instance, (int)EntadBuildingProperty.LightRadius);
        }
    }

    // Each generator type computes its own output, so patch every override
    [HarmonyPatch]
    public static class Patch_PowerGeneration
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in typeof(CompPowerPlant).AllSubclasses().Concat(new[] { typeof(CompPowerPlant) }))
            {
                var getter = AccessTools.DeclaredPropertyGetter(t, "DesiredPowerOutput");
                if (getter != null) yield return getter;
            }
        }

        public static void Postfix(CompPowerPlant __instance, ref float __result)
        {
            __result = EntadBuildingProperties.Scale(__result, __instance, (int)EntadBuildingProperty.PowerGeneration);
        }
    }
}
