using System.Collections.Generic;
using System.Linq;
using Verse;

namespace EntadFramework
{
    // Relative weights for picking (defaults come from the mod settings) a trait rarity. Rarities with no eligible trait are skipped
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

        // Picks a trait from the candidates: first a rarity by weight, then uniformly among the traits that can appear
        // at that rarity (fixed-rarity ones at exactly that level, rarity-scaling ones whose range includes it).
        // "allowed" optionally limits the rarities considered.
        public EntadTraitDef Pick(IList<EntadTraitDef> candidates, System.Func<EntadTraitDef, float> weightOf = null)
        {
            return Pick(candidates, weightOf, null, out _);
        }

        public EntadTraitDef Pick(IList<EntadTraitDef> candidates, System.Func<EntadTraitDef, float> weightOf, ICollection<EntadRarity> allowed, out EntadRarity rarity)
        {
            rarity = EntadRarity.Common;
            if (candidates.NullOrEmpty()) return null;

            var byRarity = new List<EntadTraitDef>[5];
            foreach (var d in candidates)
            {
                EntadRarity hi = d.MaxRarity;
                for (var r = d.rarity; r <= hi; r++)
                {
                    if (allowed != null && allowed.Count > 0 && !allowed.Contains(r)) continue;
                    (byRarity[(int)r] ?? (byRarity[(int)r] = new List<EntadTraitDef>())).Add(d);
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

        private struct Pair
        {
            public EntadTraitDef def;
            public EntadRarity rarity;
            public float traitWeight;
        }

        // Point-target picking (EntadApi). The same odds as Pick, a rarity by its weight and then a trait within it,
        // but over the (trait, rarity) pairs "fits" accepts, so a rarity with no accepted pair drops out. "pairWeight"
        // then scales each pair's chance. Returns null when no pair fits.
        public EntadTraitDef PickWhere(IList<EntadTraitDef> candidates, System.Func<EntadTraitDef, float> weightOf, ICollection<EntadRarity> allowed,
            System.Func<EntadTraitDef, EntadRarity, bool> fits, System.Func<EntadTraitDef, EntadRarity, float> pairWeight, out EntadRarity rarity)
        {
            rarity = EntadRarity.Common;
            if (candidates.NullOrEmpty()) return null;

            var pairs = new List<Pair>();
            var sums = new float[5];
            foreach (var d in candidates)
            {
                float wd = weightOf?.Invoke(d) ?? 1f;
                if (wd <= 0f) continue;
                EntadRarity hi = d.MaxRarity;
                for (var r = d.rarity; r <= hi; r++)
                {
                    if (allowed != null && allowed.Count > 0 && !allowed.Contains(r)) continue;
                    if (fits != null && !fits(d, r)) continue;
                    pairs.Add(new Pair { def = d, rarity = r, traitWeight = wd });
                    sums[(int)r] += wd;
                }
            }

            var final = new float[pairs.Count];
            float total = 0f;
            for (int i = 0; i < pairs.Count; i++)
            {
                var p = pairs[i];
                float wr = weights[p.rarity];
                if (wr <= 0f) continue;
                final[i] = wr * p.traitWeight / sums[(int)p.rarity] * (pairWeight?.Invoke(p.def, p.rarity) ?? 1f);
                total += final[i];
            }
            if (total <= 0f) return null;

            float roll = Rand.Value * total;
            int chosen = pairs.Count - 1;
            for (int i = 0; i < pairs.Count; i++)
            {
                roll -= final[i];
                if (final[i] > 0f && roll <= 0f) { chosen = i; break; }
            }
            while (chosen > 0 && final[chosen] <= 0f) chosen--;
            rarity = pairs[chosen].rarity;
            return pairs[chosen].def;
        }

        private static EntadTraitDef PickWithin(List<EntadTraitDef> list, System.Func<EntadTraitDef, float> weightOf)
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
