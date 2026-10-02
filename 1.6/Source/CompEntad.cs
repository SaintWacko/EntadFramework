using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // A modifier applied to a specific item, with the values rolled for it
    public class AppliedEntadModifier : IExposable
    {
        public EntadModifierDef def;
        public List<float> offsetValues = new List<float>();
        public List<float> factorValues = new List<float>();
        public float thoughtHours;
        public float mealNutritionFactor = 1f;

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Collections.Look(ref offsetValues, "offsetValues", LookMode.Value);
            Scribe_Collections.Look(ref factorValues, "factorValues", LookMode.Value);
            Scribe_Values.Look(ref thoughtHours, "thoughtHours");
            Scribe_Values.Look(ref mealNutritionFactor, "mealNutritionFactor", 1f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                offsetValues = offsetValues ?? new List<float>();
                factorValues = factorValues ?? new List<float>();
            }
        }

        public static AppliedEntadModifier Roll(EntadModifierDef def)
        {
            var applied = new AppliedEntadModifier { def = def };
            if (def.statOffsets != null) foreach (var r in def.statOffsets) applied.offsetValues.Add(r.Roll());
            if (def.statFactors != null) foreach (var r in def.statFactors) applied.factorValues.Add(r.Roll());
            if (def.thought != null) applied.thoughtHours = def.thoughtHours.RandomInRange;
            applied.mealNutritionFactor = def.mealNutritionFactor.RandomInRange;
            return applied;
        }

        public int ThoughtDurationTicks => UnityEngine.Mathf.Max(1, (int)(thoughtHours * GenDate.TicksPerHour));

        public float OffsetFor(int i) => i < offsetValues.Count ? offsetValues[i] : 0f;
        public float FactorFor(int i) => i < factorValues.Count ? factorValues[i] : 1f;

        // Average position (0..1) of the rolled values within their ranges
        public float RollQuality()
        {
            float sum = 0f;
            int n = 0;
            if (def.statOffsets != null)
                for (int i = 0; i < def.statOffsets.Count; i++) { sum += def.statOffsets[i].Normalize(OffsetFor(i)); n++; }
            if (def.statFactors != null)
                for (int i = 0; i < def.statFactors.Count; i++) { sum += def.statFactors[i].Normalize(FactorFor(i)); n++; }
            if (def.thought != null) { sum += def.thoughtHours.max - def.thoughtHours.min > 0.0001f ? UnityEngine.Mathf.InverseLerp(def.thoughtHours.min, def.thoughtHours.max, thoughtHours) : 0.5f; n++; }
            var f = def.mealNutritionFactor;
            if (f.max - f.min > 0.0001f) { sum += UnityEngine.Mathf.InverseLerp(f.min, f.max, mealNutritionFactor); n++; }
            return n == 0 ? 0.5f : sum / n;
        }

        // Market value added by this modifier: scales with rarity and where the roll landed
        public float MarketValueOffset() => def.BaseMarketValue * (0.5f + RollQuality());
    }

    public class CompEntad : ThingComp
    {
        public List<AppliedEntadModifier> activeModifiers = new List<AppliedEntadModifier>();

        public CompProperties_Entad Props => (CompProperties_Entad)props;

        public bool HasModifier(EntadModifierDef def) => activeModifiers.Any(m => m.def == def);

        public void AddModifier(EntadModifierDef def)
        {
            activeModifiers.Add(AppliedEntadModifier.Roll(def));
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref activeModifiers, "activeModifiers", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                activeModifiers = activeModifiers ?? new List<AppliedEntadModifier>();
                activeModifiers.RemoveAll(m => m == null || m.def == null);
            }
        }

        // The pawn currently wearing/wielding this item, if any
        public Pawn Holder
        {
            get
            {
                if (parent is Apparel apparel) return apparel.Wearer;
                return (parent.ParentHolder as Pawn_EquipmentTracker)?.pawn;
            }
        }

        // While equipped, keep the mood thought refreshed with a short lifetime so it ends soon after unequipping
        public override void CompTick()
        {
            if (activeModifiers.NullOrEmpty() || !parent.IsHashIntervalTick(150)) return;
            Pawn holder = Holder;
            if (holder == null) return;
            EntadAbilities.Grant(holder, this);
            foreach (var m in activeModifiers)
                if (m.def.thought != null) EntadMoods.Give(holder, m.def.thought, 600);
        }

        public override string TransformLabel(string label)
        {
            return activeModifiers.NullOrEmpty() ? label : "\u263C" + label + "\u263C";
        }

        public override string CompInspectStringExtra()
        {
            if (activeModifiers.NullOrEmpty()) return null;
            return "Entad Modifiers: " + string.Join(", ", activeModifiers.Select(m => m.def.LabelCap.ToString()));
        }

        // Single row in the Basics section; hover shows details like unique weapon traits
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (activeModifiers.NullOrEmpty()) yield break;

            var sb = new System.Text.StringBuilder("This item's entad modifiers.\n");
            foreach (var m in activeModifiers)
            {
                sb.Append("\n").AppendLine(m.def.LabelCap.Resolve().Colorize(ColoredText.TipSectionTitleColor));
                sb.AppendLine(m.def.description);
                for (int i = 0; m.def.statOffsets != null && i < m.def.statOffsets.Count; i++)
                {
                    var s = m.def.statOffsets[i].stat;
                    float v = m.OffsetFor(i);
                    sb.AppendLine($" - {s.LabelCap} {(v >= 0 ? "+" : "")}{v.ToStringByStyle(s.toStringStyle, ToStringNumberSense.Offset)}");
                }
                for (int i = 0; m.def.statFactors != null && i < m.def.statFactors.Count; i++)
                {
                    var s = m.def.statFactors[i].stat;
                    sb.AppendLine($" - {s.LabelCap} x{m.FactorFor(i).ToStringPercent()}");
                }
                if (m.def.thought != null)
                {
                    sb.AppendLine($" - Mood: {m.def.thought.stages?.FirstOrDefault()?.LabelCap ?? m.def.thought.defName} ({m.thoughtHours:0.#}h)");
                }
                if (!m.def.abilities.NullOrEmpty())
                    sb.AppendLine($" - Grants ability: {string.Join(", ", m.def.abilities.Select(a => a.LabelCap.ToString()))}");
                if (m.def.HasMealEffect)
                {
                    if (m.def.mealNutritionFactor.min != 1f || m.def.mealNutritionFactor.max != 1f)
                        sb.AppendLine($" - Meal nutrition x{m.mealNutritionFactor.ToStringPercent()}");
                    if (m.def.mealThought != null)
                        sb.AppendLine($" - Meals give: {m.def.mealThought.stages?.FirstOrDefault()?.LabelCap ?? m.def.mealThought.defName}");
                }
                sb.AppendLine($" - Market value +{m.MarketValueOffset().ToStringMoney()}");
            }

            string label = string.Join(", ", activeModifiers.Select(m => m.def.label));
            yield return new StatDrawEntry(StatCategoryDefOf.Basics, "Entad modifiers", label, sb.ToString().TrimEnd(), 4000);
        }
    }

    public class CompProperties_Entad : CompProperties
    {
        public CompProperties_Entad()
        {
            compClass = typeof(CompEntad);
        }
    }
}
