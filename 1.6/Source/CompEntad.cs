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
        public List<float> buildingValues = new List<float>();
        public ThoughtDef thought;
        public float thoughtHours;
        public float mealNutritionFactor = 1f;

        // Furniture abilities: game tick each ability is ready again, parallel to def.abilities
        public List<int> abilityReadyTicks = new List<int>();

        public void ExposeData()
        {
            Scribe_Defs.Look(ref def, "def");
            Scribe_Collections.Look(ref offsetValues, "offsetValues", LookMode.Value);
            Scribe_Collections.Look(ref factorValues, "factorValues", LookMode.Value);
            Scribe_Collections.Look(ref buildingValues, "buildingValues", LookMode.Value);
            Scribe_Defs.Look(ref thought, "thought");
            Scribe_Values.Look(ref thoughtHours, "thoughtHours");
            Scribe_Values.Look(ref mealNutritionFactor, "mealNutritionFactor", 1f);
            Scribe_Collections.Look(ref abilityReadyTicks, "abilityReadyTicks", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                abilityReadyTicks = abilityReadyTicks ?? new List<int>();
                offsetValues = offsetValues ?? new List<float>();
                factorValues = factorValues ?? new List<float>();
                buildingValues = buildingValues ?? new List<float>();
            }
        }

        public static AppliedEntadModifier Roll(EntadModifierDef def)
        {
            var applied = new AppliedEntadModifier { def = def };
            if (def.statOffsets != null) foreach (var r in def.statOffsets) applied.offsetValues.Add(r.Roll());
            if (def.statFactors != null) foreach (var r in def.statFactors) applied.factorValues.Add(r.Roll());
            if (def.buildingFactors != null) foreach (var r in def.buildingFactors) applied.buildingValues.Add(r.Roll());
            applied.thought = def.thought;
            if (def.HasMoodRange) applied.thought = def.MoodCandidates().RandomElementWithFallback();
            if (applied.thought != null) applied.thoughtHours = def.thoughtHours.RandomInRange;
            applied.mealNutritionFactor = def.mealNutritionFactor.RandomInRange;
            return applied;
        }

        public int AbilityReadyTick(int i) => i < abilityReadyTicks.Count ? abilityReadyTicks[i] : 0;

        public void SetAbilityReadyTick(int i, int tick)
        {
            while (abilityReadyTicks.Count <= i) abilityReadyTicks.Add(0);
            abilityReadyTicks[i] = tick;
        }

        public int ThoughtDurationTicks => UnityEngine.Mathf.Max(1, (int)(thoughtHours * GenDate.TicksPerHour));

        public float OffsetFor(int i) => i < offsetValues.Count ? offsetValues[i] : 0f;
        public float BuildingFactorFor(int i) => i < buildingValues.Count ? buildingValues[i] : 1f;
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
            if (def.buildingFactors != null)
                for (int i = 0; i < def.buildingFactors.Count; i++) { sum += def.buildingFactors[i].Normalize(BuildingFactorFor(i)); n++; }
            if (def.HasMoodRange && thought != null && def.thoughtMoodRange.max - def.thoughtMoodRange.min > 0.0001f)
            { sum += UnityEngine.Mathf.InverseLerp(def.thoughtMoodRange.min, def.thoughtMoodRange.max, EntadModifierDef.MoodEffectOf(thought)); n++; }
            if (thought != null) { sum += def.thoughtHours.max - def.thoughtHours.min > 0.0001f ? UnityEngine.Mathf.InverseLerp(def.thoughtHours.min, def.thoughtHours.max, thoughtHours) : 0.5f; n++; }
            var f = def.mealNutritionFactor;
            if (f.max - f.min > 0.0001f) { sum += UnityEngine.Mathf.InverseLerp(f.min, f.max, mealNutritionFactor); n++; }
            return n == 0 ? 0.5f : sum / n;
        }

        // Market value added by this modifier: scales with rarity and where the roll landed
        public float MarketValueOffset() => def.BaseMarketValue * (0.5f + RollQuality());
    }

    public partial class CompEntad : ThingComp
    {
        public List<AppliedEntadModifier> activeModifiers = new List<AppliedEntadModifier>();

        public CompProperties_Entad Props => (CompProperties_Entad)props;

        public bool HasModifier(EntadModifierDef def) => activeModifiers.Any(m => m.def == def);

        private float[] propertyFactors;

        // Product of this item's factors for a building property; cached since it's read from hot paths
        public float PropertyFactor(EntadBuildingProperty property)
        {
            if (propertyFactors == null)
            {
                propertyFactors = new float[System.Enum.GetValues(typeof(EntadBuildingProperty)).Length];
                for (int i = 0; i < propertyFactors.Length; i++) propertyFactors[i] = 1f;
                foreach (var m in activeModifiers)
                {
                    if (m.def.buildingFactors == null) continue;
                    for (int i = 0; i < m.def.buildingFactors.Count; i++)
                        propertyFactors[(int)m.def.buildingFactors[i].property] *= m.BuildingFactorFor(i);
                }
            }
            return propertyFactors[(int)property];
        }

        private void ModifiersChanged()
        {
            propertyFactors = null;
            if (parent.Spawned) parent.GetComp<CompGlower>()?.RefreshGlower();
        }

        public void AddModifier(EntadModifierDef def)
        {
            activeModifiers.Add(AppliedEntadModifier.Roll(def));
            ModifiersChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
        }

        public void RemoveModifier(AppliedEntadModifier modifier)
        {
            if (!activeModifiers.Remove(modifier)) return;
            ModifiersChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
            if (modifier.def.abilities != null && holder != null)
                foreach (var a in modifier.def.abilities)
                    if (!activeModifiers.Any(m => m.def.abilities != null && m.def.abilities.Contains(a)) && holder.abilities?.GetAbility(a) != null)
                        holder.abilities.RemoveAbility(a);
        }

        public void ClearModifiers()
        {
            foreach (var m in activeModifiers.ToList()) RemoveModifier(m);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref activeModifiers, "activeModifiers", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                activeModifiers = activeModifiers ?? new List<AppliedEntadModifier>();
                activeModifiers.RemoveAll(m => m == null || m.def == null);
                propertyFactors = null;
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
                if (m.thought != null)
                {
                    sb.AppendLine($" - Mood: {m.thought.stages?.FirstOrDefault()?.LabelCap ?? m.thought.defName} ({EntadModifierDef.MoodEffectOf(m.thought):+0.#;-0.#}) ({m.thoughtHours:0.#}h)");
                }
                for (int i = 0; m.def.buildingFactors != null && i < m.def.buildingFactors.Count; i++)
                    sb.AppendLine($" - {m.def.buildingFactors[i].Label} x{m.BuildingFactorFor(i).ToStringPercent()}");
                if (!m.def.abilities.NullOrEmpty())
                    sb.AppendLine($" - {(parent.def.building != null ? "Activatable ability" : "Grants ability")}: {string.Join(", ", m.def.abilities.Select(a => a.LabelCap.ToString()))}");
                if (m.def.HasMealEffect)
                {
                    if (m.def.mealNutritionFactor.min != 1f || m.def.mealNutritionFactor.max != 1f)
                        sb.AppendLine($" - Meal nutrition x{m.mealNutritionFactor.ToStringPercent()}");
                    if (m.def.mealQualityOffset != 0)
                        sb.AppendLine($" - Meal quality {(m.def.mealQualityOffset > 0 ? "+" : "")}{m.def.mealQualityOffset}");
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
