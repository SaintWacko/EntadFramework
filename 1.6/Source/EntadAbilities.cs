using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // Abilities from entad modifiers are held by the pawn only while the item is worn/wielded
    public static class EntadAbilities
    {
        public static void Grant(Pawn pawn, CompEntad comp)
        {
            if (pawn?.abilities == null || comp == null) return;
            foreach (var m in comp.activeModifiers)
            {
                if (m.def.abilities == null) continue;
                foreach (AbilityDef ability in m.def.abilities)
                    if (pawn.abilities.GetAbility(ability) == null) pawn.abilities.GainAbility(ability);
            }
        }

        public static void Revoke(Pawn pawn, Thing removed)
        {
            var comp = removed?.TryGetComp<CompEntad>();
            if (pawn?.abilities == null || comp == null) return;

            var stillGranted = new HashSet<AbilityDef>();
            foreach (Thing other in Equipped(pawn))
            {
                if (other == removed) continue;
                var otherComp = other.TryGetComp<CompEntad>();
                if (otherComp == null) continue;
                foreach (var m in otherComp.activeModifiers)
                    if (m.def.abilities != null) stillGranted.UnionWith(m.def.abilities);
            }

            foreach (var m in comp.activeModifiers)
            {
                if (m.def.abilities == null) continue;
                foreach (AbilityDef ability in m.def.abilities)
                    if (!stillGranted.Contains(ability) && pawn.abilities.GetAbility(ability) != null)
                        pawn.abilities.RemoveAbility(ability);
            }
        }

        private static IEnumerable<Thing> Equipped(Pawn pawn)
        {
            if (pawn.apparel != null) foreach (Thing a in pawn.apparel.WornApparel) yield return a;
            if (pawn.equipment != null) foreach (Thing e in pawn.equipment.AllEquipmentListForReading) yield return e;
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentAdded))]
    public static class Patch_EquipmentAdded_Abilities
    {
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps eq) =>
            EntadAbilities.Grant(__instance.pawn, eq.GetComp<CompEntad>());
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentRemoved))]
    public static class Patch_EquipmentRemoved_Abilities
    {
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps eq) =>
            EntadAbilities.Revoke(__instance.pawn, eq);
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
    public static class Patch_ApparelAdded_Abilities
    {
        public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel) =>
            EntadAbilities.Grant(__instance.pawn, apparel.GetComp<CompEntad>());
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
    public static class Patch_ApparelRemoved_Abilities
    {
        public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel) =>
            EntadAbilities.Revoke(__instance.pawn, apparel);
    }
}
