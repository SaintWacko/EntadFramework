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
            bool wasBound = comp.IsBound;
            comp.TryBindOnUse(pawn);
            // A first-use bind already re-ran Grant for the holder (BindingChanged), so don't do it all twice
            if (!wasBound && comp.IsBound && comp.Holder == pawn) return;
            comp.HolderChanged();
            if (!comp.ActiveFor(pawn)) return;
            // Stats that are always in effect (move speed, carrying capacity...) are noticed once the item is carried
            EntadStatReveal.Fire(comp, EntadStatTrigger.Equip);
            EntadWeaponTraits.AddHediffs(pawn, comp);
            if (pawn?.abilities == null) return;
            foreach (var m in comp.activeTraits)
            {
                if (m.def.abilities == null) continue;
                for (int i = 0; i < m.def.abilities.Count; i++)
                {
                    AbilityDef ability = m.def.abilities[i];
                    if (pawn.abilities.GetAbility(ability) != null) continue;
                    pawn.abilities.GainAbility(ability);
                    // Reloadable charges live on the item: a new Ability starts full, so put back what is left
                    if (m.def.IsReloadable)
                    {
                        var granted = pawn.abilities.GetAbility(ability);
                        if (granted != null) granted.RemainingCharges = m.StoredCharges(i);
                    }
                }
            }
        }

        public static void Revoke(Pawn pawn, Thing removed)
        {
            var comp = removed?.TryGetComp<CompEntad>();
            comp?.HolderChanged();
            if (comp != null) EntadWeaponTraits.RemoveHediffs(pawn, comp, Equipped(pawn));
            if (pawn?.abilities == null || comp == null) return;

            var stillGranted = new HashSet<AbilityDef>();
            foreach (Thing other in Equipped(pawn))
            {
                if (other == removed) continue;
                var otherComp = other.TryGetComp<CompEntad>();
                if (otherComp == null || !otherComp.ActiveFor(pawn)) continue;
                foreach (var m in otherComp.activeTraits)
                    if (m.def.abilities != null) stillGranted.UnionWith(m.def.abilities);
            }

            foreach (var m in comp.activeTraits)
            {
                if (m.def.abilities == null) continue;
                for (int i = 0; i < m.def.abilities.Count; i++)
                {
                    AbilityDef ability = m.def.abilities[i];
                    var held = pawn.abilities.GetAbility(ability);
                    if (held == null) continue;
                    // Store even when other gear keeps the ability: two items share one pawn Ability, and an item
                    // that wrote nothing back would come off still reading full, refilled for free
                    if (m.def.IsReloadable) m.SetStoredCharges(i, held.RemainingCharges);
                    if (stillGranted.Contains(ability)) continue;
                    pawn.abilities.RemoveAbility(ability);
                }
            }
        }

        public static IEnumerable<AppliedEntadTrait> TraitsGranting(Pawn pawn, AbilityDef ability)
        {
            if (pawn == null) yield break;
            foreach (Thing item in Equipped(pawn))
            {
                var comp = item.TryGetComp<CompEntad>();
                if (comp == null || !comp.ActiveFor(pawn)) continue;
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
        private static readonly AccessTools.FieldRef<Command_Ability, string> OriginalLabel = AccessTools.FieldRefAccess<Command_Ability, string>("originalLabel");
        private static readonly AccessTools.FieldRef<Command_Ability, string> PawnLabel = AccessTools.FieldRefAccess<Command_Ability, string>("pawnLabel");

        public static bool IsHidden(Ability ability) =>
            EntadAbilities.TraitsGranting(ability.pawn, ability.def).Any(m => !m.IsRevealed(EntadEffectKind.Ability));

        // The game caches the gizmo, so it is updated in place each time it is requested (hiding it, or restoring it once revealed).
        // Command_Ability rebuilds its label from the fields below every frame, so they have to be changed too.
        public static void Postfix(Ability __instance, ref IEnumerable<Command> __result)
        {
            if (!EntadAbilities.TraitsGranting(__instance.pawn, __instance.def).Any()) return;
            bool hidden = IsHidden(__instance);
            var list = __result.ToList();
            foreach (Command c in list)
            {
                if (!(c is Command_Ability ca)) continue;
                string label = hidden ? "EF_Unknown".Translate().ToString() : __instance.def.LabelCap.ToString();
                ca.defaultLabel = label;
                OriginalLabel(ca) = label;
                PawnLabel(ca) = label;
                ca.icon = hidden ? TexButton.Info : __instance.def.uiIcon;
                ca.iconAngle = 0f;
                ca.iconOffset = UnityEngine.Vector2.zero;
                ca.iconDrawScale = 1f;
            }
            __result = list;
        }
    }

    // The tooltip is read straight from the ability, so veil it while the trait is unrevealed
    [HarmonyPatch(typeof(Command_Ability), nameof(Command_Ability.Tooltip), MethodType.Getter)]
    public static class Patch_CommandAbility_Tooltip_Hide
    {
        public static void Postfix(Command_Ability __instance, ref string __result)
        {
            if (__instance.Ability != null && Patch_Ability_GetGizmos_Hide.IsHidden(__instance.Ability))
                // Command_Ability inserts the pawn's name after the (colored) title when it is built, so keep this longer than any title
                __result = "EF_Unknown".Translate().ToString().Colorize(ColoredText.TipSectionTitleColor) + "\n\n" + "EF_AbilityUnknown".Translate();
        }
    }
}
