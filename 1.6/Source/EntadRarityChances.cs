using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EntadFramework
{
    // Relative weights for picking (defaults come from the mod settings) a modifier rarity. Rarities with no eligible modifier are skipped
    // and the remaining weights are renormalised.
    public class EntadRarityChances
    {
        private readonly Dictionary<EntadRarity, float> weights = new Dictionary<EntadRarity, float>
        {
            { EntadRarity.Common, EntadSettings.Weights[EntadRarity.Common] },
            { EntadRarity.Uncommon, EntadSettings.Weights[EntadRarity.Uncommon] },
            { EntadRarity.Rare, EntadSettings.Weights[EntadRarity.Rare] },
            { EntadRarity.Epic, EntadSettings.Weights[EntadRarity.Epic] },
            { EntadRarity.Legendary, EntadSettings.Weights[EntadRarity.Legendary] },
        };

        public static EntadRarityChances Default => new EntadRarityChances();

        public float this[EntadRarity rarity]
        {
            get => weights[rarity];
            set => weights[rarity] = value < 0f ? 0f : value;
        }

        public EntadRarityChances Set(EntadRarity rarity, float weight)
        {
            this[rarity] = weight;
            return this;
        }

        // Picks a modifier from the candidates: first a rarity by weight, then uniformly among the modifiers that can appear
        // at that rarity (fixed-rarity ones at exactly that level, rarity-scaling ones whose range includes it).
        // "allowed" optionally limits the rarities considered.
        public EntadModifierDef Pick(IList<EntadModifierDef> candidates, System.Func<EntadModifierDef, float> weightOf = null)
        {
            return Pick(candidates, weightOf, null, out _);
        }

        public EntadModifierDef Pick(IList<EntadModifierDef> candidates, System.Func<EntadModifierDef, float> weightOf, ICollection<EntadRarity> allowed, out EntadRarity rarity)
        {
            rarity = EntadRarity.Common;
            if (candidates.NullOrEmpty()) return null;

            var byRarity = new List<EntadModifierDef>[5];
            foreach (var d in candidates)
            {
                EntadRarity hi = d.MaxRarity;
                for (var r = d.rarity; r <= hi; r++)
                {
                    if (allowed != null && allowed.Count > 0 && !allowed.Contains(r)) continue;
                    (byRarity[(int)r] ?? (byRarity[(int)r] = new List<EntadModifierDef>())).Add(d);
                }
            }

            var open = new List<EntadRarity>();
            float total = 0f;
            for (int r = 0; r < byRarity.Length; r++)
            {
                if (byRarity[r] == null || weights[(EntadRarity)r] <= 0f) continue;
                open.Add((EntadRarity)r);
                total += weights[(EntadRarity)r];
            }

            if (open.Count == 0)
            {
                var any = candidates.RandomElement();
                rarity = any.ClampRarity(any.rarity);
                return any;
            }

            float roll = Rand.Value * total;
            EntadRarity chosen = open[open.Count - 1];
            foreach (var r in open)
            {
                roll -= weights[r];
                if (roll <= 0f) { chosen = r; break; }
            }
            rarity = chosen;
            return PickWithin(byRarity[(int)chosen], weightOf);
        }

        private static EntadModifierDef PickWithin(List<EntadModifierDef> list, System.Func<EntadModifierDef, float> weightOf)
        {
            if (weightOf == null) return list.RandomElement();
            float total = 0f;
            foreach (var d in list) total += weightOf(d);
            if (total <= 0f) return list.RandomElement();
            float roll = Rand.Value * total;
            foreach (var d in list)
            {
                roll -= weightOf(d);
                if (roll <= 0f) return d;
            }
            return list[list.Count - 1];
        }
    }
}
