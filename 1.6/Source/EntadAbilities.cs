using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // Abilities from entad traits are held by the pawn only while the item is worn/wielded
    public static class EntadAbilities
    {
        public static void Grant(Pawn pawn, CompEntad comp)
        {
            if (comp == null) return;
            // Stats that are always in effect (move speed, carrying capacity...) are noticed once the item is carried
            EntadStatReveal.Fire(comp, EntadStatTrigger.Equip);
            if (pawn?.abilities == null) return;
            foreach (var m in comp.activeTraits)
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
                foreach (var m in otherComp.activeTraits)
                    if (m.def.abilities != null) stillGranted.UnionWith(m.def.abilities);
            }

            foreach (var m in comp.activeTraits)
            {
                if (m.def.abilities == null) continue;
                foreach (AbilityDef ability in m.def.abilities)
                    if (!stillGranted.Contains(ability) && pawn.abilities.GetAbility(ability) != null)
                        pawn.abilities.RemoveAbility(ability);
            }
        }

        public static IEnumerable<AppliedEntadTrait> TraitsGranting(Pawn pawn, AbilityDef ability)
        {
            if (pawn == null) yield break;
            foreach (Thing item in Equipped(pawn))
            {
                var comp = item.TryGetComp<CompEntad>();
                if (comp == null) continue;
                foreach (var m in comp.activeTraits)
                    if (m.def.abilities != null && m.def.abilities.Contains(ability)) yield return m;
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
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps eq)
        {
            EntadAbilities.Grant(__instance.pawn, eq.GetComp<CompEntad>());
            EntadMoods.SyncEquipped(__instance.pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentRemoved))]
    public static class Patch_EquipmentRemoved_Abilities
    {
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps eq)
        {
            EntadAbilities.Revoke(__instance.pawn, eq);
            EntadMoods.SyncEquipped(__instance.pawn, eq);
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelAdded))]
    public static class Patch_ApparelAdded_Abilities
    {
        public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
        {
            EntadAbilities.Grant(__instance.pawn, apparel.GetComp<CompEntad>());
            EntadMoods.SyncEquipped(__instance.pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), nameof(Pawn_ApparelTracker.Notify_ApparelRemoved))]
    public static class Patch_ApparelRemoved_Abilities
    {
        public static void Postfix(Pawn_ApparelTracker __instance, Apparel apparel)
        {
            EntadAbilities.Revoke(__instance.pawn, apparel);
            EntadMoods.SyncEquipped(__instance.pawn, apparel);
        }
    }

    // Equipped abilities reveal their trait when cast, and show as "???" until then
    [HarmonyPatch(typeof(Ability), nameof(Ability.Activate), new[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo) })]
    public static class Patch_Ability_Activate_Reveal
    {
        public static void Postfix(Ability __instance)
        {
            foreach (var m in EntadAbilities.TraitsGranting(__instance.pawn, __instance.def))
                m.Reveal(EntadEffectKind.Ability);
        }
    }

    [HarmonyPatch(typeof(Ability), nameof(Ability.GetGizmos))]
    public static class Patch_Ability_GetGizmos_Hide
    {
        public static void Postfix(Ability __instance, ref IEnumerable<Gizmo> __result)
        {
            if (!EntadAbilities.TraitsGranting(__instance.pawn, __instance.def).Any(m => !m.IsRevealed(EntadEffectKind.Ability))) return;
            __result = Hide(__result);
        }

        private static IEnumerable<Gizmo> Hide(IEnumerable<Gizmo> gizmos)
        {
            foreach (Gizmo g in gizmos)
            {
                if (g is Command c)
                {
                    c.defaultLabel = "???";
                    c.defaultDesc = "The effect of this ability is not yet known.";
                    c.icon = TexButton.Info;
                    c.iconAngle = 0f;
                    c.iconOffset = UnityEngine.Vector2.zero;
                    c.iconDrawScale = 1f;
                }
                yield return g;
            }
        }
    }
}
