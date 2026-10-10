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
        public List<float> weaponValues = new List<float>();
        // EntadTrigger.ValueCount rolls per trigger (heal, rest, food, joy, hediff hours), and when each is ready again
        public List<float> triggerValues = new List<float>();
        public List<int> triggerReadyTicks = new List<int>();
        // Portable space: rolled width and height (EntadPortableSpace.cs), and what the space holds
        public List<float> spaceValues = new List<float>();
        public PortableSpace space;
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

        public void Reveal(EntadEffectKind kind, bool announce = true)
        {
            if (IsRevealed(kind)) return;
            revealedKinds |= kind;
            owner?.RevealedChanged(this, kind, announce);
        }

        public void Hide() { revealedKinds = EntadEffectKind.None; owner?.RevealedChanged(this, EntadEffectKind.All, false); }

        // Furniture abilities: game tick each ability is ready again, parallel to def.abilities
        public List<int> abilityReadyTicks = new List<int>();

        public void ExposeData()
        {
            // The name as written in the save, before BackCompatibility maps a removed trait onto its replacement
            // (Patch_BackCompatibleDefName). Read only while loading: on save it would write a second "def" node.
            string savedName = null;
            if (Scribe.mode == LoadSaveMode.LoadingVars) Scribe_Values.Look(ref savedName, "def");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref rarityValue, "rarity", -1);
            Scribe_Collections.Look(ref offsetValues, "offsetValues", LookMode.Value);
            Scribe_Collections.Look(ref factorValues, "factorValues", LookMode.Value);
            Scribe_Collections.Look(ref buildingValues, "buildingValues", LookMode.Value);
            Scribe_Collections.Look(ref extraDamageValues, "extraDamageValues", LookMode.Value);
            Scribe_Collections.Look(ref weaponValues, "weaponValues", LookMode.Value);
            Scribe_Collections.Look(ref triggerValues, "triggerValues", LookMode.Value);
            Scribe_Collections.Look(ref triggerReadyTicks, "triggerReadyTicks", LookMode.Value);
            Scribe_Collections.Look(ref spaceValues, "spaceValues", LookMode.Value);
            Scribe_Deep.Look(ref space, "space");
            Scribe_Defs.Look(ref thought, "thought");
            Scribe_Values.Look(ref thoughtHours, "thoughtHours");
            Scribe_Values.Look(ref mealNutritionFactor, "mealNutritionFactor", 1f);
            Scribe_Values.Look(ref revealedKinds, "revealedKinds", EntadEffectKind.All);
            bool legacyRevealed = true;
            Scribe_Values.Look(ref legacyRevealed, "revealed", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !legacyRevealed && revealedKinds == EntadEffectKind.All) revealedKinds = EntadEffectKind.None;
            // Kill memories revealed as Mood before they became Kill triggers (build 65): keep them known
            if (Scribe.mode == LoadSaveMode.LoadingVars && def?.killThought != null && (revealedKinds & EntadEffectKind.Mood) != 0)
                revealedKinds |= EntadEffectKind.Trigger;
            Scribe_Collections.Look(ref abilityReadyTicks, "abilityReadyTicks", LookMode.Value);
            Scribe_Collections.Look(ref abilityCharges, "abilityCharges", LookMode.Value);
            // A trait saved under a removed name: its stored rolls came from the old trait's range, which can be far
            // outside the replacement's (Marksman's Eye rolled shooting accuracy 0.03-0.1, Steady Hands rolls 1-4).
            // Re-roll the values at the saved rarity so the item behaves like any other copy of the replacement.
            // Also when the def has gained or lost a range since the save (Frostbite moved from a damage type to
            // extra damage, so old copies had no roll for it and dealt +0): the stored lists no longer line up.
            if (Scribe.mode == LoadSaveMode.LoadingVars && def != null)
            {
                if (savedName != null && savedName != def.defName) RerollValues();
                // Same trait, retuned: re-roll only the lists that changed shape, keeping the mood, meal rolls,
                // cooldowns and stored charges the player already has
                else if (!ListsMatchDef()) RerollMismatchedLists();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                abilityReadyTicks = abilityReadyTicks ?? new List<int>();
                abilityCharges = abilityCharges ?? new List<int>();
                offsetValues = offsetValues ?? new List<float>();
                factorValues = factorValues ?? new List<float>();
                buildingValues = buildingValues ?? new List<float>();
                extraDamageValues = extraDamageValues ?? new List<float>();
                weaponValues = weaponValues ?? new List<float>();
                triggerValues = triggerValues ?? new List<float>();
                triggerReadyTicks = triggerReadyTicks ?? new List<int>();
                spaceValues = spaceValues ?? new List<float>();
            }
        }

        private bool ListsMatchDef()
        {
            return (offsetValues?.Count ?? 0) == (def.statOffsets?.Count ?? 0)
                && (factorValues?.Count ?? 0) == (def.statFactors?.Count ?? 0)
                && (extraDamageValues?.Count ?? 0) == (def.extraDamage?.Count ?? 0)
                && (buildingValues?.Count ?? 0) == (def.buildingFactors?.Count ?? 0)
                && (weaponValues?.Count ?? 0) == (def.weaponProperties?.Count ?? 0)
                && (triggerValues?.Count ?? 0) == TriggerValueCount(def)
                && (spaceValues?.Count ?? 0) == SpaceValueCount(def);
        }

        private static int SpaceValueCount(EntadTraitDef def) => def.HasPortableSpace ? 2 : 0;

        private static int TriggerValueCount(EntadTraitDef def) => (def.triggers?.Count ?? 0) * EntadTrigger.ValueCount;

        private void RerollMismatchedLists()
        {
            var fresh = Roll(def, Rarity);
            if ((offsetValues?.Count ?? 0) != (def.statOffsets?.Count ?? 0)) offsetValues = fresh.offsetValues;
            if ((factorValues?.Count ?? 0) != (def.statFactors?.Count ?? 0)) factorValues = fresh.factorValues;
            if ((extraDamageValues?.Count ?? 0) != (def.extraDamage?.Count ?? 0)) extraDamageValues = fresh.extraDamageValues;
            if ((buildingValues?.Count ?? 0) != (def.buildingFactors?.Count ?? 0)) buildingValues = fresh.buildingValues;
            if ((weaponValues?.Count ?? 0) != (def.weaponProperties?.Count ?? 0)) weaponValues = fresh.weaponValues;
            if ((triggerValues?.Count ?? 0) != TriggerValueCount(def)) { triggerValues = fresh.triggerValues; triggerReadyTicks?.Clear(); }
            // An empty space can take a new size; one holding a room keeps the size the room was captured at
            if ((spaceValues?.Count ?? 0) != SpaceValueCount(def) && (space == null || space.IsEmpty)) spaceValues = fresh.spaceValues;
        }

        private void RerollValues()
        {
            var fresh = Roll(def, Rarity);
            rarityValue = fresh.rarityValue;
            offsetValues = fresh.offsetValues;
            factorValues = fresh.factorValues;
            extraDamageValues = fresh.extraDamageValues;
            buildingValues = fresh.buildingValues;
            weaponValues = fresh.weaponValues;
            triggerValues = fresh.triggerValues;
            triggerReadyTicks?.Clear();
            if (space == null || space.IsEmpty) spaceValues = fresh.spaceValues;
            thought = fresh.thought;
            thoughtHours = fresh.thoughtHours;
            mealNutritionFactor = fresh.mealNutritionFactor;
            // Parallel to the old trait's abilities; they rebuild against the new one's on first use
            // (null when the save had none: this runs in LoadingVars, before PostLoadInit fills in empty lists)
            abilityReadyTicks?.Clear();
            abilityCharges?.Clear();
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
            if (def.weaponProperties != null) foreach (var r in def.weaponProperties) applied.weaponValues.Add(r.RollScaled(scale));
            if (def.triggers != null)
                foreach (var t in def.triggers)
                    for (int k = 0; k < EntadTrigger.ValueCount; k++) applied.triggerValues.Add(t.RangeAt(k).RandomInRange * scale);
            if (def.HasPortableSpace)
                for (int k = 0; k < 2; k++) applied.spaceValues.Add(UnityEngine.Mathf.Max(1, UnityEngine.Mathf.RoundToInt(def.portableSpace.RandomInRange * scale)));
            applied.thought = def.thought;
            if (def.HasMoodRange) applied.thought = def.MoodCandidates().RandomElementWithFallback();
            if (applied.thought != null) applied.thoughtHours = def.thoughtHours.RandomInRange;
            applied.mealNutritionFactor = def.mealNutritionFactor.RandomInRange;
            return applied;
        }

        // Abilities with charges: charges left, and the tick the next one comes back
        public List<int> abilityCharges = new List<int>();

        // Returns the charges currently available for ability i, adding any that have come back by now
        public int ChargesLeft(int abilityIndex, int max, int cooldown)
        {
            while (abilityCharges.Count <= abilityIndex) abilityCharges.Add(max);
            int charges = abilityCharges[abilityIndex];
            int now = Find.TickManager.TicksGame;
            while (charges < max && now >= AbilityReadyTick(abilityIndex))
            {
                charges++;
                SetAbilityReadyTick(abilityIndex, charges < max ? AbilityReadyTick(abilityIndex) + cooldown : 0);
            }
            abilityCharges[abilityIndex] = charges;
            return charges;
        }

        public void UseCharge(int abilityIndex, int max, int cooldown)
        {
            int charges = ChargesLeft(abilityIndex, max, cooldown);
            if (charges >= max) SetAbilityReadyTick(abilityIndex, Find.TickManager.TicksGame + cooldown);
            abilityCharges[abilityIndex] = charges - 1;
        }

        // Reloadable abilities: charges stored on the item while nobody has the ability (full when never set)
        public int StoredCharges(int abilityIndex)
        {
            while (abilityCharges.Count <= abilityIndex) abilityCharges.Add(def.abilityCharges);
            return abilityCharges[abilityIndex];
        }

        public void SetStoredCharges(int abilityIndex, int charges)
        {
            while (abilityCharges.Count <= abilityIndex) abilityCharges.Add(def.abilityCharges);
            abilityCharges[abilityIndex] = charges;
        }

        public int AbilityReadyTick(int abilityIndex) => abilityIndex < abilityReadyTicks.Count ? abilityReadyTicks[abilityIndex] : 0;

        public void SetAbilityReadyTick(int abilityIndex, int tick)
        {
            while (abilityReadyTicks.Count <= abilityIndex) abilityReadyTicks.Add(0);
            abilityReadyTicks[abilityIndex] = tick;
        }

        public int ThoughtDurationTicks => UnityEngine.Mathf.Max(1, (int)(thoughtHours * GenDate.TicksPerHour));

        public float OffsetFor(int offsetValueIndex) => offsetValueIndex < offsetValues.Count ? offsetValues[offsetValueIndex] : 0f;
        public float ExtraDamageFor(int damageValueIndex) => damageValueIndex < extraDamageValues.Count ? extraDamageValues[damageValueIndex] : 0f;
        public float BuildingFactorFor(int buildingFactorValueIndex) => buildingFactorValueIndex < buildingValues.Count ? buildingValues[buildingFactorValueIndex] : 1f;
        public float FactorFor(int factorValueIndex) => factorValueIndex < factorValues.Count ? factorValues[factorValueIndex] : 1f;
        public float WeaponValueFor(int weaponValueIndex) => weaponValueIndex < weaponValues.Count ? weaponValues[weaponValueIndex] : def.weaponProperties[weaponValueIndex].Neutral;

        // Trigger i's rolled amount k (EntadTrigger.Heal, Rest...); the middle of the range when there's no roll
        public float TriggerValue(int triggerIndex, int triggerValue)
        {
            int idx = triggerIndex * EntadTrigger.ValueCount + triggerValue;
            return idx < triggerValues.Count ? triggerValues[idx] : def.triggers[triggerIndex].RangeAt(triggerValue).Average * def.RarityScale(Rarity);
        }

        public int TriggerReadyTick(int triggerIndex) => triggerIndex < triggerReadyTicks.Count ? triggerReadyTicks[triggerIndex] : 0;

        public void SetTriggerReadyTick(int triggerIndex, int tick)
        {
            while (triggerReadyTicks.Count <= triggerIndex) triggerReadyTicks.Add(0);
            triggerReadyTicks[triggerIndex] = tick;
        }

        // Average position (0..1) of the rolled values within their ranges
        public float CalculateRollQuality()
        {
            float rollQualitySum = 0f;
            int totalTraits = 0;
            float scale = def.RarityScale(Rarity);
            if (def.statOffsets != null)
                for (int i = 0; i < def.statOffsets.Count; i++) { rollQualitySum += def.statOffsets[i].NormalizeScaled(OffsetFor(i), scale, false); totalTraits++; }
            if (def.statFactors != null)
                for (int i = 0; i < def.statFactors.Count; i++) { rollQualitySum += def.statFactors[i].NormalizeScaled(FactorFor(i), scale, true); totalTraits++; }
            if (def.extraDamage != null)
                for (int i = 0; i < def.extraDamage.Count; i++) { rollQualitySum += def.extraDamage[i].NormalizeScaled(ExtraDamageFor(i), scale); totalTraits++; }
            if (def.buildingFactors != null)
                for (int i = 0; i < def.buildingFactors.Count; i++) { rollQualitySum += def.buildingFactors[i].NormalizeScaled(BuildingFactorFor(i), scale); totalTraits++; }
            if (def.weaponProperties != null)
                for (int i = 0; i < def.weaponProperties.Count; i++) { rollQualitySum += def.weaponProperties[i].NormalizeScaled(WeaponValueFor(i), scale); totalTraits++; }
            if (def.triggers != null)
                for (int i = 0; i < def.triggers.Count; i++)
                    for (int k = 0; k < EntadTrigger.ValueCount; k++)
                    {
                        var triggerRange = def.triggers[i].RangeAt(k);
                        if (triggerRange.max - triggerRange.min <= 0.0001f) continue;
                        rollQualitySum += UnityEngine.Mathf.InverseLerp(triggerRange.min * scale, triggerRange.max * scale, TriggerValue(i, k)); totalTraits++;
                    }
            if (def.HasMoodRange && thought != null && def.thoughtMoodRange.max - def.thoughtMoodRange.min > 0.0001f)
            { rollQualitySum += UnityEngine.Mathf.InverseLerp(def.thoughtMoodRange.min, def.thoughtMoodRange.max, EntadTraitDef.MoodEffectOf(thought)); totalTraits++; }
            if (thought != null) { rollQualitySum += def.thoughtHours.max - def.thoughtHours.min > 0.0001f ? UnityEngine.Mathf.InverseLerp(def.thoughtHours.min, def.thoughtHours.max, thoughtHours) : 0.5f; totalTraits++; }
            if (def.HasPortableSpace && def.portableSpace.max - def.portableSpace.min > 0.0001f)
                for (int k = 0; k < spaceValues.Count; k++)
                { rollQualitySum += UnityEngine.Mathf.InverseLerp(def.portableSpace.min * scale, def.portableSpace.max * scale, spaceValues[k]); totalTraits++; }
            var f = def.mealNutritionFactor;
            if (f.max - f.min > 0.0001f) { rollQualitySum += UnityEngine.Mathf.InverseLerp(f.min, f.max, mealNutritionFactor); totalTraits++; }
            return totalTraits == 0 ? 0.5f : rollQualitySum / totalTraits;
        }

        // Market value added by this trait, from the item's value before any entad part (baseValue). By default each
        // point adds a share of that value plus flat silver (settings), so the same trait is worth more on a charge
        // rifle than on a bow. A def's own marketValuePercent/marketValueOffset replace the point price. Where the
        // roll landed moves it +/-25%; drawbacks skip that, since "high in its range" isn't consistently better or
        // worse for them.
        public float MarketValueOffset(float baseValue)
        {
            float value = def.HasMarketValueOverride
                ? (baseValue * (float.IsNaN(def.marketValuePercent) ? 0f : def.marketValuePercent)
                    + (float.IsNaN(def.marketValueOffset) ? 0f : def.marketValueOffset)) * def.ValueScaleAt(Rarity)
                : def.PointsAt(Rarity) * (baseValue * EntadSettings.ValuePercentPerPoint + EntadSettings.ValueSilverPerPoint);
            return def.IsDrawback ? value : value * (0.75f + 0.5f * CalculateRollQuality());
        }
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
                var offsetDict = new Dictionary<StatDef, KeyValuePair<float, float>>();
                foreach (var trait in activeTraits)
                {
                    var offsets = trait.def.statOffsets;
                    if (offsets == null) continue;
                    for (int offsetIndex = 0; offsetIndex < offsets.Count; offsetIndex++)
                    {
                        StatDef offsetStat = offsets[offsetIndex].stat;
                        if (offsetStat == null || !EntadTraitDef.IsWearerStat(offsetStat)) continue;
                        offsetDict.TryGetValue(offsetStat, out var cur);
                        float offsetValue = trait.OffsetFor(offsetIndex);
                        offsetDict[offsetStat] = new KeyValuePair<float, float>(cur.Key + offsetValue, cur.Value + (trait.IsRevealed(EntadEffectKind.Stat) ? 0f : offsetValue));
                    }
                }
                wearerOffsets = offsetDict;
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

        // Everything at once, without a message per trait (the caller says what was found)
        public void RevealAll()
        {
            for (int i = 0; i < activeTraits.Count; i++) activeTraits[i].Reveal(EntadEffectKind.All, false);
        }

        public void RevealedChanged(AppliedEntadTrait m, EntadEffectKind kind, bool announce = true)
        {
            hiddenState = 0;
            propertyFactors = null;
            wearerOffsets = null;
            ClearStatCaches();
            // No glower refresh here: a reveal never changes the radius (building factors apply whether revealed or
            // not), and a reveal can be triggered from inside GlowGrid.RegisterGlower (the radius read in the GlowLight
            // constructor), where re-registering would corrupt the glow grid's light list.
            if (announce) Messages.Message("EF_TraitRevealed".Translate(parent.LabelNoCount, m.def.label), parent, MessageTypeDefOf.NeutralEvent, false);
        }

        // Applies a changed glow radius to a light that is on. The glow grid keeps the radius it read when the light
        // registered, and vanilla's private RefreshGlower does nothing for a light that is already registered, so
        // ForceRegister (deregister, then register) is the call that picks up the new radius. A light that is off
        // reads the radius when it next turns on.
        private void RefreshGlower(CompGlower glower)
        {
            if (glower != null && glower.Glows) glower.ForceRegister(parent.Map);
        }

        // Highest rarity among this item's traits (Common when it has none); sets its durability multiplier
        public EntadRarity HighestRarity
        {
            get
            {
                EntadRarity best = EntadRarity.Common;
                for (int i = 0; i < activeTraits.Count; i++)
                    if (activeTraits[i].Rarity > best) best = activeTraits[i].Rarity;
                return best;
            }
        }

        // Max hit points multiplier every entad gets, visible from the start; 1 for an item without traits
        public float DurabilityFactor => activeTraits.NullOrEmpty() ? 1f : EntadSettings.Durability[HighestRarity];

        // True when some trait's stat effect is still unrevealed (HasHidden also counts mood, fuel, building...).
        public bool HasHiddenStat
        {
            get
            {
                for (int i = 0; i < activeTraits.Count; i++)
                    if (!activeTraits[i].IsRevealed(EntadEffectKind.Stat)) return true;
                return false;
            }
        }

        // Called every frame this item's info card draws, after the card computed its stats without hidden traits.
        // Clears the item and its holder: an equipped weapon's card (melee DPS) reads holder stats, which run
        // StatOffsetFromGear over this item under the same flag. Plain loops, no AllRanges iterator, since it is
        // per frame.
        internal void ClearCardStatCaches()
        {
            Pawn holder = Holder;
            for (int t = 0; t < activeTraits.Count; t++)
            {
                var def = activeTraits[t].def;
                for (int i = 0; def.statOffsets != null && i < def.statOffsets.Count; i++) ClearFor(def.statOffsets[i].stat, holder);
                for (int i = 0; def.statFactors != null && i < def.statFactors.Count; i++) ClearFor(def.statFactors[i].stat, holder);
            }
            StatDefOf.MarketValue.Worker.ClearCacheForThing(parent);
        }

        private void ClearFor(StatDef stat, Pawn holder)
        {
            if (stat == null) return;
            stat.Worker.ClearCacheForThing(parent);
            if (holder != null) stat.Worker.ClearCacheForThing(holder);
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
            // Durability depends on the trait list itself (highest rarity), not on any one trait's stats
            StatDefOf.MaxHitPoints.Worker.ClearCacheForThing(parent);
        }

        // Reveals the given kind of effect on every trait that has it and for which the predicate holds
        public void RevealWhere(EntadEffectKind kind, System.Func<AppliedEntadTrait, bool> predicate = null)
        {
            if (!HasHidden) return;
            // Index loop, no copy: Reveal only updates flags and caches, it never adds or removes traits
            for (int i = 0; i < activeTraits.Count; i++)
            {
                var m = activeTraits[i];
                if (!m.IsRevealed(kind) && (predicate == null || predicate(m))) m.Reveal(kind);
            }
        }

        // Building properties are read while the building is in play, so only count it once it has been
        // around a while (not when it spawns or a save loads). Called every tick from the fuel, glow and power reads,
        // so the bitmask check comes first and the common case (nothing hidden for this property) costs nothing.
        // minAgeTicks: storage reveals on the first item stored, so it only needs to skip the spawn tick itself (items
        // spawning onto it as a save loads or a reinstalled building unpacks all arrive on that tick).
        public void RevealProperty(EntadBuildingProperty property, int minAgeTicks = 2500)
        {
            PropertyFactor(property);
            if ((hiddenPropertyMask & (1 << (int)property)) == 0) return;
            if (Find.TickManager.TicksGame - spawnedTick < minAgeTicks) return;
            for (int t = 0; t < activeTraits.Count; t++)
            {
                var trait = activeTraits[t];
                if (trait.def.buildingFactors == null || trait.IsRevealed(EntadEffectKind.Building)) continue;
                for (int i = 0; i < trait.def.buildingFactors.Count; i++)
                    if (trait.def.buildingFactors[i].property == property) { trait.Reveal(EntadEffectKind.Building); break; }
            }
        }

        // Bit per EntadBuildingProperty that some unrevealed trait changes; rebuilt with propertyFactors
        private int hiddenPropertyMask;

        // The refuelable's def-shared properties, kept while EntadFuel has swapped in a per-item copy
        internal CompProperties originalRefuelableProps;

        public bool FuelHidden => activeTraits.Any(trait => !trait.def.AllFuelTypes.NullOrEmpty() && !trait.IsRevealed(EntadEffectKind.Fuel));

        // Product of this item's factors for a building property; cached since it's read from hot paths
        public float PropertyFactor(EntadBuildingProperty property)
        {
            if (propertyFactors == null)
            {
                propertyFactors = new float[System.Enum.GetValues(typeof(EntadBuildingProperty)).Length];
                for (int i = 0; i < propertyFactors.Length; i++) propertyFactors[i] = 1f;
                hiddenPropertyMask = 0;
                foreach (var trait in activeTraits)
                {
                    if (trait.def.buildingFactors == null) continue;
                    bool hidden = !trait.IsRevealed(EntadEffectKind.Building);
                    for (int i = 0; i < trait.def.buildingFactors.Count; i++)
                    {
                        propertyFactors[(int)trait.def.buildingFactors[i].property] *= trait.BuildingFactorFor(i);
                        if (hidden) hiddenPropertyMask |= 1 << (int)trait.def.buildingFactors[i].property;
                    }
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
            EntadStorage.Refresh(this, parent.Spawned);
            if (!parent.Spawned) return;
            RefreshGlower(parent.GetComp<CompGlower>());
            EntadFuel.Refresh(parent, this);
            parent.GetComp<CompPowerTrader>()?.SetUpPowerVars();
        }


        // Keeps the item's hit point fraction when its maximum changes, so a sturdier item starts at full health
        private void RescaleHitPoints(float fraction)
        {
            ClearStatCaches();
            parent.HitPoints = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(fraction * parent.MaxHitPoints), 1, parent.MaxHitPoints);
            scaledDurability = DurabilityFactor;
        }

        // The durability factor this item's HitPoints were last scaled to. Lets an item catch up when the factor
        // changed without it being rescaled: a save from before durability existed, a settings change made from the
        // main menu, or an item the settings-window rescale couldn't reach (world pawns, pods in flight).
        private float scaledDurability = 1f;

        // Brings HitPoints in line with the current durability factor, keeping the health fraction. A full-health item
        // snaps to the new maximum, so rounding on MaxHitPoints can't leave it a few points short.
        public void SyncDurability()
        {
            if (!parent.def.useHitPoints) return;
            float now = DurabilityFactor;
            if (UnityEngine.Mathf.Approximately(now, scaledDurability)) return;
            StatDefOf.MaxHitPoints.Worker.ClearCacheForThing(parent);
            int newMax = parent.MaxHitPoints;
            int oldMax = UnityEngine.Mathf.RoundToInt(newMax * scaledDurability / now);
            // Slack for MaxHitPoints' own rounding (to 5 above 200), so a full item isn't read as slightly damaged
            // plus newMax's own rounding error, which the division above magnifies by scaledDurability / now
            float ratio = scaledDurability / now;
            int slack = (oldMax > 200 ? 5 : 1) + UnityEngine.Mathf.CeilToInt((newMax > 200 ? 2.5f : 0.5f) * ratio);
            parent.HitPoints = parent.HitPoints >= oldMax - slack
                ? newMax
                : UnityEngine.Mathf.Clamp(UnityEngine.Mathf.RoundToInt(parent.HitPoints * now / scaledDurability), 1, newMax);
            scaledDurability = now;
        }

        // Traits the player switched off in the settings are refused here too, so other mods can't add them either.
        // Only developer tools pass ignoreDisabled.
        public bool AddTrait(EntadTraitDef def, EntadRarity? rarity = null, bool ignoreDisabled = false)
        {
            if (!ignoreDisabled && EntadSettings.IsDisabled(def)) return false;
            // Every trait can change durability (it may become the highest rarity), so always rescale
            bool scaleHp = parent.def.useHitPoints;
            float hpFraction = scaleHp ? (float)parent.HitPoints / parent.MaxHitPoints : 1f;
            var applied = AppliedEntadTrait.Roll(def, rarity);
            applied.owner = this;
            activeTraits.Add(applied);
            if (scaleHp) RescaleHitPoints(hpFraction);
            TraitsChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
            PersonaTraitAdded(applied);
            return true;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            spawnedTick = Find.TickManager.TicksGame;
            if (!activeTraits.NullOrEmpty()) EntadFuel.Refresh(parent, this);
            if (!activeTraits.NullOrEmpty()) EntadStorage.Refresh(this, true, fromSpawn: true);
        }

        // Set while BaseMarketValue reads the stat, so StatPart_Entad leaves this item's market value untouched
        [System.ThreadStatic] internal static Thing baseValueFor;

        // The item's market value without any entad part: material, quality and everything else vanilla counts
        public float BaseMarketValue()
        {
            var prev = baseValueFor;
            baseValueFor = parent;
            try { return parent.GetStatValue(StatDefOf.MarketValue); }
            finally { baseValueFor = prev; }
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            EntadStorage.Refresh(this, false);
        }

        public void RemoveTrait(AppliedEntadTrait trait)
        {
            bool scaleHp = parent.def.useHitPoints;
            float hpFraction = scaleHp ? (float)parent.HitPoints / parent.MaxHitPoints : 1f;
            if (!activeTraits.Contains(trait)) return;
            // Clear while the trait is still listed: ClearStatCaches walks activeTraits, so after removal the
            // removed trait's own stats would never be cleared.
            ClearStatCaches();
            SpillSpaceOnRemove(trait);
            activeTraits.Remove(trait);
            if (scaleHp) RescaleHitPoints(hpFraction);
            TraitsChanged();
            Pawn holder = Holder;
            if (holder != null) EntadMoods.SyncEquipped(holder);
            PersonaTraitRemoved(trait);
            if (trait.def.abilities != null && holder != null)
                foreach (var ability in trait.def.abilities)
                    if (!activeTraits.Any(trait => trait.def.abilities != null && trait.def.abilities.Contains(ability)) && holder.abilities?.GetAbility(ability) != null)
                        holder.abilities.RemoveAbility(ability);
        }

        public void ClearTraits()
        {
            foreach (var trait in activeTraits.ToList()) RemoveTrait(trait);
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref customName, "customName");
            Scribe_Collections.Look(ref activeTraits, "activeTraits", LookMode.Deep);
            // Saves from before durability load as 1 (unscaled), so SyncDurability below brings them up to full scale
            Scribe_Values.Look(ref scaledDurability, "scaledDurability", 1f);
            ExposeBinding();
            ExposePersona();
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                activeTraits = activeTraits ?? new List<AppliedEntadTrait>();
                activeTraits.RemoveAll(trait => trait == null || trait.def == null);
                // Removed traits load as their replacement (Patch_BackCompatibleDefName). An old item that had both
                // the old trait and its replacement, or two old traits with one replacement, would now hold the same
                // trait twice and apply it twice: keep the higher-rarity copy, so the item's durability doesn't drop.
                for (int i = activeTraits.Count - 1; i > 0; i--)
                    for (int j = 0; j < i; j++)
                        if (activeTraits[j].def == activeTraits[i].def)
                        {
                            // Whatever the player had found out about either copy stays found out
                            activeTraits[j].revealedKinds |= activeTraits[i].revealedKinds;
                            activeTraits[i].revealedKinds |= activeTraits[j].revealedKinds;
                            if (activeTraits[i].Rarity > activeTraits[j].Rarity) activeTraits[j] = activeTraits[i];
                            activeTraits.RemoveAt(i);
                            break;
                        }
                propertyFactors = null;
                wearerOffsets = null;
                hiddenState = 0;
                foreach (var trait in activeTraits) trait.owner = this;
                // A bond whose persona trait is gone (def removed or renamed away) mustn't hold the pawn's slot
                if (BondedPawn != null && !IsPersona) Unbond();
                SyncDurability();
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
                defaultLabel = "EF_Rename".Translate(),
                defaultDesc = "EF_RenameDesc".Translate(),
                icon = TexButton.Rename,
                action = () => Find.WindowStack.Add(new Dialog_RenameEntad(this))
            };
        }

        public override string CompInspectStringExtra()
        {
            if (activeTraits.NullOrEmpty()) return null;
            string unknown = "EF_Unknown".Translate();
            string inspectString = "EF_InspectTraits".Translate(string.Join(", ", activeTraits.Select(m => m.NameHidden ? unknown : m.def.LabelCap.ToString())));
            // Plain: an item in the inspect pane is on the map, so it has no holder for the red to be about
            if (IsBound) inspectString += "\n" + "EF_BoundTo".Translate(BoundNames);
            string bond = PersonaInspectLine;
            if (bond != null) inspectString += "\n" + bond;
            return inspectString;
        }

        // Single row in the Basics section; hover shows details like unique weapon traits
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (activeTraits.NullOrEmpty()) yield break;

            string unknown = "EF_Unknown".Translate();
            float baseValue = BaseMarketValue();
            var infoString = new System.Text.StringBuilder();
            infoString.AppendLine("EF_Card_Intro".Translate());
            foreach (var trait in activeTraits)
            {
                if (trait.NameHidden)
                {
                    infoString.Append("\n").AppendLine(unknown.Colorize(ColoredText.TipSectionTitleColor));
                    continue;
                }
                infoString.Append("\n").AppendLine("EF_Card_TraitHeader".Translate(trait.def.LabelCap.Resolve(), trait.Rarity.Label()).Resolve().Colorize(ColoredText.TipSectionTitleColor));
                if (!trait.AnyHidden) infoString.AppendLine(trait.def.description);
                for (int i = 0; trait.IsRevealed(EntadEffectKind.Stat) && trait.def.statOffsets != null && i < trait.def.statOffsets.Count; i++)
                {
                    var stat = trait.def.statOffsets[i].stat;
                    if (stat == null || !trait.def.UsesStat(stat, parent)) continue;
                    float statValue = trait.OffsetFor(i);
                    // Offsets are added before the stat is finalized, so they read in the unfinalized style (as vanilla's
                    // StatModifier.ValueToStringAsOffset does): shooting accuracy +3 is three skill levels, not +300%
                    infoString.AppendLine(" - " + "EF_Card_StatOffset".Translate(stat.LabelCap, stat.Worker.ValueToString(statValue, false, ToStringNumberSense.Offset)));
                }
                for (int i = 0; trait.IsRevealed(EntadEffectKind.Stat) && trait.def.statFactors != null && i < trait.def.statFactors.Count; i++)
                {
                    var stat = trait.def.statFactors[i].stat;
                    if (stat == null || !trait.def.UsesStat(stat, parent)) continue;
                    infoString.AppendLine(" - " + "EF_Card_StatFactor".Translate(stat.LabelCap, trait.FactorFor(i).ToStringPercent()));
                }
                if (trait.thought != null && trait.IsRevealed(EntadEffectKind.Mood))
                {
                    infoString.AppendLine(" - " + "EF_Card_Mood".Translate(trait.thought.stages?.FirstOrDefault()?.LabelCap ?? trait.thought.defName,
                        EntadTraitDef.MoodEffectOf(trait.thought).ToString("+0.#;-0.#"), trait.thoughtHours.ToString("0.#")));
                }
                if (trait.def.HasDamageEffect && trait.IsRevealed(EntadEffectKind.Damage))
                {
                    if (trait.def.changeDamageType != null) infoString.AppendLine(" - " + "EF_Card_DamageType".Translate(trait.def.changeDamageType.LabelCap));
                    for (int i = 0; trait.def.extraDamage != null && i < trait.def.extraDamage.Count; i++)
                        infoString.AppendLine(" - " + "EF_Card_ExtraDamage".Translate(trait.def.extraDamage[i].damageType.label, trait.ExtraDamageFor(i).ToString("0.#")));
                }
                if (trait.def.HasWeaponEffect && trait.IsRevealed(EntadEffectKind.Damage))
                {
                    for (int i = 0; trait.def.weaponProperties != null && i < trait.def.weaponProperties.Count; i++)
                        infoString.AppendLine(" - " + "EF_Card_StatOffset".Translate(trait.def.weaponProperties[i].Label, trait.def.weaponProperties[i].ValueString(trait.WeaponValueFor(i))));
                    if (trait.def.ignoreAccuracyMaluses) infoString.AppendLine(" - " + "EF_Card_IgnoresAccuracyMaluses".Translate());
                }
                for (int i = 0; trait.IsRevealed(EntadEffectKind.Trigger) && trait.def.triggers != null && i < trait.def.triggers.Count; i++)
                {
                    int triggerIndex = i;
                    infoString.AppendLine(" - " + trait.def.triggers[i].Line(triggerValue => trait.TriggerValue(triggerIndex, triggerValue)));
                }
                if (trait.def.HasPortableSpace && trait.IsRevealed(EntadEffectKind.Space)) infoString.AppendLine(" - " + EntadPortableSpace.CardLine(trait));
                if (trait.IsRevealed(EntadEffectKind.Bond)) foreach (var line in PersonaCardLines(trait)) infoString.AppendLine(" - " + line);
                if (!trait.def.equippedHediffs.NullOrEmpty() && trait.IsRevealed(EntadEffectKind.Hediff))
                    infoString.AppendLine(" - " + "EF_Card_EquippedHediffs".Translate(string.Join(", ", trait.def.equippedHediffs.Select(h => h.LabelCap.ToString()))));
                if (!trait.def.AllFuelTypes.NullOrEmpty() && trait.IsRevealed(EntadEffectKind.Fuel))
                    infoString.AppendLine(" - " + (trait.def.replaceFuel ? "EF_Card_BurnsOnly" : "EF_Card_AlsoBurns").Translate(string.Join(", ", trait.def.AllFuelTypes.Select(f => f.LabelCap.ToString()))));
                for (int i = 0; trait.IsRevealed(EntadEffectKind.Building) && trait.def.buildingFactors != null && i < trait.def.buildingFactors.Count; i++)
                    infoString.AppendLine(" - " + "EF_Card_StatFactor".Translate(trait.def.buildingFactors[i].Label, trait.BuildingFactorFor(i).ToStringPercent()));
                if (!trait.def.abilities.NullOrEmpty() && trait.IsRevealed(EntadEffectKind.Ability))
                {
                    infoString.AppendLine(" - " + (parent.def.building != null ? "EF_Card_ActivatableAbility" : "EF_Card_GrantsAbility").Translate(string.Join(", ", trait.def.abilities.Select(a => a.LabelCap.ToString()))));
                    if (trait.def.IsReloadable)
                        foreach (var slot in EntadReload.SlotsOf(this).Where(x => x.trait == trait))
                            infoString.AppendLine("   " + "EF_Card_Reloadable".Translate(EntadReload.Charges(slot), slot.Max, slot.Ammo.label, slot.PerCharge));
                }
                if (trait.def.HasMealEffect && trait.IsRevealed(EntadEffectKind.Meal))
                {
                    if (trait.def.mealNutritionFactor.min != 1f || trait.def.mealNutritionFactor.max != 1f)
                        infoString.AppendLine(" - " + "EF_Card_MealNutrition".Translate(trait.mealNutritionFactor.ToStringPercent()));
                    if (trait.def.mealQualityOffset != 0)
                        infoString.AppendLine(" - " + "EF_Card_MealQuality".Translate(trait.def.mealQualityOffset.ToString("+0;-0")));
                    if (trait.def.mealThought != null)
                        infoString.AppendLine(" - " + "EF_Card_MealThought".Translate(trait.def.mealThought.stages?.FirstOrDefault()?.LabelCap ?? trait.def.mealThought.defName));
                }
                if (trait.AnyHidden) infoString.AppendLine(" - " + unknown);
                if (!trait.AnyHidden) infoString.AppendLine(" - " + "EF_Card_MarketValue".Translate(trait.MarketValueOffset(baseValue).ToStringMoneyOffset()));
            }
            if (activeTraits.Any(m => m.AnyHidden))
                infoString.Append("\n").AppendLine("EF_Card_Unidentified".Translate(EntadSettings.MysteryBonus.ToStringPercent()));

            foreach (var e in DamageDisplayStats()) yield return e;

            if (IsBound)
            {
                Pawn holder = Holder;
                if (holder != null && !ActiveFor(holder)) infoString.Append("\n").AppendLine("EF_Card_InactiveFor".Translate(holder.LabelShort));
                yield return new StatDrawEntry(StatCategoryDefOf.Basics, "EF_Card_BoundLabel".Translate(), BoundLine(BoundNames),
                    "EF_Card_BoundDesc".Translate(), 3999);
            }
            string label = string.Join(", ", activeTraits.Select(m => m.NameHidden ? unknown : m.def.label));
            yield return new StatDrawEntry(StatCategoryDefOf.Basics, "EF_Card_TraitsLabel".Translate(), label, infoString.ToString().TrimEnd(), 4000);
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
            // A bound weapon held outside the bloodline deals no extra damage (EntadWeaponDamage.CompOf), so don't list it
            if (!parent.def.IsWeapon || !ActiveForHolder) yield break;
            var cat = parent.def.IsRangedWeapon ? StatCategoryDefOf.Weapon_Ranged : StatCategoryDefOf.Weapon_Melee;
            int order = 5500;
            float total = 0f;
            foreach (var trait in activeTraits)
            {
                if (!trait.IsRevealed(EntadEffectKind.Damage)) continue;
                // Burst and stopping power values are folded into vanilla's own rows (Patch_ThingDef_SpecialDisplayStats_EntadWeapon)
                if (trait.def.ignoreAccuracyMaluses)
                    yield return new StatDrawEntry(cat, "EF_Stat_IgnoresAccuracyMaluses".Translate(), "Yes".Translate(),
                        "EF_Stat_IgnoresAccuracyMalusesDesc".Translate(trait.def.LabelCap), order++);
                if (!trait.def.HasDamageEffect) continue;
                if (trait.def.changeDamageType != null)
                    yield return new StatDrawEntry(cat, "EF_Stat_DamageType".Translate(), trait.def.changeDamageType.LabelCap,
                        "EF_Stat_DamageTypeDesc".Translate(trait.def.LabelCap, trait.def.changeDamageType.label), order++);
                for (int i = 0; trait.def.extraDamage != null && i < trait.def.extraDamage.Count; i++)
                {
                    float extraDamageValue = trait.ExtraDamageFor(i);
                    total += extraDamageValue;
                    var damageType = trait.def.extraDamage[i].damageType;
                    yield return new StatDrawEntry(cat, "EF_Stat_ExtraDamage".Translate(damageType.label), "+" + extraDamageValue.ToString("0.#"),
                        "EF_Stat_ExtraDamageDesc".Translate(trait.def.LabelCap, damageType.label), order++);
                }
            }
            if (total > 0f)
                yield return new StatDrawEntry(cat, "EF_Stat_ExtraTotal".Translate(), "+" + total.ToString("0.#"),
                    "EF_Stat_ExtraTotalDesc".Translate(), order);
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
