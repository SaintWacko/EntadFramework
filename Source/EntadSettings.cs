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
        // scales with rarity has its points multiplied by this table relative to its own rarity. Fibonacci (build 149):
        // steeper than the strength multipliers on purpose, since a Legendary roll is worth more than the size of its
        // numbers, but no longer the 1/2/4/7/11 table, which priced high rolls of scaling traits out of most targets
        // (a Legendary Portable Space cost 33).
        public static readonly Dictionary<EntadRarity, float> DefaultPoints = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, 1f },
            { EntadRarity.Uncommon, 2f },
            { EntadRarity.Rare, 3f },
            { EntadRarity.Epic, 5f },
            { EntadRarity.Legendary, 8f },
        };

        public static readonly Dictionary<EntadRarity, float> Points = new Dictionary<EntadRarity, float>(DefaultPoints);

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

        // Entads carried by NPC groups (EntadWorldPawns.cs): the chance a group has any carriers, one more carrier per
        // so many group points up to the most, each entad's points from a curve over group points (four anchors,
        // straight lines between) rolled within the variance, and the share bound to their carrier with its points
        // multiplier. Factions may override the chances and add their own multiplier (EntadFactionExtension).
        // The world switches (this one, WorldEnabled, TradersEnabled, RewardsEnabled, LootEnabled, AppraisersEnabled)
        // are defaults for new games: a game can override them (EntadWorld.cs). Read them through EntadWorld.Enabled.
        public static bool WorldEnabled = true;
        public static bool AppraisersEnabled = true;
        public static bool CarriersEnabled = true;
        public const float DefaultCarrierGroupChance = 0.35f;
        public static float CarrierGroupChance = DefaultCarrierGroupChance;
        public const float DefaultCarrierPointsPerExtra = 1500f;
        public static float CarrierPointsPerExtra = DefaultCarrierPointsPerExtra;
        public const int DefaultMaxCarriers = 3;
        public static int MaxCarriers = DefaultMaxCarriers;
        public static readonly float[] CarrierCurveGroupPoints = { 100f, 1000f, 3000f, 7000f };
        public static readonly float[] DefaultCarrierCurvePoints = { 3f, 8f, 15f, 25f };
        public static readonly float[] CarrierCurvePoints = (float[])DefaultCarrierCurvePoints.Clone();
        public const float DefaultCarrierPointVariance = 0.25f;
        public static float CarrierPointVariance = DefaultCarrierPointVariance;
        public const float DefaultCarrierBoundChance = 0.5f;
        public static float CarrierBoundChance = DefaultCarrierBoundChance;
        public const float DefaultCarrierBoundPointFactor = 1.5f;
        public static float CarrierBoundPointFactor = DefaultCarrierBoundPointFactor;

        // Entads in trader stock, quest rewards and site loot (EntadWorldItems.cs): a trader's stock rolls once and
        // converts a few items; each reward or loot item rolls on its own up to a most per set. Points come from a
        // curve over the set's market value (four anchors), rolled within the variance. ThingSetMakerDefs and
        // TraderKindDefs may override (EntadThingSetExtension, EntadTraderExtension).
        public static bool TradersEnabled = true;
        public static bool RewardsEnabled = true;
        public static bool LootEnabled = true;
        public const float DefaultTraderStockChance = 0.4f;
        public static float TraderStockChance = DefaultTraderStockChance;
        public static readonly IntRange DefaultTraderEntadCount = new IntRange(1, 2);
        public static IntRange TraderEntadCount = DefaultTraderEntadCount;
        public const float DefaultRewardItemChance = 0.15f;
        public static float RewardItemChance = DefaultRewardItemChance;
        public const int DefaultRewardMaxEntads = 1;
        public static int RewardMaxEntads = DefaultRewardMaxEntads;
        public const float DefaultLootItemChance = 0.25f;
        public static float LootItemChance = DefaultLootItemChance;
        public const int DefaultLootMaxEntads = 2;
        public static int LootMaxEntads = DefaultLootMaxEntads;
        public const float DefaultLootBindChance = 0.25f;
        public static float LootBindChance = DefaultLootBindChance;
        public static readonly float[] ItemCurveValues = { 1000f, 4000f, 10000f, 25000f };
        public static readonly float[] DefaultItemCurvePoints = { 3f, 8f, 15f, 25f };
        public static readonly float[] ItemCurvePoints = (float[])DefaultItemCurvePoints.Clone();
        public const float DefaultItemPointVariance = 0.25f;
        public static float ItemPointVariance = DefaultItemPointVariance;

        // The colony's verity as a row under the resource readout, once there is any
        public static bool VerityReadout = true;

        // When on, a trait's details stay hidden ("???") until something it affects actually happens
        public static bool HideTraits = true;

        // Aura ground glow and link lines (need Ideology). Off: auras show their range ring when selected instead.
        // Pulse flecks don't need Ideology and aren't affected.
        public static bool AuraEffects = true;

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
            AuraEffects = true;
            WeaponSpecificWeight = DefaultWeaponSpecificWeight;
            UniqueWeaponChance = 0f;
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
            WorldEnabled = true;
            AppraisersEnabled = true;
            CarriersEnabled = true;
            CarrierGroupChance = DefaultCarrierGroupChance;
            CarrierPointsPerExtra = DefaultCarrierPointsPerExtra;
            MaxCarriers = DefaultMaxCarriers;
            DefaultCarrierCurvePoints.CopyTo(CarrierCurvePoints, 0);
            CarrierPointVariance = DefaultCarrierPointVariance;
            CarrierBoundChance = DefaultCarrierBoundChance;
            CarrierBoundPointFactor = DefaultCarrierBoundPointFactor;
            TradersEnabled = true;
            RewardsEnabled = true;
            LootEnabled = true;
            TraderStockChance = DefaultTraderStockChance;
            TraderEntadCount = DefaultTraderEntadCount;
            RewardItemChance = DefaultRewardItemChance;
            RewardMaxEntads = DefaultRewardMaxEntads;
            LootItemChance = DefaultLootItemChance;
            LootMaxEntads = DefaultLootMaxEntads;
            LootBindChance = DefaultLootBindChance;
            DefaultItemCurvePoints.CopyTo(ItemCurvePoints, 0);
            ItemPointVariance = DefaultItemPointVariance;
            VerityReadout = true;
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
            Scribe_Values.Look(ref AuraEffects, "auraEffects", true);
            var disabled = DisabledTraits.ToList();
            Scribe_Collections.Look(ref disabled, "disabledTraits", LookMode.Value);
            DisabledTraits = new HashSet<string>(disabled ?? new List<string>());
            Scribe_Values.Look(ref WeaponSpecificWeight, "weaponSpecificWeight", DefaultWeaponSpecificWeight);
            WeaponSpecificWeight = Mathf.Clamp(WeaponSpecificWeight, 1f, 10f);
            Scribe_Values.Look(ref UniqueWeaponChance, "uniqueWeaponChance", 0f);
            UniqueWeaponChance = Mathf.Clamp01(UniqueWeaponChance);
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
            Scribe_Values.Look(ref WorldEnabled, "worldEnabled", true);
            Scribe_Values.Look(ref AppraisersEnabled, "appraisersEnabled", true);
            Scribe_Values.Look(ref CarriersEnabled, "carriersEnabled", true);
            Scribe_Values.Look(ref CarrierGroupChance, "carrierGroupChance", DefaultCarrierGroupChance);
            CarrierGroupChance = Mathf.Clamp01(CarrierGroupChance);
            Scribe_Values.Look(ref CarrierPointsPerExtra, "carrierPointsPerExtra", DefaultCarrierPointsPerExtra);
            CarrierPointsPerExtra = Mathf.Clamp(CarrierPointsPerExtra, 250f, 10000f);
            Scribe_Values.Look(ref MaxCarriers, "maxCarriers", DefaultMaxCarriers);
            MaxCarriers = Mathf.Clamp(MaxCarriers, 1, 10);
            for (int i = 0; i < CarrierCurvePoints.Length; i++)
            {
                float v = CarrierCurvePoints[i];
                Scribe_Values.Look(ref v, "carrierCurve" + i, DefaultCarrierCurvePoints[i]);
                CarrierCurvePoints[i] = Mathf.Clamp(v, 0f, 100f);
            }
            Scribe_Values.Look(ref CarrierPointVariance, "carrierPointVariance", DefaultCarrierPointVariance);
            CarrierPointVariance = Mathf.Clamp(CarrierPointVariance, 0f, 0.9f);
            Scribe_Values.Look(ref CarrierBoundChance, "carrierBoundChance", DefaultCarrierBoundChance);
            CarrierBoundChance = Mathf.Clamp01(CarrierBoundChance);
            Scribe_Values.Look(ref CarrierBoundPointFactor, "carrierBoundPointFactor", DefaultCarrierBoundPointFactor);
            CarrierBoundPointFactor = Mathf.Clamp(CarrierBoundPointFactor, 0.5f, 4f);
            // worldItemsEnabled was one switch for all three before they were split; it seeds them until they are saved
            bool legacyItems = true;
            if (Scribe.mode == LoadSaveMode.LoadingVars) Scribe_Values.Look(ref legacyItems, "worldItemsEnabled", true);
            Scribe_Values.Look(ref TradersEnabled, "tradersEnabled", legacyItems);
            Scribe_Values.Look(ref RewardsEnabled, "rewardsEnabled", legacyItems);
            Scribe_Values.Look(ref LootEnabled, "lootEnabled", legacyItems);
            Scribe_Values.Look(ref TraderStockChance, "traderStockChance", DefaultTraderStockChance);
            TraderStockChance = Mathf.Clamp01(TraderStockChance);
            Scribe_Values.Look(ref TraderEntadCount, "traderEntadCount", DefaultTraderEntadCount);
            TraderEntadCount = new IntRange(Mathf.Clamp(TraderEntadCount.min, 1, 5), Mathf.Clamp(TraderEntadCount.max, Mathf.Clamp(TraderEntadCount.min, 1, 5), 5));
            Scribe_Values.Look(ref RewardItemChance, "rewardItemChance", DefaultRewardItemChance);
            RewardItemChance = Mathf.Clamp01(RewardItemChance);
            Scribe_Values.Look(ref RewardMaxEntads, "rewardMaxEntads", DefaultRewardMaxEntads);
            RewardMaxEntads = Mathf.Clamp(RewardMaxEntads, 1, 10);
            Scribe_Values.Look(ref LootItemChance, "lootItemChance", DefaultLootItemChance);
            LootItemChance = Mathf.Clamp01(LootItemChance);
            Scribe_Values.Look(ref LootMaxEntads, "lootMaxEntads", DefaultLootMaxEntads);
            LootMaxEntads = Mathf.Clamp(LootMaxEntads, 1, 10);
            Scribe_Values.Look(ref LootBindChance, "lootBindChance", DefaultLootBindChance);
            LootBindChance = Mathf.Clamp01(LootBindChance);
            for (int i = 0; i < ItemCurvePoints.Length; i++)
            {
                float v = ItemCurvePoints[i];
                Scribe_Values.Look(ref v, "itemCurve" + i, DefaultItemCurvePoints[i]);
                ItemCurvePoints[i] = Mathf.Clamp(v, 0f, 100f);
            }
            Scribe_Values.Look(ref ItemPointVariance, "itemPointVariance", DefaultItemPointVariance);
            ItemPointVariance = Mathf.Clamp(ItemPointVariance, 0f, 0.9f);
            Scribe_Values.Look(ref VerityReadout, "verityReadout", true);
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

        // A world source's switch, as Bastle Framework draws its own: the source's sliders are only drawn while it is
        // on. While the loaded game overrides the switch (EntadWorld.SetForGame), the box shows the game's value,
        // greyed and locked, with a line saying where to change it; the settings' own value is kept for new games.
        // The sliders show while either the game or the settings has the source on, and stay editable: numbers are
        // never per game. The master switch asks for the game's value instead, since it dims everything below it.
        private static bool WorldSwitch(Listing_Standard list, EntadWorldFeature feature, string key, ref bool value, bool returnGameValue = false)
        {
            string tip = (key + "_Tip").Translate();
            bool? forGame = EntadWorld.GetForGame(feature);
            if (!forGame.HasValue)
            {
                list.CheckboxLabeled(key.Translate(), ref value, tip);
                return value;
            }
            bool shown = forGame.Value;
            string label = key.Translate();
            Rect row = list.GetRect(Text.CalcHeight(label, list.ColumnWidth));
            TooltipHandler.TipRegion(row, tip);
            Widgets.CheckboxLabeled(row, label, ref shown, disabled: true);
            list.Gap(list.verticalSpacing);
            string source = EntadWorld.OverrideSourceForGame;
            Hint(list, source != null
                ? "EF_Settings_WorldOverriddenBy".Translate(source).ToString()
                : "EF_Settings_WorldOverridden".Translate().ToString());
            return returnGameValue ? shown : shown || value;
        }

        private static void Hint(Listing_Standard list, string text)
        {
            Text.Font = GameFont.Tiny;
            Color before = GUI.color;
            GUI.color = Color.gray * new Color(1f, 1f, 1f, before.a);
            list.Label(text);
            GUI.color = before;
            Text.Font = GameFont.Small;
            list.Gap(4f);
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
            if (d.aura?.hediff != null) sb.Append(' ').Append("EF_Summary_Aura".Translate(d.aura.hediff.label));
            if (d.powerNet != null) foreach (var line in d.powerNet.CardLines()) sb.Append(' ').Append(line);
            if (d.customCardLines != null) foreach (var line in d.customCardLines) sb.Append(' ').Append(line);
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
                string summary = Summary(d);
                Widgets.Label(new Rect(row.x + 300f, row.y + 2f, row.width - 300f, rowH), summary);
                // The summary can run past the row, so the tooltip repeats it under the flavour text
                TooltipHandler.TipRegion(row, summary.NullOrEmpty() ? d.description : d.description + "\n\n" + summary);
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
            if (d.aura?.hediff != null) parts.Add("EF_Summary_Aura".Translate(d.aura.hediff.label));
            if (d.triggers != null) foreach (var t in d.triggers) parts.Add(t.EventLabel);
            if (!d.AllFuelTypes.NullOrEmpty()) parts.Add("EF_Summary_Fuel".Translate());
            // Descriptions are flavour text, so the row's summary is where the rules show in the settings list
            if (d.HasMood) parts.Add("EF_Summary_Mood".Translate());
            if (d.HasMealEffect) parts.Add("EF_Summary_Meal".Translate());
            if (d.HasPortableSpace) parts.Add("EF_Summary_Space".Translate());
            if (d.powerNet?.grounded == true) parts.Add("EF_Summary_NetGrounded".Translate());
            if (d.powerNet?.flareProof == true) parts.Add("EF_Summary_NetFlareProof".Translate());
            if (d.powerNet?.Arcs == true) parts.Add("EF_Summary_NetArcs".Translate());
            if (d.customCardLines != null) parts.AddRange(d.customCardLines);
            if (d.persona) parts.Add("EF_Summary_Persona".Translate());
            if (d.neverBond) parts.Add("EF_Summary_NeverBond".Translate());
            if (d.secretive) parts.Add("EF_Summary_Secretive".Translate());
            if (d.bondedHediffs != null) foreach (var h in d.bondedHediffs) if (h != null) parts.Add(h.LabelCap);
            if (d.bondedThought != null) parts.Add("EF_Summary_BondedMood".Translate());
            if (d.otherWeaponThought != null) parts.Add("EF_Summary_OtherWeapon".Translate());
            if (d.killThirstThought != null) parts.Add("EF_Summary_KillThirst".Translate());
            return string.Join(", ", parts.Distinct());
        }

        private static void DoGeneral(Listing_Standard list)
        {
            Section(list, "EF_Settings_Discovery");
            list.CheckboxLabeled("EF_Settings_HideTraits".Translate(), ref EntadSettings.HideTraits, "EF_Settings_HideTraits_Tip".Translate());
            list.Gap();

            Section(list, "EF_Settings_Visuals");
            list.CheckboxLabeled("EF_Settings_AuraEffects".Translate(), ref EntadSettings.AuraEffects, "EF_Settings_AuraEffects_Tip".Translate());
            list.CheckboxLabeled("EF_Settings_VerityReadout".Translate(), ref EntadSettings.VerityReadout, "EF_Settings_VerityReadout_Tip".Translate());
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

            Section(list, "EF_Settings_WorldSources");
            bool worldOn = WorldSwitch(list, EntadWorldFeature.World, "EF_Settings_WorldEnabled", ref EntadSettings.WorldEnabled, returnGameValue: true);
            // Dimmed by a box drawn over everything below once it is drawn: sliders and ranges set GUI.color to white
            // themselves, so tinting GUI.color first only dims the labels
            Rect dimFrom = list.GetRect(0f);
            list.Gap();

            Section(list, "EF_Settings_Carriers");
            if (WorldSwitch(list, EntadWorldFeature.Carriers, "EF_Settings_CarriersEnabled", ref EntadSettings.CarriersEnabled))
            {
                list.Label("EF_Settings_CarrierChance".Translate(EntadSettings.CarrierGroupChance.ToStringPercent()));
                EntadSettings.CarrierGroupChance = Mathf.Round(list.Slider(EntadSettings.CarrierGroupChance, 0f, 1f) * 20f) / 20f;
                list.Label("EF_Settings_CarrierPerExtra".Translate(EntadSettings.CarrierPointsPerExtra.ToString("0")));
                EntadSettings.CarrierPointsPerExtra = Mathf.Round(list.Slider(EntadSettings.CarrierPointsPerExtra, 250f, 10000f) / 250f) * 250f;
                list.Label("EF_Settings_MaxCarriers".Translate(EntadSettings.MaxCarriers));
                EntadSettings.MaxCarriers = Mathf.RoundToInt(list.Slider(EntadSettings.MaxCarriers, 1f, 10f));
                list.Label("EF_Settings_Carriers_Desc".Translate());
                list.Gap();
                for (int i = 0; i < EntadSettings.CarrierCurvePoints.Length; i++)
                {
                    float v = EntadSettings.CarrierCurvePoints[i];
                    list.Label("EF_Settings_CarrierCurveRow".Translate(EntadSettings.CarrierCurveGroupPoints[i].ToString("0"), v.ToString("0.#")));
                    EntadSettings.CarrierCurvePoints[i] = Mathf.Round(list.Slider(v, 0f, 100f) * 2f) / 2f;
                }
                list.Label("EF_Settings_CarrierVariance".Translate(EntadSettings.CarrierPointVariance.ToStringPercent()));
                EntadSettings.CarrierPointVariance = Mathf.Round(list.Slider(EntadSettings.CarrierPointVariance, 0f, 0.9f) * 20f) / 20f;
                list.Label("EF_Settings_CarrierCurve_Desc".Translate());
                list.Gap();
                list.Label("EF_Settings_CarrierBound".Translate(EntadSettings.CarrierBoundChance.ToStringPercent()));
                EntadSettings.CarrierBoundChance = Mathf.Round(list.Slider(EntadSettings.CarrierBoundChance, 0f, 1f) * 20f) / 20f;
                list.Label("EF_Settings_CarrierBoundFactor".Translate(EntadSettings.CarrierBoundPointFactor.ToString("0.##")));
                EntadSettings.CarrierBoundPointFactor = Mathf.Round(list.Slider(EntadSettings.CarrierBoundPointFactor, 0.5f, 4f) * 20f) / 20f;
                list.Label("EF_Settings_CarrierBound_Desc".Translate());
            }
            list.Gap();

            Section(list, "EF_Settings_WorldItems");
            bool traders = WorldSwitch(list, EntadWorldFeature.Traders, "EF_Settings_TradersEnabled", ref EntadSettings.TradersEnabled);
            if (traders)
            {
                list.Label("EF_Settings_TraderChance".Translate(EntadSettings.TraderStockChance.ToStringPercent()));
                EntadSettings.TraderStockChance = Mathf.Round(list.Slider(EntadSettings.TraderStockChance, 0f, 1f) * 20f) / 20f;
                list.Label("EF_Settings_TraderCount".Translate(EntadSettings.TraderEntadCount.min, EntadSettings.TraderEntadCount.max));
                Widgets.IntRange(list.GetRect(28f), 0x45465754, ref EntadSettings.TraderEntadCount, 1, 5);
                list.Label("EF_Settings_Trader_Desc".Translate());
            }
            bool rewards = WorldSwitch(list, EntadWorldFeature.Rewards, "EF_Settings_RewardsEnabled", ref EntadSettings.RewardsEnabled);
            if (rewards)
            {
                list.Label("EF_Settings_RewardChance".Translate(EntadSettings.RewardItemChance.ToStringPercent()));
                EntadSettings.RewardItemChance = Mathf.Round(list.Slider(EntadSettings.RewardItemChance, 0f, 1f) * 20f) / 20f;
                list.Label("EF_Settings_RewardMax".Translate(EntadSettings.RewardMaxEntads));
                EntadSettings.RewardMaxEntads = Mathf.RoundToInt(list.Slider(EntadSettings.RewardMaxEntads, 1f, 10f));
            }
            bool loot = WorldSwitch(list, EntadWorldFeature.Loot, "EF_Settings_LootEnabled", ref EntadSettings.LootEnabled);
            if (loot)
            {
                list.Label("EF_Settings_LootChance".Translate(EntadSettings.LootItemChance.ToStringPercent()));
                EntadSettings.LootItemChance = Mathf.Round(list.Slider(EntadSettings.LootItemChance, 0f, 1f) * 20f) / 20f;
                list.Label("EF_Settings_LootMax".Translate(EntadSettings.LootMaxEntads));
                EntadSettings.LootMaxEntads = Mathf.RoundToInt(list.Slider(EntadSettings.LootMaxEntads, 1f, 10f));
                list.Label("EF_Settings_LootBind".Translate(EntadSettings.LootBindChance.ToStringPercent()), tooltip: "EF_Settings_LootBind_Tip".Translate());
                EntadSettings.LootBindChance = Mathf.Round(list.Slider(EntadSettings.LootBindChance, 0f, 1f) * 20f) / 20f;
            }
            if (rewards || loot) list.Label("EF_Settings_RewardLoot_Desc".Translate());
            if (traders || rewards || loot)
            {
                list.Gap();
                for (int i = 0; i < EntadSettings.ItemCurvePoints.Length; i++)
                {
                    float v = EntadSettings.ItemCurvePoints[i];
                    list.Label("EF_Settings_ItemCurveRow".Translate(EntadSettings.ItemCurveValues[i].ToStringMoney(), v.ToString("0.#")));
                    EntadSettings.ItemCurvePoints[i] = Mathf.Round(list.Slider(v, 0f, 100f) * 2f) / 2f;
                }
                list.Label("EF_Settings_ItemVariance".Translate(EntadSettings.ItemPointVariance.ToStringPercent()));
                EntadSettings.ItemPointVariance = Mathf.Round(list.Slider(EntadSettings.ItemPointVariance, 0f, 0.9f) * 20f) / 20f;
                list.Label("EF_Settings_ItemCurve_Desc".Translate());
            }
            list.Gap();

            Section(list, "EF_Settings_Appraisers");
            if (WorldSwitch(list, EntadWorldFeature.Appraisers, "EF_Settings_AppraisersEnabled", ref EntadSettings.AppraisersEnabled))
                DoAppraisers(list);

            if (!worldOn)
            {
                Rect dimTo = list.GetRect(0f);
                Widgets.DrawBoxSolid(new Rect(dimFrom.x, dimFrom.y, dimFrom.width, dimTo.y - dimFrom.y), new Color(Widgets.MenuSectionBGFillColor.r,
                    Widgets.MenuSectionBGFillColor.g, Widgets.MenuSectionBGFillColor.b, 0.6f));
            }
        }

        private static void DoAppraisers(Listing_Standard list)
        {
            list.Label("EF_Settings_CaravanAppraiser".Translate(EntadSettings.CaravanAppraiserChance.ToStringPercent()));
            EntadSettings.CaravanAppraiserChance = Mathf.Round(list.Slider(EntadSettings.CaravanAppraiserChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_SettlementAppraiser".Translate(EntadSettings.SettlementAppraiserChance.ToStringPercent()));
            EntadSettings.SettlementAppraiserChance = Mathf.Round(list.Slider(EntadSettings.SettlementAppraiserChance, 0f, 1f) * 20f) / 20f;
            list.Label("EF_Settings_Appraiser_Desc".Translate());
            list.Gap();
            list.Label("EF_Settings_IdentifyMinPrice".Translate(((float)EntadSettings.IdentifyMinPrice).ToStringMoney()));
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
