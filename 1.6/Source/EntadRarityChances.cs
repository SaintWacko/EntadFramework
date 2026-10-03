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

        // Picks a modifier from the candidates: first a rarity by weight, then uniformly within it.
        public EntadModifierDef Pick(IList<EntadModifierDef> candidates)
        {
            if (candidates.NullOrEmpty()) return null;

            var available = candidates.GroupBy(c => c.rarity).Where(g => weights[g.Key] > 0f).ToList();
            if (available.Count == 0) return candidates.RandomElement();

            float total = available.Sum(g => weights[g.Key]);
            float roll = Rand.Value * total;
            foreach (var group in available)
            {
                roll -= weights[group.Key];
                if (roll <= 0f) return group.RandomElement();
            }
            return available.Last().RandomElement();
        }
    }
}
