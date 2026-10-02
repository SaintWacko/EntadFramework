using System.Collections.Generic;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public enum EntadRarity
    {
        Common,
        Uncommon,
        Rare,
        VeryRare,
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
                    case EntadRarity.VeryRare: return 800f;
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
