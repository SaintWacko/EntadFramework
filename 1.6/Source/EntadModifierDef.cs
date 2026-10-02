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

    public class EntadModifierDef : Def
    {
        public List<string> categories = new List<string>();
        public EntadRarity rarity = EntadRarity.Common;

        public List<StatModifierRange> statOffsets;
        public List<StatModifierRange> statFactors;

        // Mood effect: while equipped (weapons/apparel) or after use (furniture) the wearer/user gets this thought.
        // The lifetime in hours is rolled in thoughtHours when the modifier is applied.
        public ThoughtDef thought;
        public FloatRange thoughtHours = new FloatRange(1f, 1f);

        // Table effects on meals eaten off this furniture. Nutrition is multiplied by a factor rolled in
        // mealNutritionFactor; mealThought is added to the thoughts the eater gets from the meal (e.g. a fine meal).
        public FloatRange mealNutritionFactor = new FloatRange(1f, 1f);
        public ThoughtDef mealThought;

        public bool HasMealEffect => mealThought != null || mealNutritionFactor.min != 1f || mealNutritionFactor.max != 1f;

        // Abilities granted to the pawn wearing/wielding the item (weapons and apparel only)
        public List<AbilityDef> abilities;

        public string discoveryMessage;

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

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            foreach (var r in Ranges(statOffsets))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat offset range";
            foreach (var r in Ranges(statFactors))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat factor range";
            if (thought != null && !thought.IsMemory) yield return $"{defName}: thought {thought.defName} is not a memory thought";
            if (mealNutritionFactor.min > mealNutritionFactor.max) yield return $"{defName}: mealNutritionFactor min is greater than max";
            if (thought != null && thoughtHours.min > thoughtHours.max) yield return $"{defName}: thoughtHours min is greater than max";
        }

        // Categories restrict by item type; stats must additionally be meaningful for the item
        public bool CanApplyTo(Thing thing)
        {
            ThingDef td = thing?.def;
            if (td == null) return false;

            if (!abilities.NullOrEmpty() && (EntadUtility.KindOf(td) & (EntadItemKind.Weapon | EntadItemKind.Apparel)) == EntadItemKind.None) return false;
            if (HasMealEffect && td.surfaceType != SurfaceType.Eat) return false;

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
