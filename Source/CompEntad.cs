using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // A trait applied to a specific item, with the values rolled for it
    public class AppliedEntadTrait : IExposable
    {
        public EntadTraitDef def;
        public List<float> offsetValues = new List<float>();
        public List<float> factorValues = new List<float>();
        public List<float> buildingValues = new List<float>();
        public List<float> extraDamageValues = new List<float>();
        public ThoughtDef thought;
        public float thoughtHours;
        public float mealNutritionFactor = 1f;

        // Which kinds of this trait's effects have been revealed. Each kind reveals on its own, e.g. using an
        // ability doesn't reveal a damage bonus. Hidden effects are shown as "???" and don't affect displayed stats.
        public EntadEffectKind revealedKinds = EntadEffectKind.All;
        public CompEntad owner;

        // Rarity this trait was rolled at (the def's own rarity unless it scales with rarity). Saved as an int so
        // saves from before rarity scaling load with the def's rarity.
        private int rarityValue = -1;
        public EntadRarity Rarity => rarityValue < 0 ? def.rarity : (EntadRarity)rarityValue;

        public bool IsRevealed(EntadEffectKind kind) => (def.EffectKinds & kind & ~revealedKinds) == EntadEffectKind.None;

        // No part of the trait is known yet, so even its name is hidden
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
            Scribe_Values.Look(ref rarityValue, "rarity", -1);
            Scribe_Collections.Look(ref offsetValues, "offsetValues", LookMode.Value);
            Scribe_Collections.Look(ref factorValues, "factorValues", LookMode.Value);
            Scribe_Collections.Look(ref buildingValues, "buildingValues", LookMode.Value);
            Scribe_Collections.Look(ref extraDamageValues, "extraDamageValues", LookMode.Value);
            Scribe_Defs.Look(ref thought, "thought");
            Scribe_Values.Look(ref thoughtHours, "thoughtHours");
            Scribe_Values.Look(ref mealNutritionFactor, "mealNutritionFactor", 1f);
            Scribe_Values.Look(ref revealedKinds, "revealedKinds", EntadEffectKind.All);
            bool legacyRevealed = true;
            Scribe_Values.Look(ref legacyRevealed, "revealed", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !legacyRevealed && revealedKinds == EntadEffectKind.All) revealedKinds = EntadEffectKind.None;
            Scribe_Collections.Look(ref abilityReadyTicks, "abilityReadyTicks", LookMode.Value);
            Scribe_Collections.Look(ref abilityCharges, "abilityCharges", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                abilityReadyTicks = abilityReadyTicks ?? new List<int>();
                abilityCharges = abilityCharges ?? new List<int>();
                offsetValues = offsetValues ?? new List<float>();
                factorValues = factorValues ?? new List<float>();
                buildingValues = buildingValues ?? new List<float>();
                extraDamageValues = extraDamageValues ?? new List<float>();
            }
        }

        public static AppliedEntadTrait Roll(EntadTraitDef def, EntadRarity? rarity = null)
        {
            EntadRarity at = def.ClampRarity(rarity ?? def.rarity);
            float scale = def.RarityScale(at);
            var applied = new AppliedEntadTrait { def = def, rarityValue = (int)at, revealedKinds = EntadSettings.HideTraits ? EntadEffectKind.None : EntadEffectKind.All };
            if (def.statOffsets != null) foreach (var r in def.statOffsets) applied.offsetValues.Add(r.RollScaled(scale, false));
            if (def.statFactors != null) foreach (var r in def.statFactors) applied.factorValues.Add(r.RollScaled(scale, true));
            if (def.extraDamage != null) foreach (var r in def.extraDamage) applied.extraDamageValues.Add(r.RollScaled(scale));
            if (def.buildingFactors != null) foreach (var r in def.buildingFactors) applied.buildingValues.Add(r.RollScaled(scale));
            applied.thought = def.thought;
            if (def.HasMoodRange) applied.thought = def.MoodCandidates().RandomElementWithFallback();
            if (applied.thought != null) applied.thoughtHours = def.thoughtHours.RandomInRange;
            applied.mealNutritionFactor = def.mealNutritionFactor.RandomInRange;
            return applied;
        }

        // Furniture abilities with charges: charges left, and the tick the next one comes back
        public List<int> abilityCharges = new List<int>();

        // Returns the charges currently available for ability i, adding any that have come back by now
        public int ChargesLeft(int i, int max, int cooldown)
        {
            while (abilityCharges.Count <= i) abilityCharges.Add(max);
            int c = abilityCharges[i];
            int now = Find.TickManager.TicksGame;
            while (c < max && now >= AbilityReadyTick(i))
            {
                c++;
                SetAbilityReadyTick(i, c < max ? AbilityReadyTick(i) + cooldown : 0);
            }
            abilityCharges[i] = c;
            return c;
        }

        public void UseCharge(int i, int max, int cooldown)
        {
            int c = ChargesLeft(i, max, cooldown);
            if (c >= max) SetAbilityReadyTick(i, Find.TickManager.TicksGame + cooldown);
            abilityCharges[i] = c - 1;
        }

        public int AbilityReadyTick(int i) => i < abilityReadyTicks.Count ? abilityReadyTicks[i] : 0;

        public void SetAbilityReadyTick(int i, int tick)
        {
            while (abilityReadyTicks.Count <= i) abilityReadyTicks.Add(0);
            abilityReadyTicks[i] = tick;
        }

        public int ThoughtDurationTicks => UnityEngine.Mathf.Max(1, (int)(thoughtHours * GenDate.TicksPerHour));

        public float OffsetFor(int i) => i < offsetValues.Count ? offsetValues[i] : 0f;
        public float ExtraDamageFor(int i) => i < extraDamageValues.Count ? extraDamageValues[i] : 0f;
        public float BuildingFactorFor(int i) => i < buildingValues.Count ? buildingValues[i] : 1f;
        public float FactorFor(int i) => i < factorValues.Count ? factorValues[i] : 1f;

        // Average position (0..1) of the rolled values within their ranges
        public float RollQuality()
        {
            float sum = 0f;
            int n = 0;
            float scale = def.RarityScale(Rarity);
            if (def.statOffsets != null)
                for (int i = 0; i < def.statOffsets.Count; i++) { sum += def.statOffsets[i].NormalizeScaled(OffsetFor(i), scale, false); n++; }
            if (def.statFactors != null)
                for (int i = 0; i < def.statFactors.Count; i++) { sum += def.statFactors[i].NormalizeScaled(FactorFor(i), scale, true); n++; }
            if (def.extraDamage != null)
                for (int i = 0; i < def.extraDamage.Count; i++) { sum += def.extraDamage[i].NormalizeScaled(ExtraDamageFor(i), scale); n++; }
            if (def.buildingFactors != null)
                for (int i = 0; i < def.buildingFactors.Count; i++) { sum += def.buildingFactors[i].NormalizeScaled(BuildingFactorFor(i), scale); n++; }
            if (def.HasMoodRange && thought != null && def.thoughtMoodRange.max - def.thoughtMoodRange.min > 0.0001f)
            { sum += UnityEngine.Mathf.InverseLerp(def.thoughtMoodRange.min, def.thoughtMoodRange.max, EntadTraitDef.MoodEffectOf(thought)); n++; }
            if (thought != null) { sum += def.thoughtHours.max - def.thoughtHours.min > 0.0001f ? UnityEngine.Mathf.InverseLerp(def.thoughtHours.min, def.thoughtHours.max, thoughtHours) : 0.5f; n++; }
            var f = def.mealNutritionFactor;
            if (f.max - f.min > 0.0001f) { sum += UnityEngine.Mathf.InverseLerp(f.min, f.max, mealNutritionFactor); n++; }
            return n == 0 ? 0.5f : sum / n;
        }

        // Market value added by this trait: scales with rarity and where the roll landed
        public float MarketValueOffset() => EntadTraitDef.BaseMarketValueAt(Rarity) * (0.5f + RollQuality());
    }

    public partial class CompEntad : ThingComp, IRenameable
    {
        // Player-chosen name; the surrounding entad markers are kept
        public string customName;

        public List<AppliedEntadTrait> activeTraits = new List<AppliedEntadTrait>();

        public CompProperties_Entad Props => (CompProperties_Entad)props;

        public bool HasTrait(EntadTraitDef def) => activeTraits.Any(m => m.def == def);

        private float[] propertyFactors;

        // Total offset this item gives its holder per pawn stat, and how much of that total comes from traits whose
        // stat effect is still unrevealed. The hidden part is kept as a sum (not a flag) so the stat explanation can
        // show the vanilla + revealed part of the line and leave only the hidden part out.
        // Built lazily and dropped whenever the traits change, so the stat-offset hook is a single lookup.
        private Dictionary<StatDef, KeyValuePair<float, float>> wearerOffsets;

        public float WearerOffset(StatDef stat, out float hiddenPart)
        {
            // Nearly all gear carries this comp but most has no traits; this runs for every worn item on every
            // affected stat evaluation, so bail before allocating the (empty) cache dictionary.
            if (activeTraits.NullOrEmpty()) { hiddenPart = 0f; return 0f; }
            if (wearerOffsets == null)
            {
                // Filled in a local and published at the end, so a throw mid-build can't leave a half-built cache
                var built = new Dictionary<StatDef, KeyValuePair<float, float>>();
                foreach (var m in activeTraits)
                {
                    var offsets = m.def.statOffsets;
                    if (offsets == null) continue;
                    for (int i = 0; i < offsets.Count; i++)
                    {
                        StatDef s = offsets[i].stat;
                        if (s == null || !EntadTraitDef.IsWearerStat(s)) continue;
                        built.TryGetValue(s, out var cur);
                        float v = m.OffsetFor(i);
                        built[s] = new KeyValuePair<float, float>(cur.Key + v, cur.Value + (m.IsRevealed(EntadEffectKind.Stat) ? 0f : v));
                    }
                }
                wearerOffsets = built;
            }
            if (wearerOffsets.Count > 0 && wearerOffsets.TryGetValue(stat, out var e)) { hiddenPart = e.Value; return e.Key; }
            hiddenPart = 0f;
            return 0f;
        }
        private int spawnedTick;
        private int hiddenState; // 0 unknown, 1 some hidden, 2 none hidden

        public bool HasHidden
        {
            get
            {
                if (hiddenState == 0) hiddenState = activeTraits.Any(m => m.AnyHidden) ? 1 : 2;
                return hiddenState == 1;
            }
        }

        public void RevealedChanged(AppliedEntadTrait m, EntadEffectKind kind, bool announce = true)
        {
            hiddenState = 0;
            propertyFactors = null;
            wearerOffsets = null;
            ClearStatCaches();
            if ((kind & EntadEffectKind.Building) != 0 && parent.Spawned) parent.GetComp<CompGlower>()?.RefreshGlower();
            if (announce) Messages.Message($"Something about {parent.LabelNoCount} has revealed itself: {m.def.label}.", parent, MessageTypeDefOf.NeutralEvent, false);
        }

        private void ClearStatCaches()
        {
            Pawn holder = Holder;
            foreach (var m in activeTraits)
                foreach (var r in m.def.AllRanges())
                {
                    if (r.stat == null) continue;
                    r.stat.Worker.ClearCacheForThing(parent);
                    if (holder != null) r.stat.Worker.ClearCacheForThing(holder);
                }
            StatDefOf.MarketValue.Worker.ClearCacheForThing(parent);
        }

        // Reveals the given kind of effect on every trait that has it and for which the predicate holds
        public void RevealWhere(EntadEffectKind kind, System.Func<AppliedEntadTrait, bool> predicate = null)
        {
            if (!HasHidden) return;
            foreach (var m in activeTraits.ToList())
                if (!m.IsRevealed(kind) && (predicate == null || predicate(m))) m.Reveal(kind);
        }

        // Building properties are read while the building is in play, so only count it once it has been
        // around a while (not when it spawns or a save loads)
        public void RevealProperty(EntadBuildingProperty property)
        {
            if (Find.TickManager.TicksGame - spawnedTick < 2500) return;
            RevealWhere(EntadEffectKind.Building, m => m.def.buildingFactors != null && m.def.buildingFactors.Any(b => b.property == property));
        }

        public bool FuelHidden => activeTraits.Any(m => !m.def.AllFuelTypes.NullOrEmpty() && !m.IsRevealed(EntadEffectKind.Fuel));

        // Product of this item's factors for a building property; cached since it's read from hot paths
        public float PropertyFactor(EntadBuildingProperty property)
        {
            if (propertyFactors == null)
            {
                propertyFactors = new float[System.Enum.GetValues(typeof(EntadBuildingProperty)).Length];
                for (int i = 0; i < propertyFactors.Length; i++) propertyFactors[i] = 1f;
                foreach (var m in activeTraits)
                {
                    if (m.def.buildingFactors == null) continue;
                    for (int i = 0; i < m.def.buildingFactors.Count; i++)
                        propertyFactors[(int)m.def.buildingFactors[i].property] *= m.BuildingFactorFor(i);
                }
            }
            return propertyFactors[(int)property];
        }

        private void TraitsChanged()
        {
            propertyFactors = null;
            wearerOffsets = null;
            hiddenState = 0;
            // Trait changes are rare (generation, dev tools), so clearing every affected stat here is cheap and keeps
            // the game's stat caches for the item and its holder from serving a pre-change value.
            ClearStatCaches();
            if (!parent.Spawned) return;
            parent.GetComp<CompGlower>()?.RefreshGlower();
            EntadFuel.Refresh(parent, this);
            parent.GetComp<CompPowerTrader>()?.SetUpPowerVars();
        }

        private static bool AffectsMaxHitPoints(EntadTraitDef def)
        {
            foreach (var r in def.AllRanges()) if (r.stat == StatDefOf.MaxHitPoints) return true;
            return false;
        }

        // Keeps the item's hit point fraction when its maximum changes, so a sturdier item starts at full health
        private void RescaleHitPoints(float fraction)
        {
            ClearStatCaches();
            parent.HitPoints = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(fraction * parent.MaxHitPoints), 1, parent.MaxHitPoints);
        }

        // Traits the player switched off in the settings are refused here too, so other mods can't add them either.
        // Only developer tools pass ignoreDisabled.
        public bool AddTrait(EntadTraitDef def, EntadRarity? rarity = null, bool ignoreDisabled = false)
        {
            if (!ignoreDisabled && EntadSettings.IsDisabled(def)) return false;
            bool scaleHp = parent.def.useHitPoints && AffectsMaxHitPoints(def);
            float hpFraction = scaleHp ? (float)parent.HitPoints / parent.MaxHitPoints : 1f;
            var applied = AppliedEntadTrait.Roll(def, rarity);
            applied.owner = this;
            activeTraits.Add(applied);
            if (scaleHp) RescaleHitPoints(hpFraction);
            TraitsChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
            return true;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            spawnedTick = Find.TickManager.TicksGame;
            if (!activeTraits.NullOrEmpty()) EntadFuel.Refresh(parent, this);
        }

        public void RemoveTrait(AppliedEntadTrait trait)
        {
            bool scaleHp = parent.def.useHitPoints && AffectsMaxHitPoints(trait.def);
            float hpFraction = scaleHp ? (float)parent.HitPoints / parent.MaxHitPoints : 1f;
            if (!activeTraits.Contains(trait)) return;
            // Clear while the trait is still listed: ClearStatCaches walks activeTraits, so after removal the
            // removed trait's own stats would never be cleared.
            ClearStatCaches();
            activeTraits.Remove(trait);
            if (scaleHp) RescaleHitPoints(hpFraction);
            TraitsChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
            if (trait.def.abilities != null && holder != null)
                foreach (var a in trait.def.abilities)
                    if (!activeTraits.Any(m => m.def.abilities != null && m.def.abilities.Contains(a)) && holder.abilities?.GetAbility(a) != null)
                        holder.abilities.RemoveAbility(a);
        }

        public void ClearTraits()
        {
            foreach (var m in activeTraits.ToList()) RemoveTrait(m);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref activeTraits, "activeTraits", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                activeTraits = activeTraits ?? new List<AppliedEntadTrait>();
                activeTraits.RemoveAll(m => m == null || m.def == null);
                propertyFactors = null;
                wearerOffsets = null;
                hiddenState = 0;
                foreach (var m in activeTraits) m.owner = this;
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
            if (activeTraits.NullOrEmpty()) return label;
            return "\u263C" + (customName.NullOrEmpty() ? label : customName) + "\u263C";
        }

        public string RenamableLabel
        {
            get => customName;
            set => customName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public string BaseLabel => parent.def.LabelCap;
        public string InspectLabel => parent.LabelCap;

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (activeTraits.NullOrEmpty()) yield break;
            yield return new Command_Action
            {
                defaultLabel = "Rename",
                defaultDesc = "Give this entad a name.",
                icon = TexButton.Rename,
                action = () => Find.WindowStack.Add(new Dialog_RenameEntad(this))
            };
        }

        public override string CompInspectStringExtra()
        {
            if (activeTraits.NullOrEmpty()) return null;
            return "Entad Traits: " + string.Join(", ", activeTraits.Select(m => m.NameHidden ? "???" : m.def.LabelCap.ToString()));
        }

        // Single row in the Basics section; hover shows details like unique weapon traits
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (activeTraits.NullOrEmpty()) yield break;

            var sb = new System.Text.StringBuilder("This item's entad traits.\n");
            foreach (var m in activeTraits)
            {
                if (m.NameHidden)
                {
                    sb.Append("\n").AppendLine("???".Colorize(ColoredText.TipSectionTitleColor));
                    continue;
                }
                sb.Append("\n").AppendLine($"{m.def.LabelCap.Resolve()} ({m.Rarity})".Colorize(ColoredText.TipSectionTitleColor));
                if (!m.AnyHidden) sb.AppendLine(m.def.description);
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statOffsets != null && i < m.def.statOffsets.Count; i++)
                {
                    var s = m.def.statOffsets[i].stat;
                    if (s == null) continue;
                    float v = m.OffsetFor(i);
                    sb.AppendLine($" - {s.LabelCap} {v.ToStringByStyle(s.toStringStyle, ToStringNumberSense.Offset)}");
                }
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statFactors != null && i < m.def.statFactors.Count; i++)
                {
                    var s = m.def.statFactors[i].stat;
                    if (s == null) continue;
                    sb.AppendLine($" - {s.LabelCap} x{m.FactorFor(i).ToStringPercent()}");
                }
                if (m.thought != null && m.IsRevealed(EntadEffectKind.Mood))
                {
                    sb.AppendLine($" - Mood: {m.thought.stages?.FirstOrDefault()?.LabelCap ?? m.thought.defName} ({EntadTraitDef.MoodEffectOf(m.thought):+0.#;-0.#}) ({m.thoughtHours:0.#}h)");
                }
                if (m.def.HasDamageEffect && m.IsRevealed(EntadEffectKind.Damage))
                {
                    if (m.def.changeDamageType != null) sb.AppendLine($" - Damage type: {m.def.changeDamageType.LabelCap}");
                    for (int i = 0; m.def.extraDamage != null && i < m.def.extraDamage.Count; i++)
                        sb.AppendLine($" - Extra {m.def.extraDamage[i].damageType.label} damage +{m.ExtraDamageFor(i):0.#}");
                }
                if (!m.def.AllFuelTypes.NullOrEmpty() && m.IsRevealed(EntadEffectKind.Fuel))
                    sb.AppendLine($" - {(m.def.replaceFuel ? "Burns only" : "Also burns")}: {string.Join(", ", m.def.AllFuelTypes.Select(f => f.LabelCap.ToString()))}");
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
                if (!m.AnyHidden) sb.AppendLine($" - Market value +{m.MarketValueOffset().ToStringMoney()}");
            }
            if (activeTraits.Any(m => m.AnyHidden))
                sb.Append("\nUnidentified properties: market value +").AppendLine(EntadTraitDef.UnidentifiedMarketValue.ToStringMoney());

            foreach (var e in DamageDisplayStats()) yield return e;

            string label = string.Join(", ", activeTraits.Select(m => m.NameHidden ? "???" : m.def.label));
            yield return new StatDrawEntry(StatCategoryDefOf.Basics, "Entad traits", label, sb.ToString().TrimEnd(), 4000);
        }
    }

    public class Dialog_RenameEntad : Dialog_Rename<CompEntad>
    {
        public Dialog_RenameEntad(CompEntad comp) : base(comp) { }
    }

    public partial class CompEntad
    {
        // Revealed damage effects get their own rows in the weapon section of the info card
        private IEnumerable<StatDrawEntry> DamageDisplayStats()
        {
            if (!parent.def.IsWeapon) yield break;
            var cat = parent.def.IsRangedWeapon ? StatCategoryDefOf.Weapon_Ranged : StatCategoryDefOf.Weapon_Melee;
            int order = 5500;
            float total = 0f;
            foreach (var m in activeTraits)
            {
                if (!m.def.HasDamageEffect || !m.IsRevealed(EntadEffectKind.Damage)) continue;
                if (m.def.changeDamageType != null)
                    yield return new StatDrawEntry(cat, "Entad damage type", m.def.changeDamageType.LabelCap,
                        $"{m.def.LabelCap}: this weapon's attacks deal {m.def.changeDamageType.label} damage instead of their usual type.", order++);
                for (int i = 0; m.def.extraDamage != null && i < m.def.extraDamage.Count; i++)
                {
                    float v = m.ExtraDamageFor(i);
                    total += v;
                    var d = m.def.extraDamage[i].damageType;
                    yield return new StatDrawEntry(cat, $"Entad {d.label} damage", "+" + v.ToString("0.#"),
                        $"{m.def.LabelCap}: each hit deals this much additional {d.label} damage, on top of the weapon's normal damage. It is not reduced by or added to the Damage stat above.", order++);
                }
            }
            if (total > 0f)
                yield return new StatDrawEntry(cat, "Entad extra damage per hit", "+" + total.ToString("0.#"),
                    "Total additional damage, of other damage types, dealt with every hit by this weapon's entad traits.", order);
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
