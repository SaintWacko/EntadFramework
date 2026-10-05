using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public enum EntadRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    // A stat trait whose value is rolled between min and max when applied to an item
    public class StatTraitRange
    {
        public StatDef stat;
        public float min;
        public float max;

        private float Roll() => Rand.Range(min, max);

        // Rarity scaling widens the whole range: offsets multiply, factors scale their distance from 1
        public float RollScaled(float scale, bool factor) => EntadTraitDef.Scale(Roll(), scale, factor);

        public float NormalizeScaled(float value, float scale, bool factor)
        {
            float lo = EntadTraitDef.Scale(min, scale, factor), hi = EntadTraitDef.Scale(max, scale, factor);
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }
    }

    // Building properties that aren't stats (they live in comp properties) and can be scaled by a trait
    public enum EntadBuildingProperty
    {
        LightRadius,
        HeatOutput,
        FuelConsumptionRate,
        FuelCapacity,
        PowerGeneration,
        PowerConsumption,
        TemperatureControlPower
    }

    // A multiplier on a building property, rolled between min and max when applied to an item
    public class BuildingPropertyRange
    {
        public EntadBuildingProperty property;
        public float min = 1f;
        public float max = 1f;

        private float Roll() => Rand.Range(min, max);

        public float RollScaled(float scale) => EntadTraitDef.Scale(Roll(), scale, true);

        public float NormalizeScaled(float value, float scale)
        {
            float lo = EntadTraitDef.Scale(min, scale, true), hi = EntadTraitDef.Scale(max, scale, true);
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }

        // Private on CompProperties_Power; the public PowerConsumption applies research upgrades on top
        private static readonly HarmonyLib.AccessTools.FieldRef<CompProperties_Power, float> BasePowerConsumption =
            HarmonyLib.AccessTools.FieldRefAccess<CompProperties_Power, float>("basePowerConsumption");

        public bool AppliesTo(ThingDef td)
        {
            if (td?.comps == null) return false;
            foreach (var c in td.comps)
            {
                switch (property)
                {
                    case EntadBuildingProperty.LightRadius: if (c is CompProperties_Glower) return true; break;
                    case EntadBuildingProperty.HeatOutput: if (c is CompProperties_HeatPusher) return true; break;
                    case EntadBuildingProperty.FuelConsumptionRate:
                    case EntadBuildingProperty.FuelCapacity: if (c is CompProperties_Refuelable) return true; break;
                    case EntadBuildingProperty.TemperatureControlPower: if (c is CompProperties_TempControl) return true; break;
                    case EntadBuildingProperty.PowerConsumption:
                        if (c is CompProperties_Power pp && pp.compClass != null && !typeof(CompPowerPlant).IsAssignableFrom(pp.compClass) && BasePowerConsumption(pp) > 0f) return true; break;
                    case EntadBuildingProperty.PowerGeneration: if (c.compClass != null && typeof(CompPowerPlant).IsAssignableFrom(c.compClass)) return true; break;
                }
            }
            return false;
        }

        public string Label
        {
            get
            {
                switch (property)
                {
                    case EntadBuildingProperty.LightRadius: return "Light radius";
                    case EntadBuildingProperty.HeatOutput: return "Heat output";
                    case EntadBuildingProperty.FuelConsumptionRate: return "Fuel consumption rate";
                    case EntadBuildingProperty.FuelCapacity: return "Fuel capacity";
                    case EntadBuildingProperty.PowerConsumption: return "Power consumption";
                    case EntadBuildingProperty.TemperatureControlPower: return "Heating/cooling power";
                    default: return "Power generation";
                }
            }
        }
    }

    // Extra damage dealt alongside a weapon's normal damage on each hit; amount is rolled when applied
    public class ExtraDamageRange
    {
        public DamageDef damageType;
        public float min = 1f;
        public float max = 1f;

        private float Roll() => Rand.Range(min, max);

        public float RollScaled(float scale) => Roll() * scale;

        public float NormalizeScaled(float value, float scale)
        {
            float lo = min * scale, hi = max * scale;
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }
    }

    [System.Flags]
    public enum EntadEffectKind
    {
        None = 0,
        Stat = 1,
        Mood = 2,
        Meal = 4,
        Ability = 8,
        Building = 16,
        Fuel = 32,
        Damage = 64,
        All = Stat | Mood | Meal | Ability | Building | Fuel | Damage
    }

    public class EntadTraitDef : Def
    {
        public List<string> categories = new List<string>();
        public EntadRarity rarity = EntadRarity.Common;

        // When true, "rarity" is the lowest rarity this trait appears at. It can also appear at every higher
        // rarity up to MaxRarity, with its min/max scaled by the rarity multipliers in the mod settings (relative to
        // this def's own rarity). When false the trait only ever appears at "rarity".
        public bool scalesWithRarity;

        public const float MinScaledFactor = 0.05f;

        public static float Scale(float value, float scale, bool factor)
        {
            return factor ? UnityEngine.Mathf.Max(MinScaledFactor, 1f + (value - 1f) * scale) : value * scale;
        }

        // Multiplier applied to this def's ranges when rolled at the given rarity (1 at its own rarity)
        public float RarityScale(EntadRarity at)
        {
            if (!scalesWithRarity) return 1f;
            float baseMult = EntadSettings.RarityMultiplier(rarity);
            return baseMult > 0.0001f ? EntadSettings.RarityMultiplier(at) / baseMult : 1f;
        }

        // Whether the scaled ranges still make sense at this rarity: a factor can't be pushed to or below zero, and an
        // offset can't exceed what its stat allows. Rarities past the first one that fails are not offered.
        private bool FitsAt(EntadRarity at)
        {
            float scale = RarityScale(at);
            foreach (var r in Ranges(statFactors))
                if (1f + (UnityEngine.Mathf.Min(r.min, r.max) - 1f) * scale < MinScaledFactor) return false;
            if (buildingFactors != null)
                foreach (var b in buildingFactors)
                    if (1f + (UnityEngine.Mathf.Min(b.min, b.max) - 1f) * scale < MinScaledFactor) return false;
            foreach (var r in Ranges(statOffsets))
                if (r.stat != null && UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(r.min), UnityEngine.Mathf.Abs(r.max)) * scale > r.stat.maxValue) return false;
            return true;
        }

        public EntadRarity MaxRarity
        {
            get
            {
                if (!scalesWithRarity) return rarity;
                EntadRarity best = rarity;
                for (var r = rarity + 1; r <= EntadRarity.Legendary; r++)
                {
                    if (!FitsAt(r)) break;
                    best = r;
                }
                return best;
            }
        }

        public bool CanAppearAt(EntadRarity at) => scalesWithRarity ? at >= rarity && at <= MaxRarity : at == rarity;

        public EntadRarity ClampRarity(EntadRarity at)
        {
            if (!scalesWithRarity) return rarity;
            return at < rarity ? rarity : (at > MaxRarity ? MaxRarity : at);
        }

        public List<StatTraitRange> statOffsets;
        public List<StatTraitRange> statFactors;

        // Multipliers on building properties that aren't stats: light radius, heat output, fuel consumption
        // rate, fuel capacity, power generation. Only applies to buildings that have the matching component.
        public List<BuildingPropertyRange> buildingFactors;

        // Weapons only. changeDamageType replaces the damage type of the weapon's attacks (melee and ranged);
        // extraDamage adds more damage of other types to each hit.
        public DamageDef changeDamageType;
        public List<ExtraDamageRange> extraDamage;

        // Ranged weapons that don't fire a plain direct-hit projectile (explosive projectiles, beams, flame streams...)
        // deal their damage some other way, so damage added to a hit would never apply. Melee weapons are never area weapons.
        public static bool IsAreaWeapon(ThingDef td)
        {
            if (!td.IsRangedWeapon) return false;
            if (td.Verbs != null)
                foreach (var v in td.Verbs)
                {
                    var proj = v.defaultProjectile;
                    if (proj?.projectile == null) continue;
                    if (typeof(Verb_ShootBeam).IsAssignableFrom(v.verbClass)) continue;
                    if (proj.projectile.explosionRadius > 0f || typeof(Projectile_Explosive).IsAssignableFrom(proj.thingClass)) continue;
                    return false;
                }
            return true;
        }

        public bool HasDamageEffect => changeDamageType != null || !extraDamage.NullOrEmpty();

        // Fuel for buildings with a refuelable component (campfires, generators...). By default these are
        // accepted in addition to the building's normal fuel; with replaceFuel only these are accepted.
        public List<ThingDef> fuelTypes;
        // Every thing in these categories is also accepted, including modded things added to them (e.g. all stone blocks)
        public List<ThingCategoryDef> fuelCategories;

        private List<ThingDef> allFuelTypes;

        // fuelTypes plus everything in fuelCategories, resolved on first use because categories fill up after loading
        public List<ThingDef> AllFuelTypes
        {
            get
            {
                if (allFuelTypes == null)
                {
                    var set = new HashSet<ThingDef>();
                    if (fuelTypes != null) foreach (var t in fuelTypes) if (t != null) set.Add(t);
                    if (fuelCategories != null) foreach (var c in fuelCategories) if (c != null) foreach (var t in c.DescendantThingDefs) set.Add(t);
                    allFuelTypes = set.ToList();
                }
                return allFuelTypes;
            }
        }
        public bool replaceFuel;

        // Mood effect: while equipped (weapons/apparel) or after use (furniture) the wearer/user gets this thought.
        // The lifetime in hours is rolled in thoughtHours when the trait is applied.
        public ThoughtDef thought;
        // Alternative to a fixed thought: when the trait is applied, a random existing memory thought whose
        // mood effect lies within this range is picked. A range of 0..0 means unused.
        public FloatRange thoughtMoodRange = new FloatRange(0f, 0f);

        public bool HasMoodRange => thought == null && (thoughtMoodRange.min != 0f || thoughtMoodRange.max != 0f);
        public bool HasMood => thought != null || HasMoodRange;
        public FloatRange thoughtHours = new FloatRange(1f, 1f);

        // Table effects on meals eaten off this furniture. Nutrition is multiplied by a factor rolled in
        // mealNutritionFactor; mealThought is added to the thoughts the eater gets from the meal (e.g. a fine meal).
        public FloatRange mealNutritionFactor = new FloatRange(1f, 1f);
        public ThoughtDef mealThought;

        // Shifts the quality of meals eaten here along simple -> fine -> lavish (e.g. +1: simple becomes fine,
        // -1: lavish becomes fine). Applied to the thoughts the meal gives, clamped to the ends of that ladder.
        public int mealQualityOffset;

        public bool HasMealEffect => mealThought != null || mealQualityOffset != 0 || mealNutritionFactor.min != 1f || mealNutritionFactor.max != 1f;

        // Weapons and apparel grant these to the pawn using them; furniture offers them as right-click
        // actions that a pawn walks over and performs (with a per-item cooldown)
        public List<AbilityDef> abilities;

        // Optional limits on the abilities above. Cooldown (ticks) overrides the ability's own cooldown. With charges,
        // the ability can be used that many times in a row, and gains a charge back at the end of each cooldown.
        public int abilityCharges;
        public int abilityCooldownTicks;
        // How long using the ability takes (ticks). Equipped abilities use it as their warmup; furniture abilities make
        // the pawn work at the furniture for this long. 0 keeps the ability's own casting time.
        public int abilityCastTicks;

        // Traits that can never share an item with this one (symmetric: listing it on either def is enough)
        public List<EntadTraitDef> exclusiveWith;

        // Tags shared with other traits that mean "only one of us per item"
        public List<string> exclusivityTags;

        // By default two traits that affect the same thing (a stat, meal nutrition, meal quality, an ability)
        // can't share an item. Set to true to opt this trait out of that implicit check; explicit
        // exclusiveWith / exclusivityTags still apply.
        public bool allowOverlap;

        public bool ConflictsWith(EntadTraitDef other)
        {
            if (other == null) return false;
            if (other == this) return true;
            if (exclusiveWith != null && exclusiveWith.Contains(other)) return true;
            if (other.exclusiveWith != null && other.exclusiveWith.Contains(this)) return true;
            if (exclusivityTags != null && other.exclusivityTags != null && exclusivityTags.Any(t => other.exclusivityTags.Contains(t))) return true;
            if (allowOverlap || other.allowOverlap) return false;

            if (mealNutritionFactor.min != 1f || mealNutritionFactor.max != 1f)
                if (other.mealNutritionFactor.min != 1f || other.mealNutritionFactor.max != 1f) return true;
            if (mealQualityOffset != 0 && other.mealQualityOffset != 0) return true;
            if (!AllFuelTypes.NullOrEmpty() && !other.AllFuelTypes.NullOrEmpty()) return true;
            if (changeDamageType != null && other.changeDamageType != null) return true;
            if (!buildingFactors.NullOrEmpty() && !other.buildingFactors.NullOrEmpty()
                && buildingFactors.Any(a => other.buildingFactors.Any(b => a.property == b.property))) return true;
            if (!abilities.NullOrEmpty() && !other.abilities.NullOrEmpty() && abilities.Any(a => other.abilities.Contains(a))) return true;
            if (thought != null && thought == other.thought) return true;

            foreach (var a in AllRanges())
            {
                if (a.stat == null) continue;
                foreach (var b in other.AllRanges())
                    if (a.stat == b.stat) return true;
            }
            return false;
        }

        // Flat value added once to an item with any unidentified trait, instead of those traits' own value
        // (between a common and an uncommon trait)
        public const float UnidentifiedMarketValue = 60f;

        // Base market value contributed by a trait of this rarity
        public static float BaseMarketValueAt(EntadRarity at)
        {
            {
                switch (at)
                {
                    case EntadRarity.Uncommon: return 100f;
                    case EntadRarity.Rare: return 300f;
                    case EntadRarity.Epic: return 800f;
                    case EntadRarity.Legendary: return 2000f;
                    default: return 30f;
                }
            }
        }

        public static float MoodEffectOf(ThoughtDef t) => t.stages[0].baseMoodEffect;

        // Plain memory thoughts with a single stage, usable on any pawn without extra context
        private List<ThoughtDef> moodCandidates;

        public List<ThoughtDef> MoodCandidates()
        {
            if (!HasMoodRange) return new List<ThoughtDef>();
            return moodCandidates ?? (moodCandidates = DefDatabase<ThoughtDef>.AllDefsListForReading.Where(t =>
                // ThoughtClass (the property), not the raw thoughtClass field: most memory defs never set <thoughtClass>,
                // so the field is null and the old field check left nearly every vanilla and modded memory out.
                // Every loaded mod's ThoughtDefs are included: this runs on first use, after all defs are loaded.
                t.IsMemory && t.ThoughtClass == typeof(Thought_Memory) && t.stages != null && t.stages.Count == 1
                && t.stages[0] != null && t.stages[0].visible && t.requiredTraits.NullOrEmpty() && t.requiredGenes.NullOrEmpty()
                && t.nextThought == null && t.thoughtToMake == null && t.stages[0].baseMoodEffect != 0f
                && IsSelfContained(t.stages[0])
                && thoughtMoodRange.Includes(MoodEffectOf(t))).ToList());
        }

        // A label and description that read on their own. Some memories expect context the item can't give: a
        // placeholder for another pawn or a precept ({0}, [PAWN_nameDef]) would show up literally.
        private static bool IsSelfContained(ThoughtStage s)
        {
            return !s.label.NullOrEmpty() && !s.description.NullOrEmpty()
                && s.label.IndexOfAny(PlaceholderChars) < 0 && s.description.IndexOfAny(PlaceholderChars) < 0;
        }

        private static readonly char[] PlaceholderChars = { '{', '[' };

        // Cached: IsRevealed/AnyHidden read this on stat and draw paths, and computing it allocates AllRanges
        // iterators. Everything it reads is def data fixed after loading (EntadAbilityPrep swaps ability entries
        // but never empties the list), and it is first read in play, after every reference has resolved.
        private EntadEffectKind? effectKinds;

        public EntadEffectKind EffectKinds => effectKinds ?? (effectKinds = ComputeEffectKinds()).Value;

        private EntadEffectKind ComputeEffectKinds()
        {
            {
                EntadEffectKind kinds = EntadEffectKind.None;
                if (AllRanges().Any()) kinds |= EntadEffectKind.Stat;
                if (HasMood) kinds |= EntadEffectKind.Mood;
                if (HasMealEffect) kinds |= EntadEffectKind.Meal;
                if (!buildingFactors.NullOrEmpty()) kinds |= EntadEffectKind.Building;
                if (!AllFuelTypes.NullOrEmpty()) kinds |= EntadEffectKind.Fuel;
                if (HasDamageEffect) kinds |= EntadEffectKind.Damage;
                if (!abilities.NullOrEmpty()) kinds |= EntadEffectKind.Ability;
                return kinds;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            foreach (var r in Ranges(statOffsets))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat offset range";
            foreach (var r in Ranges(statFactors))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat factor range";
            if (thought != null && !thought.IsMemory) yield return $"{defName}: thought {thought.defName} is not a memory thought";
            if (buildingFactors != null)
                foreach (var b in buildingFactors)
                    if (b.min > b.max || b.min < 0f) yield return $"{defName}: invalid building factor range for {b.property}";
            if (extraDamage != null && extraDamage.Any(e => e.damageType == null || e.min > e.max)) yield return $"{defName}: invalid extraDamage entry";
            if (fuelCategories != null && fuelCategories.Any(c => c == null)) yield return $"{defName}: fuelCategories contains an unknown def";
            if (fuelTypes != null && fuelTypes.Any(f => f == null)) yield return $"{defName}: fuelTypes contains an unknown def";
            if (mealNutritionFactor.min > mealNutritionFactor.max) yield return $"{defName}: mealNutritionFactor min is greater than max";
            if (thought != null && (thoughtMoodRange.min != 0f || thoughtMoodRange.max != 0f)) yield return $"{defName}: specify either thought or thoughtMoodRange, not both";
            if (thoughtMoodRange.min > thoughtMoodRange.max) yield return $"{defName}: thoughtMoodRange min is greater than max";
            if (HasMood && thoughtHours.min > thoughtHours.max) yield return $"{defName}: thoughtHours min is greater than max";
        }

        // Categories restrict by item type; stats must additionally be meaningful for the item
        public bool CanApplyTo(Thing thing)
        {
            ThingDef td = thing?.def;
            if (td == null) return false;

            var existing = thing.TryGetComp<CompEntad>();
            if (existing != null)
                foreach (var m in existing.activeTraits)
                    if (m.def == this || ConflictsWith(m.def)) return false;

            if (HasMoodRange && MoodCandidates().Count == 0) return false;
            // Furniture moods need a pawn to use the building (abilities work from the right-click menu on any furniture)
            if (HasMood && td.building != null && !EntadUtility.IsPawnUsable(td)) return false;
            if (HasDamageEffect && (!td.IsWeapon || IsAreaWeapon(td))) return false;
            if (HasMealEffect && td.surfaceType != SurfaceType.Eat) return false;
            if (!buildingFactors.NullOrEmpty() && buildingFactors.Any(b => !b.AppliesTo(td))) return false;
            if (!AllFuelTypes.NullOrEmpty() && !(td.comps != null && td.comps.Any(c => c is CompProperties_Refuelable))) return false;

            if (!categories.NullOrEmpty())
            {
                bool match = false;
                foreach (string c in categories)
                {
                    if ((EntadUtility.KindOf(td) & EntadUtility.ParseKind(c)) != EntadItemKind.None)
                    { match = true; break; }
                }
                if (!match) return false;
            }

            foreach (var r in Ranges(statFactors))
                if (r.stat != null && IsWearerStat(r.stat) && !(td.statBases?.Any(m => m.stat == r.stat) ?? false)) return false;

            foreach (var r in AllRanges())
            {
                if (r.stat == null) continue;
                if (r.stat == StatDefOf.MarketValue) continue;
                if (!StatAppliesTo(r.stat, thing)) return false;
            }
            return true;
        }

        private static bool StatAppliesTo(StatDef stat, Thing thing)
        {
            ThingDef td = thing.def;
            if (td.IsWeapon)
            {
                if (IsRangedOnlyStat(stat) && !td.IsRangedWeapon) return false;
                if (IsMeleeOnlyStat(stat) && !td.IsMeleeWeapon) return false;
            }
            if (td.statBases != null && td.statBases.Any(m => m.stat == stat)) return true;
            // Weapon multipliers (e.g. RangedWeapon_DamageMultiplier) have a default value and are never listed in
            // a weapon's statBases, but the game reads them from the weapon, so allow them on the matching weapon type
            if (IsWearerStat(stat) && (td.IsWeapon || td.IsApparel)) return true;
            if (stat.defName.StartsWith("RangedWeapon_")) return td.IsRangedWeapon;
            if (stat.defName.StartsWith("MeleeWeapon_")) return td.IsMeleeWeapon;
            return stat.showIfUndefined && stat.Worker.ShouldShowFor(StatRequest.For(thing)) && !stat.Worker.IsDisabledFor(thing) && StatDefHasEquippedOrBase(stat, td);
        }

        public static bool IsRangedOnlyStat(StatDef stat) =>
            stat.category == StatCategoryDefOf.Weapon_Ranged || stat.defName.StartsWith("RangedWeapon_") || stat.defName.StartsWith("Accuracy");

        public static bool IsMeleeOnlyStat(StatDef stat) =>
            stat.category == StatCategoryDefOf.Weapon_Melee || stat.defName.StartsWith("MeleeWeapon_");

        // Traits made for weapons (weapon-only categories, damage effects or weapon stats) rather than general ones
        private int weaponSpecific = -1;

        public bool IsWeaponSpecific
        {
            get
            {
                if (weaponSpecific < 0)
                {
                    bool result = HasDamageEffect
                        || (!categories.NullOrEmpty() && categories.All(c => EntadUtility.ParseKind(c) == EntadItemKind.Weapon))
                        || AllRanges().Any(r => r.stat != null && (IsRangedOnlyStat(r.stat) || IsMeleeOnlyStat(r.stat)));
                    weaponSpecific = result ? 1 : 0;
                }
                return weaponSpecific == 1;
            }
        }

        private static readonly HashSet<string> WearerCategories = new HashSet<string>
        {
            "EquippedStatOffsets", "BasicsPawn", "PawnWork", "PawnCombat", "PawnSocial", "PawnMisc",
            "PawnHealth", "PawnFood", "PawnResistances", "PawnPsyfocus", "Meditation"
        };

        // Stats of the pawn carrying a weapon or apparel. Entad traits add to these as equipped stat offsets.
        public static bool IsWearerStat(StatDef stat) => stat.category != null && WearerCategories.Contains(stat.category.defName);

        // Stats such as equipped offsets work on any equippable item
        private static bool StatDefHasEquippedOrBase(StatDef stat, ThingDef td)
        {
            return stat.category == StatCategoryDefOf.EquippedStatOffsets && (td.IsWeapon || td.IsApparel);
        }

        public IEnumerable<StatTraitRange> AllRanges()
        {
            foreach (var r in Ranges(statOffsets)) yield return r;
            foreach (var r in Ranges(statFactors)) yield return r;
        }

        private static IEnumerable<StatTraitRange> Ranges(List<StatTraitRange> l)
        {
            if (l != null) foreach (var r in l) yield return r;
        }
    }
}
