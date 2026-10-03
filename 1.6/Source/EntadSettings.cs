using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    public class EntadSettings : ModSettings
    {
        public static readonly Dictionary<EntadRarity, float> DefaultWeights = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, 50f },
            { EntadRarity.Uncommon, 25f },
            { EntadRarity.Rare, 12f },
            { EntadRarity.Epic, 5f },
            { EntadRarity.Legendary, 3f },
        };

        // Relative weights for choosing a modifier's rarity; used by EntadRarityChances.Default
        // Multiplier on the ranges of rarity-scaling modifiers at each rarity (relative to the modifier's own rarity)
        public static readonly Dictionary<EntadRarity, float> DefaultMultipliers = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, 1f },
            { EntadRarity.Uncommon, 1.25f },
            { EntadRarity.Rare, 1.5f },
            { EntadRarity.Epic, 2f },
            { EntadRarity.Legendary, 3f },
        };

        public static readonly Dictionary<EntadRarity, float> Multipliers = new Dictionary<EntadRarity, float>(DefaultMultipliers);

        public static float RarityMultiplier(EntadRarity r) => Multipliers[r];

        public static readonly Dictionary<EntadRarity, float> Weights = new Dictionary<EntadRarity, float>(DefaultWeights);

        // When on, a modifier's details stay hidden ("???") until something it affects actually happens
        public static bool HideModifiers = true;

        // defNames of modifiers the player has switched off; they are never picked when generating
        public static HashSet<string> DisabledModifiers = new HashSet<string>();
        public static bool IsDisabled(EntadModifierDef def) => DisabledModifiers.Contains(def.defName);

        // Selection weight of weapon-specific modifiers relative to general ones when picking for a weapon
        public const float DefaultWeaponSpecificWeight = 3f;
        public static float WeaponSpecificWeight = DefaultWeaponSpecificWeight;

        public static void ResetRarityWeights()
        {
            foreach (var kv in DefaultWeights) Weights[kv.Key] = kv.Value;
            foreach (var kv in DefaultMultipliers) Multipliers[kv.Key] = kv.Value;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref HideModifiers, "hideModifiers", true);
            var disabled = DisabledModifiers.ToList();
            Scribe_Collections.Look(ref disabled, "disabledModifiers", LookMode.Value);
            DisabledModifiers = new HashSet<string>(disabled ?? new List<string>());
            Scribe_Values.Look(ref WeaponSpecificWeight, "weaponSpecificWeight", DefaultWeaponSpecificWeight);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = Weights[r];
                Scribe_Values.Look(ref w, "weight" + r, DefaultWeights[r]);
                Weights[r] = Mathf.Max(0f, w);
                float mult = Multipliers[r];
                Scribe_Values.Look(ref mult, "multiplier" + r, DefaultMultipliers[r]);
                Multipliers[r] = Mathf.Max(0.1f, mult);
            }
        }
    }

    public class EntadMod : Mod
    {
        public EntadMod(ModContentPack content) : base(content)
        {
            GetSettings<EntadSettings>();
        }

        public override string SettingsCategory() => "Entad Framework";

        private enum Tab { General, Modifiers }
        private Tab tab;
        private string search = "";
        private Vector2 scroll;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var tabRect = new Rect(inRect.x, inRect.y, 140f, 30f);
            if (Widgets.ButtonText(tabRect, "General", tab != Tab.General)) tab = Tab.General;
            tabRect.x += 150f;
            if (Widgets.ButtonText(tabRect, "Modifiers", tab != Tab.Modifiers)) tab = Tab.Modifiers;

            var body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            if (tab == Tab.General) DoGeneral(body);
            else DoModifiers(body);
        }

        // Everything a modifier does that can be searched for
        private static string SearchText(EntadModifierDef d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(d.label).Append(' ').Append(d.defName).Append(' ').Append(d.rarity);
            foreach (var r in d.AllRanges()) if (r.stat != null) sb.Append(' ').Append(r.stat.label).Append(' ').Append(r.stat.defName);
            if (d.buildingFactors != null) foreach (var b in d.buildingFactors) sb.Append(' ').Append(b.Label);
            if (d.abilities != null) foreach (var a in d.abilities) if (a != null) sb.Append(' ').Append(a.label).Append(' ').Append(a.defName);
            if (d.fuelTypes != null) foreach (var f in d.fuelTypes) sb.Append(' ').Append(f.label);
            if (d.changeDamageType != null) sb.Append(' ').Append(d.changeDamageType.label);
            if (d.extraDamage != null) foreach (var e in d.extraDamage) sb.Append(' ').Append(e.damageType?.label);
            return sb.ToString();
        }

        private void DoModifiers(Rect rect)
        {
            var all = DefDatabase<EntadModifierDef>.AllDefsListForReading;
            var top = new Rect(rect.x, rect.y, rect.width, 30f);
            Widgets.Label(new Rect(top.x, top.y, 70f, 30f), "Search:");
            search = Widgets.TextField(new Rect(top.x + 75f, top.y, 300f, 30f), search);
            string[] terms = search.ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);

            var shown = all.Where(d =>
            {
                if (terms.Length == 0) return true;
                string text = SearchText(d).ToLowerInvariant();
                return terms.All(t => text.Contains(t));
            }).OrderBy(d => d.label).ToList();

            if (Widgets.ButtonText(new Rect(top.x + 390f, top.y, 120f, 30f), "Enable shown"))
                foreach (var d in shown) EntadSettings.DisabledModifiers.Remove(d.defName);
            if (Widgets.ButtonText(new Rect(top.x + 520f, top.y, 120f, 30f), "Disable shown"))
                foreach (var d in shown) EntadSettings.DisabledModifiers.Add(d.defName);

            int off = all.Count(EntadSettings.IsDisabled);
            Widgets.Label(new Rect(rect.x, rect.y + 34f, rect.width, 24f), $"{shown.Count} of {all.Count} modifiers shown, {off} disabled. Disabled modifiers are never picked when generating items.");

            var outRect = new Rect(rect.x, rect.y + 62f, rect.width, rect.height - 62f);
            const float rowH = 28f;
            var view = new Rect(0f, 0f, outRect.width - 20f, shown.Count * rowH);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / rowH));
            int last = Mathf.Min(shown.Count, first + (int)(outRect.height / rowH) + 2);
            for (int i = first; i < last; i++)
            {
                var d = shown[i];
                var row = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                bool on = !EntadSettings.IsDisabled(d);
                bool was = on;
                Widgets.Checkbox(row.x + 4f, row.y + 2f, ref on, 24f);
                if (on != was) { if (on) EntadSettings.DisabledModifiers.Remove(d.defName); else EntadSettings.DisabledModifiers.Add(d.defName); }
                Widgets.Label(new Rect(row.x + 36f, row.y + 2f, 260f, rowH), d.LabelCap + " (" + d.rarity + ")");
                Widgets.Label(new Rect(row.x + 300f, row.y + 2f, row.width - 300f, rowH), Summary(d));
                TooltipHandler.TipRegion(row, d.description);
            }
            Widgets.EndScrollView();
        }

        private static string Summary(EntadModifierDef d)
        {
            var parts = new List<string>();
            foreach (var r in d.AllRanges()) if (r.stat != null) parts.Add(r.stat.LabelCap);
            if (d.buildingFactors != null) foreach (var b in d.buildingFactors) parts.Add(b.Label);
            if (d.abilities != null) foreach (var a in d.abilities) if (a != null) parts.Add(a.LabelCap);
            if (d.HasDamageEffect) parts.Add("Damage");
            if (!d.fuelTypes.NullOrEmpty()) parts.Add("Fuel");
            return string.Join(", ", parts.Distinct());
        }

        private Vector2 generalScroll;

        private void DoGeneral(Rect inRect)
        {
            var view = new Rect(0f, 0f, inRect.width - 20f, 1250f);
            Widgets.BeginScrollView(inRect, ref generalScroll, view);
            var list = new Listing_Standard();
            list.Begin(view);

            list.CheckboxLabeled("Hide modifiers until revealed", ref EntadSettings.HideModifiers,
                "Newly generated entad items show \"???\" for their modifiers until something the modifier affects happens. Existing items keep their current state.");
            list.GapLine();
            list.Label($"Weapon-specific modifier weight: x{EntadSettings.WeaponSpecificWeight:0.#}");
            EntadSettings.WeaponSpecificWeight = Mathf.Round(list.Slider(EntadSettings.WeaponSpecificWeight, 1f, 10f) * 2f) / 2f;
            list.Label("How much more likely modifiers made for weapons (damage, accuracy, ...) are than general ones when generating for a weapon. 1 = no preference.");
            list.GapLine();
            list.Label("Rarity curve: relative chance of each rarity when a modifier is picked. Rarities with no eligible modifier are skipped and the rest are renormalised.");
            list.Gap(6f);

            float total = 0f;
            foreach (var w in EntadSettings.Weights.Values) total += w;

            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = EntadSettings.Weights[r];
                string pct = total > 0f ? (w / total).ToStringPercent() : "0%";
                list.Label($"{r}: weight {w:0.#} ({pct} of the total)");
                EntadSettings.Weights[r] = Mathf.Round(list.Slider(w, 0f, 100f) * 2f) / 2f;
            }

            list.GapLine();
            list.Label("Rarity multipliers: how much a rarity widens the min/max of modifiers that scale with rarity. Offsets are multiplied; factors scale their distance from 100%. A modifier is capped at the highest rarity where its values stay valid (for example a factor never drops to zero).");
            list.Gap(6f);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float m = EntadSettings.Multipliers[r];
                list.Label($"{r}: x{m:0.##}");
                EntadSettings.Multipliers[r] = Mathf.Round(list.Slider(m, 0.5f, 6f) * 20f) / 20f;
            }

            list.Gap(6f);
            if (list.ButtonText("Reset to defaults")) EntadSettings.ResetRarityWeights();

            list.End();
            Widgets.EndScrollView();
        }
    }
}
