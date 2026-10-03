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
        public static float Scale(float value, object owner, int property)
        {
            Thing thing = owner as Thing ?? (owner as ThingComp)?.parent;
            var entad = thing?.TryGetComp<CompEntad>();
            if (entad == null || entad.activeTraits.Count == 0) return value;
            if (entad.HasHidden) entad.RevealProperty((EntadBuildingProperty)property);
            return value * entad.PropertyFactor((EntadBuildingProperty)property);
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

    [HarmonyPatch]
    public static class Patch_TemperatureControlPower
    {
        private static readonly FieldInfo field = AccessTools.Field(typeof(CompProperties_TempControl), nameof(CompProperties_TempControl.energyPerSecond));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var m in EntadBuildingProperties.InstanceMethodsReading(typeof(Building_Heater), field)) yield return m;
            foreach (var m in EntadBuildingProperties.InstanceMethodsReading(typeof(Building_Cooler), field)) yield return m;
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> ins) => EntadBuildingProperties.ScaleFieldReads(ins, field, EntadBuildingProperty.TemperatureControlPower);
    }

    // Power use is stored as a negative output; scale it whenever the trader sets it
    [HarmonyPatch(typeof(CompPowerTrader), nameof(CompPowerTrader.PowerOutput), MethodType.Setter)]
    public static class Patch_PowerConsumption
    {
        public static void Prefix(CompPowerTrader __instance, ref float value)
        {
            if (value < 0f) value = EntadBuildingProperties.Scale(value, __instance, (int)EntadBuildingProperty.PowerConsumption);
        }
    }

    // Per-item fuel types. Instead of patching every place that reads the fuel filter, an affected item gets its
    // own copy of the refuelable properties with a different filter, so all the vanilla code just sees it.
    public static class EntadFuel
    {
        private static readonly Dictionary<CompRefuelable, CompProperties> originals = new Dictionary<CompRefuelable, CompProperties>();

        public static void Refresh(Thing thing, CompEntad entad)
        {
            var refuelable = thing.TryGetComp<CompRefuelable>();
            if (refuelable == null) return;

            var extra = new List<ThingDef>();
            bool replace = false;
            foreach (var m in entad.activeTraits)
            {
                if (m.def.AllFuelTypes.NullOrEmpty()) continue;
                extra.AddRange(m.def.AllFuelTypes);
                replace |= m.def.replaceFuel;
            }

            if (!originals.TryGetValue(refuelable, out var original))
            {
                if (extra.Count == 0) return;
                originals[refuelable] = original = refuelable.props;
            }
            else if (extra.Count == 0)
            {
                refuelable.props = original;
                originals.Remove(refuelable);
                return;
            }

            var baseProps = (CompProperties_Refuelable)original;
            var copy = (CompProperties_Refuelable)Clone(baseProps);
            var filter = new ThingFilter();
            if (!replace) filter.CopyAllowancesFrom(baseProps.fuelFilter);
            foreach (var d in extra) filter.SetAllow(d, true);
            copy.fuelFilter = filter;
            hiddenFuel.Remove(filter);
            hiddenFuel.Add(filter, new HiddenFuel { comp = entad, baseFilter = baseProps.fuelFilter });
            refuelable.props = copy;
        }

        public class HiddenFuel
        {
            public CompEntad comp;
            public ThingFilter baseFilter;
        }

        // Filters we created, so their summary can be kept to the building's normal fuel while the trait is hidden
        public static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ThingFilter, HiddenFuel> hiddenFuel =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ThingFilter, HiddenFuel>();

        private static readonly MethodInfo memberwiseClone = AccessTools.Method(typeof(object), "MemberwiseClone");
        private static object Clone(object o) => memberwiseClone.Invoke(o, null);
    }

    // Burning one of an item's entad fuel types reveals the trait that allows it
    [HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.Refuel), new[] { typeof(List<Thing>) })]
    public static class Patch_Refuel_Reveal
    {
        public static void Prefix(CompRefuelable __instance, List<Thing> fuelThings)
        {
            var entad = __instance.parent.TryGetComp<CompEntad>();
            if (entad == null || !entad.HasHidden || fuelThings == null) return;
            entad.RevealWhere(EntadEffectKind.Fuel, m => !m.def.AllFuelTypes.NullOrEmpty() && fuelThings.Any(t => m.def.AllFuelTypes.Contains(t.def)));
        }
    }

    // "Need granite blocks" and similar messages use the filter summary; show only the normal fuel until revealed
    [HarmonyPatch(typeof(ThingFilter), nameof(ThingFilter.Summary), MethodType.Getter)]
    public static class Patch_ThingFilter_Summary_HideFuel
    {
        public static void Postfix(ThingFilter __instance, ref string __result)
        {
            if (EntadFuel.hiddenFuel.TryGetValue(__instance, out var info) && info.comp.FuelHidden)
                __result = info.baseFilter.Summary;
        }
    }

    // Weapon effects (damage, accuracy...) reveal when the weapon hits something
    public static class EntadWeaponReveal
    {
        public static void Hit(Pawn pawn, bool melee)
        {
            if (pawn == null) return;
            var comp = pawn.equipment?.Primary?.TryGetComp<CompEntad>();
            if (comp != null && comp.HasHidden)
            {
                EntadStatReveal.Fire(comp, EntadStatTrigger.Hit);
                comp.RevealWhere(EntadEffectKind.Damage);
            }
            EntadStatReveal.Fire(pawn, melee ? EntadStatTrigger.MeleeAttack : EntadStatTrigger.Shoot);
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttackDamage), "ApplyMeleeDamageToTarget")]
    public static class Patch_MeleeHit_Reveal
    {
        public static void Postfix(Verb_MeleeAttackDamage __instance, LocalTargetInfo target, DamageWorker.DamageResult __result)
        {
            if (target.Thing is Pawn victim) EntadStatReveal.Fire(victim, EntadStatTrigger.MeleeDodge);
            if (__result != null && __result.totalDamageDealt > 0f) EntadWeaponReveal.Hit(__instance.CasterPawn, true);
        }
    }

    [HarmonyPatch(typeof(Projectile), "Impact")]
    public static class Patch_ProjectileHit_Reveal
    {
        public static void Prefix(Projectile __instance, Thing hitThing)
        {
            // Area weapons (incinerators, grenade launchers...) explode on the ground without hitting a thing
            if (hitThing != null || __instance.def.projectile.explosionRadius > 0f) EntadWeaponReveal.Hit(Traverse.Create(__instance).Field("launcher").GetValue<Thing>() as Pawn, false);
        }
    }

    // Beam weapons never create a projectile, so a successful shot counts as a hit
    [HarmonyPatch(typeof(Verb_ShootBeam), "TryCastShot")]
    public static class Patch_BeamShot_Reveal
    {
        public static void Postfix(Verb_ShootBeam __instance, bool __result)
        {
            if (__result) EntadWeaponReveal.Hit(__instance.CasterPawn, false);
        }
    }
}
