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
            Scribe_Defs.Look(ref thought, "thought");
            Scribe_Values.Look(ref thoughtHours, "thoughtHours");
            Scribe_Values.Look(ref mealNutritionFactor, "mealNutritionFactor", 1f);
            Scribe_Values.Look(ref revealedKinds, "revealedKinds", EntadEffectKind.All);
            bool legacyRevealed = true;
            Scribe_Values.Look(ref legacyRevealed, "revealed", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && !legacyRevealed && revealedKinds == EntadEffectKind.All) revealedKinds = EntadEffectKind.None;
            Scribe_Collections.Look(ref abilityReadyTicks, "abilityReadyTicks", LookMode.Value);
            Scribe_Collections.Look(ref abilityCharges, "abilityCharges", LookMode.Value);
            // A trait saved under a removed name: its stored rolls came from the old trait's range, which can be far
            // outside the replacement's (Marksman's Eye rolled shooting accuracy 0.03-0.1, Steady Hands rolls 1-4).
            // Re-roll the values at the saved rarity so the item behaves like any other copy of the replacement.
            if (Scribe.mode == LoadSaveMode.LoadingVars && def != null && savedName != null && savedName != def.defName)
                RerollValues();
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

        private void RerollValues()
        {
            var fresh = Roll(def, Rarity);
            rarityValue = fresh.rarityValue;
            offsetValues = fresh.offsetValues;
            factorValues = fresh.factorValues;
            extraDamageValues = fresh.extraDamageValues;
            buildingValues = fresh.buildingValues;
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
        public void RevealProperty(EntadBuildingProperty property)
        {
            PropertyFactor(property);
            if ((hiddenPropertyMask & (1 << (int)property)) == 0) return;
            if (Find.TickManager.TicksGame - spawnedTick < 2500) return;
            for (int t = 0; t < activeTraits.Count; t++)
            {
                var m = activeTraits[t];
                if (m.def.buildingFactors == null || m.IsRevealed(EntadEffectKind.Building)) continue;
                for (int i = 0; i < m.def.buildingFactors.Count; i++)
                    if (m.def.buildingFactors[i].property == property) { m.Reveal(EntadEffectKind.Building); break; }
            }
        }

        // Bit per EntadBuildingProperty that some unrevealed trait changes; rebuilt with propertyFactors
        private int hiddenPropertyMask;

        // The refuelable's def-shared properties, kept while EntadFuel has swapped in a per-item copy
        internal CompProperties originalRefuelableProps;

        public bool FuelHidden => activeTraits.Any(m => !m.def.AllFuelTypes.NullOrEmpty() && !m.IsRevealed(EntadEffectKind.Fuel));

        // Product of this item's factors for a building property; cached since it's read from hot paths
        public float PropertyFactor(EntadBuildingProperty property)
        {
            if (propertyFactors == null)
            {
                propertyFactors = new float[System.Enum.GetValues(typeof(EntadBuildingProperty)).Length];
                for (int i = 0; i < propertyFactors.Length; i++) propertyFactors[i] = 1f;
                hiddenPropertyMask = 0;
                foreach (var m in activeTraits)
                {
                    if (m.def.buildingFactors == null) continue;
                    bool hidden = !m.IsRevealed(EntadEffectKind.Building);
                    for (int i = 0; i < m.def.buildingFactors.Count; i++)
                    {
                        propertyFactors[(int)m.def.buildingFactors[i].property] *= m.BuildingFactorFor(i);
                        if (hidden) hiddenPropertyMask |= 1 << (int)m.def.buildingFactors[i].property;
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
            bool scaleHp = parent.def.useHitPoints;
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
            // Saves from before durability load as 1 (unscaled), so SyncDurability below brings them up to full scale
            Scribe_Values.Look(ref scaledDurability, "scaledDurability", 1f);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                activeTraits = activeTraits ?? new List<AppliedEntadTrait>();
                activeTraits.RemoveAll(m => m == null || m.def == null);
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
                foreach (var m in activeTraits) m.owner = this;
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
            return "EF_InspectTraits".Translate(string.Join(", ", activeTraits.Select(m => m.NameHidden ? unknown : m.def.LabelCap.ToString())));
        }

        // Single row in the Basics section; hover shows details like unique weapon traits
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (activeTraits.NullOrEmpty()) yield break;

            string unknown = "EF_Unknown".Translate();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("EF_Card_Intro".Translate());
            foreach (var m in activeTraits)
            {
                if (m.NameHidden)
                {
                    sb.Append("\n").AppendLine(unknown.Colorize(ColoredText.TipSectionTitleColor));
                    continue;
                }
                sb.Append("\n").AppendLine("EF_Card_TraitHeader".Translate(m.def.LabelCap.Resolve(), m.Rarity.Label()).Resolve().Colorize(ColoredText.TipSectionTitleColor));
                if (!m.AnyHidden) sb.AppendLine(m.def.description);
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statOffsets != null && i < m.def.statOffsets.Count; i++)
                {
                    var s = m.def.statOffsets[i].stat;
                    if (s == null) continue;
                    float v = m.OffsetFor(i);
                    // Offsets are added before the stat is finalized, so they read in the unfinalized style (as vanilla's
                    // StatModifier.ValueToStringAsOffset does): shooting accuracy +3 is three skill levels, not +300%
                    sb.AppendLine(" - " + "EF_Card_StatOffset".Translate(s.LabelCap, s.Worker.ValueToString(v, false, ToStringNumberSense.Offset)));
                }
                for (int i = 0; m.IsRevealed(EntadEffectKind.Stat) && m.def.statFactors != null && i < m.def.statFactors.Count; i++)
                {
                    var s = m.def.statFactors[i].stat;
                    if (s == null) continue;
                    sb.AppendLine(" - " + "EF_Card_StatFactor".Translate(s.LabelCap, m.FactorFor(i).ToStringPercent()));
                }
                if (m.thought != null && m.IsRevealed(EntadEffectKind.Mood))
                {
                    sb.AppendLine(" - " + "EF_Card_Mood".Translate(m.thought.stages?.FirstOrDefault()?.LabelCap ?? m.thought.defName,
                        EntadTraitDef.MoodEffectOf(m.thought).ToString("+0.#;-0.#"), m.thoughtHours.ToString("0.#")));
                }
                if (m.def.HasDamageEffect && m.IsRevealed(EntadEffectKind.Damage))
                {
                    if (m.def.changeDamageType != null) sb.AppendLine(" - " + "EF_Card_DamageType".Translate(m.def.changeDamageType.LabelCap));
                    for (int i = 0; m.def.extraDamage != null && i < m.def.extraDamage.Count; i++)
                        sb.AppendLine(" - " + "EF_Card_ExtraDamage".Translate(m.def.extraDamage[i].damageType.label, m.ExtraDamageFor(i).ToString("0.#")));
                }
                if (!m.def.AllFuelTypes.NullOrEmpty() && m.IsRevealed(EntadEffectKind.Fuel))
                    sb.AppendLine(" - " + (m.def.replaceFuel ? "EF_Card_BurnsOnly" : "EF_Card_AlsoBurns").Translate(string.Join(", ", m.def.AllFuelTypes.Select(f => f.LabelCap.ToString()))));
                for (int i = 0; m.IsRevealed(EntadEffectKind.Building) && m.def.buildingFactors != null && i < m.def.buildingFactors.Count; i++)
                    sb.AppendLine(" - " + "EF_Card_StatFactor".Translate(m.def.buildingFactors[i].Label, m.BuildingFactorFor(i).ToStringPercent()));
                if (!m.def.abilities.NullOrEmpty() && m.IsRevealed(EntadEffectKind.Ability))
                    sb.AppendLine(" - " + (parent.def.building != null ? "EF_Card_ActivatableAbility" : "EF_Card_GrantsAbility").Translate(string.Join(", ", m.def.abilities.Select(a => a.LabelCap.ToString()))));
                if (m.def.HasMealEffect && m.IsRevealed(EntadEffectKind.Meal))
                {
                    if (m.def.mealNutritionFactor.min != 1f || m.def.mealNutritionFactor.max != 1f)
                        sb.AppendLine(" - " + "EF_Card_MealNutrition".Translate(m.mealNutritionFactor.ToStringPercent()));
                    if (m.def.mealQualityOffset != 0)
                        sb.AppendLine(" - " + "EF_Card_MealQuality".Translate(m.def.mealQualityOffset.ToString("+0;-0")));
                    if (m.def.mealThought != null)
                        sb.AppendLine(" - " + "EF_Card_MealThought".Translate(m.def.mealThought.stages?.FirstOrDefault()?.LabelCap ?? m.def.mealThought.defName));
                }
                if (m.AnyHidden) sb.AppendLine(" - " + unknown);
                if (!m.AnyHidden) sb.AppendLine(" - " + "EF_Card_MarketValue".Translate(m.MarketValueOffset().ToStringMoney()));
            }
            if (activeTraits.Any(m => m.AnyHidden))
                sb.Append("\n").AppendLine("EF_Card_Unidentified".Translate(EntadTraitDef.UnidentifiedMarketValue.ToStringMoney()));

            foreach (var e in DamageDisplayStats()) yield return e;

            string label = string.Join(", ", activeTraits.Select(m => m.NameHidden ? unknown : m.def.label));
            yield return new StatDrawEntry(StatCategoryDefOf.Basics, "EF_Card_TraitsLabel".Translate(), label, sb.ToString().TrimEnd(), 4000);
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
                    yield return new StatDrawEntry(cat, "EF_Stat_DamageType".Translate(), m.def.changeDamageType.LabelCap,
                        "EF_Stat_DamageTypeDesc".Translate(m.def.LabelCap, m.def.changeDamageType.label), order++);
                for (int i = 0; m.def.extraDamage != null && i < m.def.extraDamage.Count; i++)
                {
                    float v = m.ExtraDamageFor(i);
                    total += v;
                    var d = m.def.extraDamage[i].damageType;
                    yield return new StatDrawEntry(cat, "EF_Stat_ExtraDamage".Translate(d.label), "+" + v.ToString("0.#"),
                        "EF_Stat_ExtraDamageDesc".Translate(m.def.LabelCap, d.label), order++);
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
