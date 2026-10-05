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

        // Relative weights for choosing a trait's rarity; used by EntadRarityChances.Default
        // Multiplier on the ranges of rarity-scaling traits at each rarity (relative to the trait's own rarity)
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

        // Max hit points multiplier for every item with at least one entad trait, by the highest rarity among its traits
        public static readonly Dictionary<EntadRarity, float> DefaultDurability = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, 1.25f },
            { EntadRarity.Uncommon, 1.5f },
            { EntadRarity.Rare, 2f },
            { EntadRarity.Epic, 3f },
            { EntadRarity.Legendary, 5f },
        };

        public static readonly Dictionary<EntadRarity, float> Durability = new Dictionary<EntadRarity, float>(DefaultDurability);

        // When on, a trait's details stay hidden ("???") until something it affects actually happens
        public static bool HideTraits = true;

        // defNames of traits the player has switched off; they are never picked when generating
        public static HashSet<string> DisabledTraits = new HashSet<string>();
        public static bool IsDisabled(EntadTraitDef def) => DisabledTraits.Contains(def.defName);

        // Selection weight of weapon-specific traits relative to general ones when picking for a weapon
        public const float DefaultWeaponSpecificWeight = 3f;
        public static float WeaponSpecificWeight = DefaultWeaponSpecificWeight;

        // Everything on the General and Rarity tabs. Trait toggles are left alone: the Traits tab has its own
        // enable/disable-shown buttons, and losing a curated list to a misclick would hurt.
        public static void ResetToDefaults()
        {
            HideTraits = true;
            WeaponSpecificWeight = DefaultWeaponSpecificWeight;
            ResetRarityWeights();
        }

        public static void ResetRarityWeights()
        {
            foreach (var kv in DefaultWeights) Weights[kv.Key] = kv.Value;
            foreach (var kv in DefaultMultipliers) Multipliers[kv.Key] = kv.Value;
            foreach (var kv in DefaultDurability) Durability[kv.Key] = kv.Value;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref HideTraits, "hideTraits", true);
            var disabled = DisabledTraits.ToList();
            Scribe_Collections.Look(ref disabled, "disabledTraits", LookMode.Value);
            DisabledTraits = new HashSet<string>(disabled ?? new List<string>());
            Scribe_Values.Look(ref WeaponSpecificWeight, "weaponSpecificWeight", DefaultWeaponSpecificWeight);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = Weights[r];
                Scribe_Values.Look(ref w, "weight" + r, DefaultWeights[r]);
                Weights[r] = Mathf.Max(0f, w);
                float mult = Multipliers[r];
                Scribe_Values.Look(ref mult, "multiplier" + r, DefaultMultipliers[r]);
                Multipliers[r] = Mathf.Max(0.1f, mult);
                float dur = Durability[r];
                Scribe_Values.Look(ref dur, "durability" + r, DefaultDurability[r]);
                Durability[r] = Mathf.Max(0.1f, dur);
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

        // Durability factors that items in the running game are currently scaled to. When the window closes with
        // different values, items keep their health fraction instead of jumping to "damaged" or over the maximum.
        private Dictionary<EntadRarity, float> appliedDurability;

        public override void WriteSettings()
        {
            base.WriteSettings();
            if (appliedDurability != null && Current.Game != null) EntadUtility.RescaleDurability(appliedDurability);
            appliedDurability = null;
        }

        private enum Tab { General, Rarity, Traits }
        private Tab tab;
        private string search = "";
        private Vector2 scroll;
        private Vector2 listingScroll;
        private float listingHeight = 900f;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            if (appliedDurability == null) appliedDurability = new Dictionary<EntadRarity, float>(EntadSettings.Durability);

            // TabDrawer draws the tab strip above the rect it is given, so the body starts one tab height down,
            // and the bottom 40 px are kept for the reset button.
            var body = new Rect(inRect.x, inRect.y + TabDrawer.TabHeight, inRect.width, inRect.height - TabDrawer.TabHeight - 40f);
            var tabs = new List<TabRecord>
            {
                new TabRecord("EF_Tab_General".Translate(), () => SetTab(Tab.General), tab == Tab.General),
                new TabRecord("EF_Tab_Rarity".Translate(), () => SetTab(Tab.Rarity), tab == Tab.Rarity),
                new TabRecord("EF_Tab_Traits".Translate(), () => SetTab(Tab.Traits), tab == Tab.Traits),
            };
            Widgets.DrawMenuSection(body);
            TabDrawer.DrawTabs(body, tabs);

            Rect inner = body.ContractedBy(10f);
            if (tab == Tab.Traits) DoTraits(inner);
            else
            {
                var view = new Rect(0f, 0f, inner.width - 16f, listingHeight);
                Widgets.BeginScrollView(inner, ref listingScroll, view);
                // maxOneColumn: the view is sized from last frame's content, so right after switching to a taller
                // tab the listing overruns it for one frame. Without this, Listing wraps the overflow into a second
                // column off to the right, CurHeight measures only that column and the view never grows back.
                var list = new Listing_Standard { maxOneColumn = true };
                list.Begin(view);
                if (tab == Tab.General) DoGeneral(list);
                else DoRarity(list);
                listingHeight = list.CurHeight + 20f;
                list.End();
                Widgets.EndScrollView();
            }

            var reset = new Rect(inRect.x, inRect.yMax - 32f, 200f, 32f);
            if (Widgets.ButtonText(reset, "EF_Settings_Reset".Translate())) EntadSettings.ResetToDefaults();
            TooltipHandler.TipRegion(reset, "EF_Settings_Reset_Tip".Translate());
        }

        private void SetTab(Tab t)
        {
            tab = t;
            listingScroll = Vector2.zero;
        }

        private static void Section(Listing_Standard list, string key)
        {
            Text.Font = GameFont.Medium;
            list.Label(key.Translate());
            Text.Font = GameFont.Small;
            list.GapLine(4f);
        }

        // Everything a trait does that can be searched for
        private static string SearchText(EntadTraitDef d)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(d.label).Append(' ').Append(d.defName).Append(' ').Append(d.rarity).Append(' ').Append(d.RarityLabel);
            foreach (var r in d.AllRanges()) if (r.stat != null) sb.Append(' ').Append(r.stat.label).Append(' ').Append(r.stat.defName);
            if (d.buildingFactors != null) foreach (var b in d.buildingFactors) sb.Append(' ').Append(b.Label);
            if (d.abilities != null) foreach (var a in d.abilities) if (a != null) sb.Append(' ').Append(a.label).Append(' ').Append(a.defName);
            if (d.AllFuelTypes != null) foreach (var f in d.AllFuelTypes) sb.Append(' ').Append(f.label);
            if (d.changeDamageType != null) sb.Append(' ').Append(d.changeDamageType.label);
            if (d.weaponProperties != null) foreach (var w in d.weaponProperties) sb.Append(' ').Append(w.Label);
            if (d.ignoreAccuracyMaluses) sb.Append(' ').Append("EF_Summary_Weapon".Translate());
            if (d.equippedHediffs != null) foreach (var h in d.equippedHediffs) if (h != null) sb.Append(' ').Append(h.label);
            if (d.killThought != null) sb.Append(' ').Append("EF_Summary_KillThought".Translate()).Append(' ').Append(d.killThought.stages?.FirstOrDefault()?.label);
            if (d.extraDamage != null) foreach (var e in d.extraDamage) sb.Append(' ').Append(e.damageType?.label);
            return sb.ToString();
        }

        private void DoTraits(Rect rect)
        {
            var all = DefDatabase<EntadTraitDef>.AllDefsListForReading;
            var top = new Rect(rect.x, rect.y, rect.width, 30f);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(top.x, top.y, 70f, 30f), "EF_Settings_Search".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            search = Widgets.TextField(new Rect(top.x + 75f, top.y, 300f, 30f), search);
            string[] terms = search.ToLowerInvariant().Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);

            var shown = all.Where(d =>
            {
                if (terms.Length == 0) return true;
                string text = SearchText(d).ToLowerInvariant();
                return terms.All(t => text.Contains(t));
            }).OrderBy(d => d.label).ToList();

            if (Widgets.ButtonText(new Rect(top.x + 390f, top.y, 120f, 30f), "EF_Settings_EnableShown".Translate()))
                foreach (var d in shown) EntadSettings.DisabledTraits.Remove(d.defName);
            if (Widgets.ButtonText(new Rect(top.x + 520f, top.y, 120f, 30f), "EF_Settings_DisableShown".Translate()))
                foreach (var d in shown) EntadSettings.DisabledTraits.Add(d.defName);

            int off = all.Count(EntadSettings.IsDisabled);
            Widgets.Label(new Rect(rect.x, rect.y + 34f, rect.width, 24f), "EF_Settings_TraitCounts".Translate(shown.Count, all.Count, off));

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
                if (on != was) { if (on) EntadSettings.DisabledTraits.Remove(d.defName); else EntadSettings.DisabledTraits.Add(d.defName); }
                Widgets.Label(new Rect(row.x + 36f, row.y + 2f, 260f, rowH), "EF_Settings_TraitRow".Translate(d.LabelCap, d.RarityLabel));
                Widgets.Label(new Rect(row.x + 300f, row.y + 2f, row.width - 300f, rowH), Summary(d));
                TooltipHandler.TipRegion(row, d.description);
            }
            Widgets.EndScrollView();
        }

        private static string Summary(EntadTraitDef d)
        {
            var parts = new List<string>();
            foreach (var r in d.AllRanges()) if (r.stat != null) parts.Add(r.stat.LabelCap);
            if (d.buildingFactors != null) foreach (var b in d.buildingFactors) parts.Add(b.Label);
            if (d.abilities != null) foreach (var a in d.abilities) if (a != null) parts.Add(a.LabelCap);
            if (d.HasDamageEffect) parts.Add("EF_Summary_Damage".Translate());
            if (d.weaponProperties != null) foreach (var w in d.weaponProperties) parts.Add(w.Label);
            if (d.ignoreAccuracyMaluses) parts.Add("EF_Summary_Weapon".Translate());
            if (d.equippedHediffs != null) foreach (var h in d.equippedHediffs) if (h != null) parts.Add(h.LabelCap);
            if (d.killThought != null) parts.Add("EF_Summary_KillThought".Translate());
            if (!d.AllFuelTypes.NullOrEmpty()) parts.Add("EF_Summary_Fuel".Translate());
            return string.Join(", ", parts.Distinct());
        }

        private static void DoGeneral(Listing_Standard list)
        {
            Section(list, "EF_Settings_Discovery");
            list.CheckboxLabeled("EF_Settings_HideTraits".Translate(), ref EntadSettings.HideTraits, "EF_Settings_HideTraits_Tip".Translate());
            list.Gap();

            Section(list, "EF_Settings_Generation");
            list.Label("EF_Settings_WeaponWeight".Translate(EntadSettings.WeaponSpecificWeight.ToString("0.#")));
            EntadSettings.WeaponSpecificWeight = Mathf.Round(list.Slider(EntadSettings.WeaponSpecificWeight, 1f, 10f) * 2f) / 2f;
            list.Label("EF_Settings_WeaponWeight_Desc".Translate());
        }

        private static void DoRarity(Listing_Standard list)
        {
            Section(list, "EF_Settings_RarityCurve");
            list.Label("EF_Settings_RarityCurve_Desc".Translate());
            list.Gap(6f);

            float total = 0f;
            foreach (var w in EntadSettings.Weights.Values) total += w;

            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = EntadSettings.Weights[r];
                string pct = total > 0f ? (w / total).ToStringPercent() : "0%";
                list.Label("EF_Settings_WeightRow".Translate(r.Label(), w.ToString("0.#"), pct));
                EntadSettings.Weights[r] = Mathf.Round(list.Slider(w, 0f, 100f) * 2f) / 2f;
            }

            list.Gap();
            Section(list, "EF_Settings_Multipliers");
            list.Label("EF_Settings_Multipliers_Desc".Translate());
            list.Gap(6f);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float m = EntadSettings.Multipliers[r];
                list.Label("EF_Settings_FactorRow".Translate(r.Label(), m.ToString("0.##")));
                EntadSettings.Multipliers[r] = Mathf.Round(list.Slider(m, 0.5f, 6f) * 20f) / 20f;
            }

            list.Gap();
            Section(list, "EF_Settings_Durability");
            list.Label("EF_Settings_Durability_Desc".Translate());
            list.Gap(6f);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float d = EntadSettings.Durability[r];
                list.Label("EF_Settings_FactorRow".Translate(r.Label(), d.ToString("0.##")));
                EntadSettings.Durability[r] = Mathf.Round(list.Slider(d, 1f, 10f) * 20f) / 20f;
            }
        }
    }
}
