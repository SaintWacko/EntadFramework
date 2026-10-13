using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using RimWorld;
using RimWorld.Planet;

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

    // World-tile abilities (Farskip and the like) are cast through the other overload
    [HarmonyPatch(typeof(Ability), nameof(Ability.Activate), new[] { typeof(GlobalTargetInfo) })]
    public static class Patch_Ability_ActivateGlobal_Reveal
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

    // While targeting, the cursor carries the verb's icon, which Verb_CastAbility reads straight from the ability def.
    // Swap it for the same question-mark icon the hidden gizmo shows. Read only while the player is aiming the ability.
    [HarmonyPatch(typeof(Verb_CastAbility), nameof(Verb_CastAbility.UIIcon), MethodType.Getter)]
    public static class Patch_VerbCastAbility_UIIcon_Hide
    {
        public static void Postfix(Verb_CastAbility __instance, ref UnityEngine.Texture2D __result)
        {
            var ability = __instance.ability;
            if (ability?.pawn != null && Patch_Ability_GetGizmos_Hide.IsHidden(ability)) __result = TexButton.Info;
        }
    }

    // The text beside the cursor while aiming (Chunkskip's chunk count, a conversion chance and so on) says what the
    // ability does, so leave it out while hidden. Runs only while the player is aiming.
    [HarmonyPatch(typeof(Verb_CastAbility), "DrawAttachmentExtraLabel")]
    public static class Patch_VerbCastAbility_ExtraLabel_Hide
    {
        public static bool Prefix(Verb_CastAbility __instance)
        {
            var ability = __instance.ability;
            return ability?.pawn == null || !Patch_Ability_GetGizmos_Hide.IsHidden(ability);
        }
    }

    // Two-step abilities (Skip and other teleports) draw the def's icon themselves while the destination is picked
    [HarmonyPatch(typeof(CompAbilityEffect_WithDest), nameof(CompAbilityEffect_WithDest.OnGUI))]
    public static class Patch_WithDest_OnGUI_Hide
    {
        public static bool Prefix(CompAbilityEffect_WithDest __instance, LocalTargetInfo target)
        {
            var ability = __instance.parent;
            if (ability?.pawn == null || !Patch_Ability_GetGizmos_Hide.IsHidden(ability)) return true;
            GenUI.DrawMouseAttachment(target.IsValid ? TexButton.Info : TexCommand.CannotShoot);
            return false;
        }
    }

    // World-tile abilities: Command_Ability hands the def's icon straight to the world targeter, so swap it once
    // targeting has begun. Runs once per click on the gizmo.
    [HarmonyPatch(typeof(Command_Ability), nameof(Command_Ability.ProcessInput))]
    public static class Patch_CommandAbility_ProcessInput_Hide
    {
        private static readonly AccessTools.FieldRef<WorldTargeter, UnityEngine.Texture2D> MouseAttachment =
            AccessTools.FieldRefAccess<WorldTargeter, UnityEngine.Texture2D>("mouseAttachment");

        public static void Postfix(Command_Ability __instance)
        {
            var ability = __instance.Ability;
            if (ability?.pawn == null || !ability.def.targetWorldCell || !Find.WorldTargeter.IsTargeting) return;
            if (Patch_Ability_GetGizmos_Hide.IsHidden(ability)) MouseAttachment(Find.WorldTargeter) = TexButton.Info;
        }
    }

    // The label beside the cursor on the world map
    [HarmonyPatch(typeof(Ability), nameof(Ability.WorldMapExtraLabel))]
    public static class Patch_Ability_WorldMapExtraLabel_Hide
    {
        public static void Postfix(Ability __instance, ref TaggedString __result)
        {
            if (__instance.pawn != null && Patch_Ability_GetGizmos_Hide.IsHidden(__instance)) __result = TaggedString.Empty;
        }
    }

    // Rejections while aiming name the ability ("Waterskip: cells are occupied", "no valid destination for skip").
    // While a selected pawn holds a hidden entad ability, its name in a rejection message becomes "???". Only
    // RejectInput messages are looked at, and only while playing, so this costs nothing outside a misclick.
    [HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
    public static class Patch_Messages_HideAbilityName
    {
        public static void Prefix(Message msg)
        {
            if (msg == null || msg.def != MessageTypeDefOf.RejectInput || msg.text.NullOrEmpty()) return;
            if (Current.ProgramState != ProgramState.Playing) return;
            var selected = Find.Selector.SelectedObjectsListForReading;
            for (int i = 0; i < selected.Count; i++)
            {
                if (!(selected[i] is Pawn pawn) || pawn.abilities == null) continue;
                foreach (Ability a in pawn.abilities.abilities)
                {
                    string label = a.def.label;
                    if (label.NullOrEmpty() || msg.text.IndexOf(label, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (!Patch_Ability_GetGizmos_Hide.IsHidden(a)) continue;
                    msg.text = System.Text.RegularExpressions.Regex.Replace(msg.text, System.Text.RegularExpressions.Regex.Escape(label),
                        "EF_Unknown".Translate().ToString(), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
            }
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
