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

        // Generation points (EntadTraitDef.PointsAt). A trait with no <points> is worth its rarity's value; one that
        // scales with rarity has its points multiplied by this table relative to its own rarity. Steeper than the
        // strength multipliers on purpose: a Legendary roll is worth more than the size of its numbers, it's rare.
        public static readonly Dictionary<EntadRarity, float> DefaultPoints = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, 1f },
            { EntadRarity.Uncommon, 2f },
            { EntadRarity.Rare, 4f },
            { EntadRarity.Epic, 7f },
            { EntadRarity.Legendary, 11f },
        };

        public static readonly Dictionary<EntadRarity, float> Points = new Dictionary<EntadRarity, float>(DefaultPoints);

        // Point target used when a request doesn't give one
        public static readonly FloatRange DefaultPointTarget = new FloatRange(2f, 6f);
        public static FloatRange PointTarget = DefaultPointTarget;
        // Chance an item may take drawbacks (negative points, which buy more traits), and how many
        public const float DefaultDrawbackChance = 0.3f;
        public static float DrawbackChance = DefaultDrawbackChance;
        public const int DefaultMaxDrawbacks = 1;
        public static int MaxDrawbacks = DefaultMaxDrawbacks;
        // Most traits a generated item gets, whatever its budget
        public const int DefaultMaxTraits = 5;
        public static int MaxTraits = DefaultMaxTraits;
        // A trait that would take an item past its point target keeps (points left / its cost)^this of its weight
        public const float DefaultOvershootStrictness = 1f;
        public static float OvershootStrictness = DefaultOvershootStrictness;

        // Market value (CompEntad.MarketValueOffset): per generation point, a share of the item's base value plus
        // flat silver; drawbacks can't take an item below ValueFloor of its base; traders add MysteryBonus of the
        // base value for an item with anything unidentified.
        public const float DefaultValuePercentPerPoint = 0.1f;
        public static float ValuePercentPerPoint = DefaultValuePercentPerPoint;
        public const float DefaultValueSilverPerPoint = 50f;
        public static float ValueSilverPerPoint = DefaultValueSilverPerPoint;
        public const float DefaultValueFloor = 0.1f;
        public static float ValueFloor = DefaultValueFloor;
        public const float DefaultMysteryBonus = 0.25f;
        public static float MysteryBonus = DefaultMysteryBonus;

        // Appraisers (EntadAppraisal.cs): chance a trade caravan brings one / a settlement has one, price per item
        // (a share of player wealth, never below the minimum), each appraiser's daily maximum (rolled once) and the
        // lowest number they can do on a day.
        public const float DefaultCaravanAppraiserChance = 0.25f;
        public static float CaravanAppraiserChance = DefaultCaravanAppraiserChance;
        public const float DefaultSettlementAppraiserChance = 0.25f;
        public static float SettlementAppraiserChance = DefaultSettlementAppraiserChance;
        public const int DefaultIdentifyMinPrice = 100;
        public static int IdentifyMinPrice = DefaultIdentifyMinPrice;
        public const float DefaultIdentifyWealthFraction = 0.001f;
        public static float IdentifyWealthFraction = DefaultIdentifyWealthFraction;
        public static readonly IntRange DefaultAppraiserMax = new IntRange(3, 6);
        public static IntRange AppraiserMax = DefaultAppraiserMax;
        public const int DefaultAppraiserDailyMin = 1;
        public static int AppraiserDailyMin = DefaultAppraiserDailyMin;

        // When on, a trait's details stay hidden ("???") until something it affects actually happens
        public static bool HideTraits = true;

        // defNames of traits the player has switched off; they are never picked when generating
        public static HashSet<string> DisabledTraits = new HashSet<string>();
        public static bool IsDisabled(EntadTraitDef def) => DisabledTraits.Contains(def.defName);

        // Selection weight of weapon-specific traits relative to general ones when picking for a weapon
        public const float DefaultWeaponSpecificWeight = 3f;
        public static float WeaponSpecificWeight = DefaultWeaponSpecificWeight;

        // Chance that a vanilla unique or persona weapon picked for random traits gets them (EntadUtility.IsUniqueWeapon)
        public static float UniqueWeaponChance = 0f;

        // Everything on the General and Rarity tabs. Trait toggles are left alone: the Traits tab has its own
        // enable/disable-shown buttons, and losing a curated list to a misclick would hurt.
        public static void ResetToDefaults()
        {
            HideTraits = true;
            WeaponSpecificWeight = DefaultWeaponSpecificWeight;
            UniqueWeaponChance = 0f;
            PointTarget = DefaultPointTarget;
            DrawbackChance = DefaultDrawbackChance;
            MaxDrawbacks = DefaultMaxDrawbacks;
            MaxTraits = DefaultMaxTraits;
            OvershootStrictness = DefaultOvershootStrictness;
            ValuePercentPerPoint = DefaultValuePercentPerPoint;
            ValueSilverPerPoint = DefaultValueSilverPerPoint;
            ValueFloor = DefaultValueFloor;
            MysteryBonus = DefaultMysteryBonus;
            CaravanAppraiserChance = DefaultCaravanAppraiserChance;
            SettlementAppraiserChance = DefaultSettlementAppraiserChance;
            IdentifyMinPrice = DefaultIdentifyMinPrice;
            IdentifyWealthFraction = DefaultIdentifyWealthFraction;
            AppraiserMax = DefaultAppraiserMax;
            AppraiserDailyMin = DefaultAppraiserDailyMin;
            ResetRarityWeights();
        }

        public static void ResetRarityWeights()
        {
            foreach (var kv in DefaultWeights) Weights[kv.Key] = kv.Value;
            foreach (var kv in DefaultMultipliers) Multipliers[kv.Key] = kv.Value;
            foreach (var kv in DefaultDurability) Durability[kv.Key] = kv.Value;
            foreach (var kv in DefaultPoints) Points[kv.Key] = kv.Value;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref HideTraits, "hideTraits", true);
            var disabled = DisabledTraits.ToList();
            Scribe_Collections.Look(ref disabled, "disabledTraits", LookMode.Value);
            DisabledTraits = new HashSet<string>(disabled ?? new List<string>());
            Scribe_Values.Look(ref WeaponSpecificWeight, "weaponSpecificWeight", DefaultWeaponSpecificWeight);
            Scribe_Values.Look(ref UniqueWeaponChance, "uniqueWeaponChance", 0f);
            UniqueWeaponChance = Mathf.Clamp01(UniqueWeaponChance);
            Scribe_Values.Look(ref PointTarget, "pointTarget", DefaultPointTarget);
            PointTarget = new FloatRange(Mathf.Max(0.5f, PointTarget.min), Mathf.Max(Mathf.Max(0.5f, PointTarget.min), PointTarget.max));
            Scribe_Values.Look(ref DrawbackChance, "drawbackChance", DefaultDrawbackChance);
            DrawbackChance = Mathf.Clamp01(DrawbackChance);
            Scribe_Values.Look(ref MaxDrawbacks, "maxDrawbacks", DefaultMaxDrawbacks);
            MaxDrawbacks = Mathf.Clamp(MaxDrawbacks, 0, 5);
            Scribe_Values.Look(ref MaxTraits, "maxTraits", DefaultMaxTraits);
            MaxTraits = Mathf.Clamp(MaxTraits, 1, 10);
            Scribe_Values.Look(ref OvershootStrictness, "overshootStrictness", DefaultOvershootStrictness);
            OvershootStrictness = Mathf.Clamp(OvershootStrictness, 0f, 4f);
            Scribe_Values.Look(ref ValuePercentPerPoint, "valuePercentPerPoint", DefaultValuePercentPerPoint);
            ValuePercentPerPoint = Mathf.Clamp(ValuePercentPerPoint, 0f, 0.5f);
            Scribe_Values.Look(ref ValueSilverPerPoint, "valueSilverPerPoint", DefaultValueSilverPerPoint);
            ValueSilverPerPoint = Mathf.Clamp(ValueSilverPerPoint, 0f, 500f);
            Scribe_Values.Look(ref ValueFloor, "valueFloor", DefaultValueFloor);
            ValueFloor = Mathf.Clamp01(ValueFloor);
            Scribe_Values.Look(ref MysteryBonus, "mysteryBonus", DefaultMysteryBonus);
            MysteryBonus = Mathf.Clamp01(MysteryBonus);
            Scribe_Values.Look(ref CaravanAppraiserChance, "caravanAppraiserChance", DefaultCaravanAppraiserChance);
            CaravanAppraiserChance = Mathf.Clamp01(CaravanAppraiserChance);
            Scribe_Values.Look(ref SettlementAppraiserChance, "settlementAppraiserChance", DefaultSettlementAppraiserChance);
            SettlementAppraiserChance = Mathf.Clamp01(SettlementAppraiserChance);
            Scribe_Values.Look(ref IdentifyMinPrice, "identifyMinPrice", DefaultIdentifyMinPrice);
            IdentifyMinPrice = Mathf.Clamp(IdentifyMinPrice, 0, 2000);
            Scribe_Values.Look(ref IdentifyWealthFraction, "identifyWealthFraction", DefaultIdentifyWealthFraction);
            IdentifyWealthFraction = Mathf.Clamp(IdentifyWealthFraction, 0f, 0.01f);
            Scribe_Values.Look(ref AppraiserMax, "appraiserMax", DefaultAppraiserMax);
            AppraiserMax = new IntRange(Mathf.Clamp(AppraiserMax.min, 1, 20), Mathf.Clamp(AppraiserMax.max, Mathf.Clamp(AppraiserMax.min, 1, 20), 20));
            Scribe_Values.Look(ref AppraiserDailyMin, "appraiserDailyMin", DefaultAppraiserDailyMin);
            AppraiserDailyMin = Mathf.Clamp(AppraiserDailyMin, 1, 20);
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
                float pts = Points[r];
                Scribe_Values.Look(ref pts, "points" + r, DefaultPoints[r]);
                Points[r] = Mathf.Max(0.1f, pts);
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
            if (d.triggers != null)
                foreach (var t in d.triggers) sb.Append(' ').Append(t.EventLabel).Append(' ').Append(t.EffectsLabel(null));
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
            if (d.triggers != null) foreach (var t in d.triggers) parts.Add(t.EventLabel);
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
            list.Gap();
            list.Label("EF_Settings_UniqueChance".Translate(EntadSettings.UniqueWeaponChance.ToStringPercent()));
            EntadSettings.UniqueWeaponChance = Mathf.Round(list.Slider(EntadSettings.UniqueWeaponChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_UniqueChance_Desc".Translate());
            list.Gap();

            list.Label("EF_Settings_PointTarget".Translate(EntadSettings.PointTarget.min.ToString("0.#"), EntadSettings.PointTarget.max.ToString("0.#")));
            Widgets.FloatRange(list.GetRect(28f), 0x45465054, ref EntadSettings.PointTarget, 0.5f, 40f, null, ToStringStyle.FloatOne);
            EntadSettings.PointTarget = new FloatRange(Mathf.Round(EntadSettings.PointTarget.min * 2f) / 2f, Mathf.Round(EntadSettings.PointTarget.max * 2f) / 2f);
            list.Label("EF_Settings_PointTarget_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_Overshoot".Translate(EntadSettings.OvershootStrictness.ToString("0.##")));
            EntadSettings.OvershootStrictness = Mathf.Round(list.Slider(EntadSettings.OvershootStrictness, 0f, 4f) * 4f) / 4f;
            list.Label("EF_Settings_Overshoot_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_MaxTraits".Translate(EntadSettings.MaxTraits));
            EntadSettings.MaxTraits = Mathf.RoundToInt(list.Slider(EntadSettings.MaxTraits, 1f, 10f));
            list.Gap();
            list.Label("EF_Settings_DrawbackChance".Translate(EntadSettings.DrawbackChance.ToStringPercent()));
            EntadSettings.DrawbackChance = Mathf.Round(list.Slider(EntadSettings.DrawbackChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_MaxDrawbacks".Translate(EntadSettings.MaxDrawbacks));
            EntadSettings.MaxDrawbacks = Mathf.RoundToInt(list.Slider(EntadSettings.MaxDrawbacks, 0f, 5f));
            list.Label("EF_Settings_Drawbacks_Desc".Translate());
            list.Gap();

            Section(list, "EF_Settings_MarketValue");
            list.Label("EF_Settings_ValuePercent".Translate(EntadSettings.ValuePercentPerPoint.ToStringPercent()));
            EntadSettings.ValuePercentPerPoint = Mathf.Round(list.Slider(EntadSettings.ValuePercentPerPoint, 0f, 0.5f) * 100f) / 100f;
            list.Label("EF_Settings_ValueSilver".Translate(EntadSettings.ValueSilverPerPoint.ToStringMoney()));
            EntadSettings.ValueSilverPerPoint = Mathf.Round(list.Slider(EntadSettings.ValueSilverPerPoint, 0f, 500f) / 5f) * 5f;
            list.Label("EF_Settings_Value_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_ValueFloor".Translate(EntadSettings.ValueFloor.ToStringPercent()));
            EntadSettings.ValueFloor = Mathf.Round(list.Slider(EntadSettings.ValueFloor, 0f, 1f) * 20f) / 20f;
            list.Gap();
            list.Label("EF_Settings_MysteryBonus".Translate(EntadSettings.MysteryBonus.ToStringPercent()));
            EntadSettings.MysteryBonus = Mathf.Round(list.Slider(EntadSettings.MysteryBonus, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_MysteryBonus_Desc".Translate());
            list.Gap();

            Section(list, "EF_Settings_Appraisers");
            list.Label("EF_Settings_CaravanAppraiser".Translate(EntadSettings.CaravanAppraiserChance.ToStringPercent()));
            EntadSettings.CaravanAppraiserChance = Mathf.Round(list.Slider(EntadSettings.CaravanAppraiserChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_SettlementAppraiser".Translate(EntadSettings.SettlementAppraiserChance.ToStringPercent()));
            EntadSettings.SettlementAppraiserChance = Mathf.Round(list.Slider(EntadSettings.SettlementAppraiserChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_Appraiser_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_IdentifyMinPrice".Translate(EntadSettings.IdentifyMinPrice.ToStringMoney()));
            EntadSettings.IdentifyMinPrice = Mathf.RoundToInt(list.Slider(EntadSettings.IdentifyMinPrice, 0f, 2000f) / 10f) * 10;
            list.Label("EF_Settings_IdentifyWealth".Translate((EntadSettings.IdentifyWealthFraction).ToStringPercent("0.##")));
            EntadSettings.IdentifyWealthFraction = Mathf.Round(list.Slider(EntadSettings.IdentifyWealthFraction, 0f, 0.01f) * 10000f) / 10000f;
            list.Label("EF_Settings_IdentifyPrice_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_AppraiserMax".Translate(EntadSettings.AppraiserMax.min, EntadSettings.AppraiserMax.max));
            Widgets.IntRange(list.GetRect(28f), 0x45465041, ref EntadSettings.AppraiserMax, 1, 20);
            list.Label("EF_Settings_AppraiserDailyMin".Translate(EntadSettings.AppraiserDailyMin));
            EntadSettings.AppraiserDailyMin = Mathf.RoundToInt(list.Slider(EntadSettings.AppraiserDailyMin, 1f, 20f));
            list.Label("EF_Settings_AppraiserDaily_Desc".Translate());
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

            list.Gap();
            Section(list, "EF_Settings_Points");
            list.Label("EF_Settings_Points_Desc".Translate());
            list.Gap(6f);
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float p = EntadSettings.Points[r];
                list.Label("EF_Settings_PointRow".Translate(r.Label(), p.ToString("0.#")));
                EntadSettings.Points[r] = Mathf.Round(list.Slider(p, 0.5f, 30f) * 2f) / 2f;
            }
        }
    }
}
