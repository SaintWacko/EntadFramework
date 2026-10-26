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

        // Every ability some trait grants, built on first use (after EntadAbilityPrep has swapped in its copies), so the
        // per-frame checks below return after one lookup for a psycaster's own psycasts
        private static HashSet<AbilityDef> grantable;

        private static bool Grantable(AbilityDef ability)
        {
            if (grantable == null)
            {
                var set = new HashSet<AbilityDef>();
                foreach (var def in DefDatabase<EntadTraitDef>.AllDefsListForReading)
                    if (def.abilities != null) set.UnionWith(def.abilities);
                grantable = set;
            }
            return grantable.Contains(ability);
        }

        // Whether gear the pawn wears or wields grants this ability through an active trait, and whether any such trait
        // is still hidden. Runs every frame for each ability gizmo, tooltip and cursor, so plain loops and no iterators.
        public static bool Grants(Pawn pawn, AbilityDef ability, out bool hidden)
        {
            hidden = false;
            if (pawn == null || ability == null || !Grantable(ability)) return false;
            bool any = false;
            var worn = pawn.apparel?.WornApparel;
            if (worn != null) for (int i = 0; i < worn.Count; i++) Check(worn[i], pawn, ability, ref any, ref hidden);
            var held = pawn.equipment?.AllEquipmentListForReading;
            if (held != null) for (int i = 0; i < held.Count; i++) Check(held[i], pawn, ability, ref any, ref hidden);
            return any;
        }

        private static void Check(ThingWithComps item, Pawn pawn, AbilityDef ability, ref bool any, ref bool hidden)
        {
            var comp = item.GetComp<CompEntad>();
            if (comp == null || !comp.ActiveFor(pawn)) return;
            var traits = comp.activeTraits;
            for (int i = 0; i < traits.Count; i++)
            {
                var abilities = traits[i].def.abilities;
                if (abilities == null || !abilities.Contains(ability)) continue;
                any = true;
                if (!traits[i].IsRevealed(EntadEffectKind.Ability)) hidden = true;
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

    // Equip and unequip. Vanilla's Pawn_EquipmentTracker.Notify_EquipmentAdded/Removed and
    // Pawn_ApparelTracker.Notify_ApparelAdded/Removed are the only callers of Thing.Notify_Equipped/Unequipped;
    // ThingWithComps passes it on to every comp. Until build 183 this was four Harmony postfixes on those tracker
    // methods, and the two apparel methods call the item last, so nothing moved there. The weapon methods don't:
    // - Notify_EquipmentAdded then tells the pawn's bonded weapon's CompBladelinkWeapon that another weapon was
    //   wielded. That line may now see the entad BondOnEquip just bonded, which does nothing, since an entad persona
    //   never has a CompBladelinkWeapon.
    // - Notify_EquipmentRemoved calls the item first, then the bladelink and unique weapon comps, whose trait workers
    //   remove equippedHediffs by def, as RemoveHediffs does. On a unique weapon with entad traits both may act on one
    //   hediff; whichever runs first, the pawn ends with the same hediffs.
    // The postfixes also ran for every item on every pawn: each piece of apparel put on a generated raider ran
    // SyncEquipped and its two dictionaries. The overrides run only for entads.
    public partial class CompEntad
    {
        public override void Notify_Equipped(Pawn pawn)
        {
            base.Notify_Equipped(pawn);
            lastWearer = pawn;
            // The same order as the old postfixes: a persona bond first, then abilities, hediffs and moods
            BondOnEquip(pawn);
            EntadAbilities.Grant(pawn, this);
            EntadMoods.SyncEquipped(pawn);
        }

        public override void Notify_Unequipped(Pawn pawn)
        {
            base.Notify_Unequipped(pawn);
            // A destroyed item is unequipped on its way out, before PostDestroy needs to know who wore it
            if (lastWearer == pawn && !parent.Destroyed) lastWearer = null;
            EntadAbilities.Revoke(pawn, parent);
            EntadMoods.SyncEquipped(pawn, parent);
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

        public static bool IsHidden(Ability ability) => EntadAbilities.Grants(ability.pawn, ability.def, out bool hidden) && hidden;

        // The game caches the gizmo, so it is updated in place each time it is requested (hiding it, or restoring it once revealed).
        // Command_Ability rebuilds its label from the fields below every frame, so they have to be changed too.
        // Runs every frame per ability of each selected pawn: abilities no entad grants return after one set lookup, and
        // the rest are dressed as vanilla's iterator yields them, with no list copy. The gizmo is made lazily inside that
        // iterator, so it can't simply be edited here before enumeration.
        public static void Postfix(Ability __instance, ref IEnumerable<Command> __result)
        {
            if (!EntadAbilities.Grants(__instance.pawn, __instance.def, out bool hidden)) return;
            __result = Dressed(__result, __instance, hidden);
        }

        private static IEnumerable<Command> Dressed(IEnumerable<Command> source, Ability ability, bool hidden)
        {
            foreach (Command c in source)
            {
                if (c is Command_Ability ca)
                {
                    string label = hidden ? "EF_Unknown".Translate().ToString() : ability.def.LabelCap.ToString();
                    ca.defaultLabel = label;
                    OriginalLabel(ca) = label;
                    PawnLabel(ca) = label;
                    ca.icon = hidden ? TexButton.Info : ability.def.uiIcon;
                    ca.iconAngle = 0f;
                    ca.iconOffset = UnityEngine.Vector2.zero;
                    ca.iconDrawScale = 1f;
                }
                yield return c;
            }
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
            // Every ability name of the selected pawns that appears in the message, and whether any holder has it hidden
            Dictionary<string, bool> names = null;
            var selected = Find.Selector.SelectedObjectsListForReading;
            for (int i = 0; i < selected.Count; i++)
                if (selected[i] is Pawn pawn) Collect(msg.text, pawn, ref names);
            // World-tile abilities can be aimed from a caravan, which lives in the world selector
            var world = Find.WorldSelector?.SelectedObjects;
            if (world != null)
                for (int i = 0; i < world.Count; i++)
                    if (world[i] is Caravan caravan)
                        foreach (Pawn p in caravan.PawnsListForReading) Collect(msg.text, p, ref names);
            if (names == null || !names.ContainsValue(true)) return;

            // One pass over every name, longest first, so a revealed "farskip" is matched as itself before a hidden "skip"
            // could match inside it. Names in scripts that put spaces between words also have to stand as whole words, so a
            // pawn called Skipper keeps his name; names in Thai, Chinese and the like can't be bounded that way and rely on
            // the longest-first order alone. The evaluator keeps a translated '$' from being read as a substitution.
            string pattern = string.Join("|", names.Keys.OrderByDescending(k => k.Length).Select(Bounded));
            string unknown = "EF_Unknown".Translate().ToString();
            msg.text = System.Text.RegularExpressions.Regex.Replace(msg.text, pattern,
                m => names.TryGetValue(m.Value, out bool hidden) && hidden ? unknown : m.Value,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        // A neighbouring letter that would make the match part of a longer word: spaced scripts only, the same ranges
        // IsSpacedLetter leaves out are subtracted, so "使用Waterskip失败" still counts as the whole name
        private const string WordChar = @"[\p{L}\p{M}\p{N}-[\u0E00-\u0EFF\u1000-\u109F\u1100-\u11FF\u1780-\u17FF\u2E80-\u9FFF\uAC00-\uD7FF\uF900-\uFAFF\uFF65-\uFFDC]]";

        private static string Bounded(string label)
        {
            string p = System.Text.RegularExpressions.Regex.Escape(label);
            if (IsSpacedLetter(label[0])) p = "(?<!" + WordChar + ")" + p;
            if (IsSpacedLetter(label[label.Length - 1])) p += "(?!" + WordChar + ")";
            return "(?:" + p + ")";
        }

        // A letter, digit or combining mark in a script that puts spaces between words. Thai, Lao, Myanmar and Khmer run
        // words together, Chinese and Japanese too, and Korean attaches particles straight onto nouns: no guard there.
        private static bool IsSpacedLetter(char c)
        {
            var cat = char.GetUnicodeCategory(c);
            if (!char.IsLetterOrDigit(c) && cat != System.Globalization.UnicodeCategory.NonSpacingMark
                && cat != System.Globalization.UnicodeCategory.SpacingCombiningMark) return false;
            if (c >= '\u0E00' && c <= '\u0EFF') return false; // Thai, Lao
            if (c >= '\u1000' && c <= '\u109F') return false; // Myanmar
            if (c >= '\u1100' && c <= '\u11FF') return false; // Hangul jamo
            if (c >= '\u1780' && c <= '\u17FF') return false; // Khmer
            if (c >= '\u2E80' && c <= '\u9FFF') return false; // CJK, kana
            if (c >= '\uAC00' && c <= '\uD7FF') return false; // Hangul syllables
            if (c >= '\uD800' && c <= '\uDFFF') return false; // surrogate halves
            if (c >= '\uF900' && c <= '\uFAFF') return false; // CJK compatibility
            if (c >= '\uFF65' && c <= '\uFFDC') return false; // halfwidth kana, Hangul
            return true;
        }

        private static void Collect(string text, Pawn pawn, ref Dictionary<string, bool> names)
        {
            if (pawn.abilities != null)
                foreach (Ability a in pawn.abilities.abilities)
                    Note(text, a.def.label, Patch_Ability_GetGizmos_Hide.IsHidden(a), ref names);
            // Another system's abilities (a module's externalAbilities) never reach pawn.abilities: read them off the gear
            if (!EntadCompat.HasExternalAbilities) return;
            var worn = pawn.apparel?.WornApparel;
            if (worn != null) for (int i = 0; i < worn.Count; i++) CollectExternal(text, worn[i], pawn, ref names);
            var held = pawn.equipment?.AllEquipmentListForReading;
            if (held != null) for (int i = 0; i < held.Count; i++) CollectExternal(text, held[i], pawn, ref names);
        }

        private static void CollectExternal(string text, ThingWithComps item, Pawn pawn, ref Dictionary<string, bool> names)
        {
            var comp = item.GetComp<CompEntad>();
            if (comp == null || !comp.ActiveFor(pawn)) return;
            foreach (var trait in comp.activeTraits)
            {
                var external = trait.def.externalAbilities;
                if (external == null) continue;
                bool hidden = !trait.IsRevealed(EntadEffectKind.Ability);
                for (int j = 0; j < external.Count; j++) Note(text, EntadCompat.ExternalAbilityLabel(external[j]), hidden, ref names);
            }
        }

        private static void Note(string text, string label, bool hidden, ref Dictionary<string, bool> names)
        {
            if (label.NullOrEmpty() || text.IndexOf(label, System.StringComparison.OrdinalIgnoreCase) < 0) return;
            if (names == null) names = new Dictionary<string, bool>(System.StringComparer.OrdinalIgnoreCase);
            names[label] = hidden || (names.TryGetValue(label, out bool before) && before);
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
