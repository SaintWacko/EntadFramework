using System.Collections.Generic;
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

        // Relative weights for choosing a modifier's rarity; used by EntadRarityChances.Default
        public static readonly Dictionary<EntadRarity, float> Weights = new Dictionary<EntadRarity, float>(DefaultWeights);

        public static void ResetRarityWeights()
        {
            foreach (var kv in DefaultWeights) Weights[kv.Key] = kv.Value;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = Weights[r];
                Scribe_Values.Look(ref w, "weight" + r, DefaultWeights[r]);
                Weights[r] = Mathf.Max(0f, w);
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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);

            list.Label("Rarity curve: relative chance of each rarity when a modifier is picked. Rarities with no eligible modifier are skipped and the rest are renormalised.");
            list.Gap(6f);

            float total = 0f;
            foreach (var w in EntadSettings.Weights.Values) total += w;

            foreach (EntadRarity r in System.Enum.GetValues(typeof(EntadRarity)))
            {
                float w = EntadSettings.Weights[r];
                string pct = total > 0f ? (w / total).ToStringPercent() : "0%";
                list.Label($"{r}: weight {w:0.#} ({pct} of the total)");
                EntadSettings.Weights[r] = Mathf.Round(list.Slider(w, 0f, 100f) * 2f) / 2f;
            }

            list.Gap(6f);
            if (list.ButtonText("Reset to defaults")) EntadSettings.ResetRarityWeights();

            list.End();
        }
    }
}
