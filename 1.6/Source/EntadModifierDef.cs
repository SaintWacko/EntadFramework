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

    // A stat modifier whose value is rolled between min and max when applied to an item
    public class StatModifierRange
    {
        public StatDef stat;
        public float min;
        public float max;

        public float Roll() => Rand.Range(min, max);

        // Position of a value within [min, max], 0..1 (0.5 when the range is degenerate)
        public float Normalize(float value)
        {
            return max - min > 0.0001f ? UnityEngine.Mathf.InverseLerp(min, max, value) : 0.5f;
        }
    }

    // Building properties that aren't stats (they live in comp properties) and can be scaled by a modifier
    public enum EntadBuildingProperty
    {
        LightRadius,
        HeatOutput,
        FuelConsumptionRate,
        FuelCapacity,
        PowerGeneration
    }

    // A multiplier on a building property, rolled between min and max when applied to an item
    public class BuildingPropertyRange
    {
        public EntadBuildingProperty property;
        public float min = 1f;
        public float max = 1f;

        public float Roll() => Rand.Range(min, max);

        public float Normalize(float value)
        {
            return max - min > 0.0001f ? UnityEngine.Mathf.InverseLerp(min, max, value) : 0.5f;
        }

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
                    default: return "Power generation";
                }
            }
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
        Building = 16
    }

    public class EntadModifierDef : Def
    {
        public List<string> categories = new List<string>();
        public EntadRarity rarity = EntadRarity.Common;

        public List<StatModifierRange> statOffsets;
        public List<StatModifierRange> statFactors;

        // Multipliers on building properties that aren't stats: light radius, heat output, fuel consumption
        // rate, fuel capacity, power generation. Only applies to buildings that have the matching component.
        public List<BuildingPropertyRange> buildingFactors;

        // Mood effect: while equipped (weapons/apparel) or after use (furniture) the wearer/user gets this thought.
        // The lifetime in hours is rolled in thoughtHours when the modifier is applied.
        public ThoughtDef thought;
        // Alternative to a fixed thought: when the modifier is applied, a random existing memory thought whose
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

        // Modifiers that can never share an item with this one (symmetric: listing it on either def is enough)
        public List<EntadModifierDef> exclusiveWith;

        // Tags shared with other modifiers that mean "only one of us per item"
        public List<string> exclusivityTags;

        // By default two modifiers that affect the same thing (a stat, meal nutrition, meal quality, an ability)
        // can't share an item. Set to true to opt this modifier out of that implicit check; explicit
        // exclusiveWith / exclusivityTags still apply.
        public bool allowOverlap;

        public string discoveryMessage;

        public bool ConflictsWith(EntadModifierDef other)
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

        // Base market value contributed by this modifier, by rarity
        public float BaseMarketValue
        {
            get
            {
                switch (rarity)
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
                t.IsMemory && t.thoughtClass == typeof(Thought_Memory) && t.stages != null && t.stages.Count == 1
                && t.stages[0] != null && t.requiredTraits.NullOrEmpty() && t.requiredGenes.NullOrEmpty()
                && t.nextThought == null && t.stages[0].baseMoodEffect != 0f
                && thoughtMoodRange.Includes(MoodEffectOf(t))).ToList());
        }

        public EntadEffectKind EffectKinds
        {
            get
            {
                EntadEffectKind kinds = EntadEffectKind.None;
                if (AllRanges().Any()) kinds |= EntadEffectKind.Stat;
                if (HasMood) kinds |= EntadEffectKind.Mood;
                if (HasMealEffect) kinds |= EntadEffectKind.Meal;
                if (!buildingFactors.NullOrEmpty()) kinds |= EntadEffectKind.Building;
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
                foreach (var m in existing.activeModifiers)
                    if (m.def == this || ConflictsWith(m.def)) return false;

            if (HasMoodRange && MoodCandidates().Count == 0) return false;
            if (HasMealEffect && td.surfaceType != SurfaceType.Eat) return false;
            if (!buildingFactors.NullOrEmpty() && buildingFactors.Any(b => !b.AppliesTo(td))) return false;

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
            if (td.statBases != null && td.statBases.Any(m => m.stat == stat)) return true;
            return stat.showIfUndefined && stat.Worker.ShouldShowFor(StatRequest.For(thing)) && !stat.Worker.IsDisabledFor(thing) && StatDefHasEquippedOrBase(stat, td);
        }

        // Stats such as equipped offsets work on any equippable item
        private static bool StatDefHasEquippedOrBase(StatDef stat, ThingDef td)
        {
            return stat.category == StatCategoryDefOf.EquippedStatOffsets && (td.IsWeapon || td.IsApparel);
        }

        public IEnumerable<StatModifierRange> AllRanges()
        {
            foreach (var r in Ranges(statOffsets)) yield return r;
            foreach (var r in Ranges(statFactors)) yield return r;
        }

        private static IEnumerable<StatModifierRange> Ranges(List<StatModifierRange> l)
        {
            if (l != null) foreach (var r in l) yield return r;
        }
    }
}
