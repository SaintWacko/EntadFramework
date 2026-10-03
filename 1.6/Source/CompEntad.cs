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

        // Which kinds of this modifier's effects have been revealed. Each kind reveals on its own, e.g. using an
        // ability doesn't reveal a damage bonus. Hidden effects are shown as "???" and don't affect displayed stats.
        public EntadEffectKind revealedKinds = EntadEffectKind.All;
        public CompEntad owner;

        public bool IsRevealed(EntadEffectKind kind) => (def.EffectKinds & kind & ~revealedKinds) == EntadEffectKind.None;

        // No part of the modifier is known yet, so even its name is hidden
        public bool NameHidden => def.EffectKinds != EntadEffectKind.None && (def.EffectKinds & revealedKinds) == EntadEffectKind.None;

        public bool AnyHidden => (def.EffectKinds & ~revealedKinds) != EntadEffectKind.None;

        public void Reveal(EntadEffectKind kind)
        {
            if (IsRevealed(kind)) return;
            revealedKinds |= kind;
            owner?.RevealedChanged(this, kind);
        }

        public void Hide() { revealedKinds = EntadEffectKind.None; owner?.RevealedChanged(this, EntadEffectKind.All, false); }

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
            Scribe_Values.Look(ref revealedKinds, "revealedKinds", EntadEffectKind.All);
            bool legacyRevealed = true;
            Scribe_Values.Look(ref legacyRevealed, "revealed", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !legacyRevealed && revealedKinds == EntadEffectKind.All) revealedKinds = EntadEffectKind.None;
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
            var applied = new AppliedEntadModifier { def = def, revealedKinds = EntadSettings.HideModifiers ? EntadEffectKind.None : EntadEffectKind.All };
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
        private int spawnedTick;
        private int hiddenState; // 0 unknown, 1 some hidden, 2 none hidden

        public bool HasHidden
        {
            get
            {
                if (hiddenState == 0) hiddenState = activeModifiers.Any(m => m.AnyHidden) ? 1 : 2;
                return hiddenState == 1;
            }
        }

        public void RevealedChanged(AppliedEntadModifier m, EntadEffectKind kind, bool announce = true)
        {
            hiddenState = 0;
            propertyFactors = null;
            if ((kind & EntadEffectKind.Stat) != 0) ClearStatCaches();
            if ((kind & EntadEffectKind.Building) != 0 && parent.Spawned) parent.GetComp<CompGlower>()?.RefreshGlower();
            if (announce) Messages.Message($"Something about {parent.LabelNoCount} has revealed itself: {m.def.label}.", parent, MessageTypeDefOf.NeutralEvent, false);
        }

        private void ClearStatCaches()
        {
            Pawn holder = Holder;
            foreach (var m in activeModifiers)
                foreach (var r in m.def.AllRanges())
                {
                    if (r.stat == null) continue;
                    r.stat.Worker.ClearCacheForThing(parent);
                    if (holder != null) r.stat.Worker.ClearCacheForThing(holder);
                }
            StatDefOf.MarketValue.Worker.ClearCacheForThing(parent);
        }

        // Reveals the given kind of effect on every modifier that has it and for which the predicate holds
        public void RevealWhere(EntadEffectKind kind, System.Func<AppliedEntadModifier, bool> predicate = null)
        {
            if (!HasHidden) return;
            foreach (var m in activeModifiers.ToList())
                if (!m.IsRevealed(kind) && (predicate == null || predicate(m))) m.Reveal(kind);
        }

        // Building properties are read while the building is in play, so only count it once it has been
        // around a while (not when it spawns or a save loads)
        public void RevealProperty(EntadBuildingProperty property)
        {
            if (Find.TickManager.TicksGame - spawnedTick < 2500) return;
            RevealWhere(EntadEffectKind.Building, m => m.def.buildingFactors != null && m.def.buildingFactors.Any(b => b.property == property));
        }

        public bool FuelHidden => activeModifiers.Any(m => !m.def.fuelTypes.NullOrEmpty() && !m.IsRevealed(EntadEffectKind.Fuel));

        // Product of this item's factors for a building property; cached since it's read from hot paths
        public float PropertyFactor(EntadBuildingProperty property)
        {
            if (propertyFactors == null)
            {
                propertyFactors = new float[System.Enum.GetValues(typeof(EntadBuildingProperty)).Length];
                for (int i = 0; i < propertyFactors.Length; i++) propertyFactors[i] = 1f;
                foreach (var m in activeModifiers)
                {
                    if (m.def.buildingFactors == null || !m.IsRevealed(EntadEffectKind.Building)) continue;
                    for (int i = 0; i < m.def.buildingFactors.Count; i++)
                        propertyFactors[(int)m.def.buildingFactors[i].property] *= m.BuildingFactorFor(i);
                }
            }
            return propertyFactors[(int)property];
        }

        private void ModifiersChanged()
        {
            propertyFactors = null;
            hiddenState = 0;
            if (!parent.Spawned) return;
            parent.GetComp<CompGlower>()?.RefreshGlower();
            EntadFuel.Refresh(parent, this);
            parent.GetComp<CompPowerTrader>()?.SetUpPowerVars();
        }

        public void AddModifier(EntadModifierDef def)
        {
            var applied = AppliedEntadModifier.Roll(def);
            applied.owner = this;
            activeModifiers.Add(applied);
            ModifiersChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            spawnedTick = Find.TickManager.TicksGame;
            if (!activeModifiers.NullOrEmpty()) EntadFuel.Refresh(parent, this);
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
                hiddenState = 0;
                foreach (var m in activeModifiers) m.owner = this;
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
            return "Entad Modifiers: " + string.Join(", ", activeModifiers.Select(m => m.NameHidden ? "???" : m.def.LabelCap.ToString()));
        }

        // Single row in the Basics section; hover shows details like unique weapon traits
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (activeModifiers.NullOrEmpty()) yield break;

            var sb = new System.Text.StringBuilder("This item's entad modifiers.\n");
            foreach (var m in activeModifiers)
            {
                if (m.NameHidden)
                {
                    sb.Append("\n").AppendLine("???".Colorize(ColoredText.TipSectionTitleColor));
                    sb.AppendLine($" - Market value +{m.MarketValueOffset().ToStringMoney()}");
                    continue;
                }
                sb.Append("\n").AppendLine(m.def.LabelCap.Resolve().Colorize(ColoredText.TipSectionTitleColor));
                if (!m.AnyHidden) sb.AppendLine(m.def.description);
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statOffsets != null && i < m.def.statOffsets.Count; i++)
                {
                    var s = m.def.statOffsets[i].stat;
                    float v = m.OffsetFor(i);
                    sb.AppendLine($" - {s.LabelCap} {(v >= 0 ? "+" : "")}{v.ToStringByStyle(s.toStringStyle, ToStringNumberSense.Offset)}");
                }
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statFactors != null && i < m.def.statFactors.Count; i++)
                {
                    var s = m.def.statFactors[i].stat;
                    sb.AppendLine($" - {s.LabelCap} x{m.FactorFor(i).ToStringPercent()}");
                }
                if (m.thought != null && m.IsRevealed(EntadEffectKind.Mood))
                {
                    sb.AppendLine($" - Mood: {m.thought.stages?.FirstOrDefault()?.LabelCap ?? m.thought.defName} ({EntadModifierDef.MoodEffectOf(m.thought):+0.#;-0.#}) ({m.thoughtHours:0.#}h)");
                }
                if (!m.def.fuelTypes.NullOrEmpty() && m.IsRevealed(EntadEffectKind.Fuel))
                    sb.AppendLine($" - {(m.def.replaceFuel ? "Burns only" : "Also burns")}: {string.Join(", ", m.def.fuelTypes.Select(f => f.LabelCap.ToString()))}");
                for (int i = 0; m.IsRevealed(EntadEffectKind.Building) && m.def.buildingFactors != null && i < m.def.buildingFactors.Count; i++)
                    sb.AppendLine($" - {m.def.buildingFactors[i].Label} x{m.BuildingFactorFor(i).ToStringPercent()}");
                if (!m.def.abilities.NullOrEmpty() && m.IsRevealed(EntadEffectKind.Ability))
                    sb.AppendLine($" - {(parent.def.building != null ? "Activatable ability" : "Grants ability")}: {string.Join(", ", m.def.abilities.Select(a => a.LabelCap.ToString()))}");
                if (m.def.HasMealEffect && m.IsRevealed(EntadEffectKind.Meal))
                {
                    if (m.def.mealNutritionFactor.min != 1f || m.def.mealNutritionFactor.max != 1f)
                        sb.AppendLine($" - Meal nutrition x{m.mealNutritionFactor.ToStringPercent()}");
                    if (m.def.mealQualityOffset != 0)
                        sb.AppendLine($" - Meal quality {(m.def.mealQualityOffset > 0 ? "+" : "")}{m.def.mealQualityOffset}");
                    if (m.def.mealThought != null)
                        sb.AppendLine($" - Meals give: {m.def.mealThought.stages?.FirstOrDefault()?.LabelCap ?? m.def.mealThought.defName}");
                }
                if (m.AnyHidden) sb.AppendLine(" - ???");
                sb.AppendLine($" - Market value +{m.MarketValueOffset().ToStringMoney()}");
            }

            string label = string.Join(", ", activeModifiers.Select(m => m.NameHidden ? "???" : m.def.label));
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
