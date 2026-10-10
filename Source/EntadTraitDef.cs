using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public enum EntadRarity
    {
        Common,
        Uncommon,
        Rare,
        Epic,
        Legendary
    }

    public static class EntadRarityExtensions
    {
        // Translated rarity name. Translate is a dictionary lookup, cheap enough for the card's redraws.
        private static readonly string[] Keys = { "EF_Rarity_Common", "EF_Rarity_Uncommon", "EF_Rarity_Rare", "EF_Rarity_Epic", "EF_Rarity_Legendary" };

        public static string Label(this EntadRarity r)
        {
            int i = (int)r;
            return i >= 0 && i < Keys.Length ? Keys[i].Translate().ToString() : r.ToString();
        }
    }

    // A stat trait whose value is rolled between min and max when applied to an item
    public class StatTraitRange
    {
        public StatDef stat;
        public float min;
        public float max;

        private float Roll() => Rand.Range(min, max);

        // Rarity scaling widens the whole range: offsets multiply, factors scale their distance from 1
        public float RollScaled(float scale, bool factor) => EntadTraitDef.Scale(Roll(), scale, factor);

        public float NormalizeScaled(float value, float scale, bool factor)
        {
            float lo = EntadTraitDef.Scale(min, scale, factor), hi = EntadTraitDef.Scale(max, scale, factor);
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }
    }

    // Building properties that aren't stats (they live in comp properties) and can be scaled by a trait
    public enum EntadBuildingProperty
    {
        LightRadius,
        HeatOutput,
        FuelConsumptionRate,
        FuelCapacity,
        PowerGeneration,
        PowerConsumption,
        TemperatureControlPower,
        StorageCapacity        // item stacks per cell on storage buildings (EntadStorage.cs)
    }

    // A multiplier on a building property, rolled between min and max when applied to an item
    public class BuildingPropertyRange
    {
        public EntadBuildingProperty property;
        public float min = 1f;
        public float max = 1f;

        private float Roll() => Rand.Range(min, max);

        public float RollScaled(float scale) => EntadTraitDef.Scale(Roll(), scale, true);

        public float NormalizeScaled(float value, float scale)
        {
            float lo = EntadTraitDef.Scale(min, scale, true), hi = EntadTraitDef.Scale(max, scale, true);
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }

        // Private on CompProperties_Power; the public PowerConsumption applies research upgrades on top. A FieldInfo,
        // not FieldRefAccess: that throws from this static initializer if a game version renames the field, which
        // would break AppliesTo for every property. Null instead means power consumption traits never apply.
        private static readonly System.Reflection.FieldInfo BasePowerConsumptionField =
            HarmonyLib.AccessTools.Field(typeof(CompProperties_Power), "basePowerConsumption");

        private static float BasePowerConsumption(CompProperties_Power pp) =>
            BasePowerConsumptionField?.GetValue(pp) is float f ? f : 0f;

        // LWM's Deep Storage sets capacity in its own comp, which a vanilla capacity patch doesn't reach
        private static bool HasDeepStorageComp(ThingDef td)
        {
            if (td.comps == null) return false;
            foreach (var c in td.comps)
                for (var t = c.compClass; t != null; t = t.BaseType)
                    if (t.FullName == "LWM.DeepStorage.CompDeepStorage") return true;
            return false;
        }

        public bool AppliesTo(ThingDef td)
        {
            if (property == EntadBuildingProperty.StorageCapacity)
                // Shelf-like storage only: a 1-stack building (hopper) isn't built to hold several, and vanilla skips
                // moving extra stacks off it when it's removed
                return td?.thingClass != null && typeof(Building_Storage).IsAssignableFrom(td.thingClass)
                    && td.building != null && td.building.maxItemsInCell >= 2
                    && !typeof(Building_Bookcase).IsAssignableFrom(td.thingClass) && !HasDeepStorageComp(td)
                    && (!EntadStorage_ASF.IsDef(td) || (EntadStorage_ASF.Supported && !EntadStorage_ASF.HasCellTable(td)));
            if (td?.comps == null) return false;
            foreach (var c in td.comps)
            {
                switch (property)
                {
                    case EntadBuildingProperty.LightRadius: if (c is CompProperties_Glower) return true; break;
                    case EntadBuildingProperty.HeatOutput: if (c is CompProperties_HeatPusher) return true; break;
                    case EntadBuildingProperty.FuelConsumptionRate:
                    case EntadBuildingProperty.FuelCapacity: if (c is CompProperties_Refuelable) return true; break;
                    case EntadBuildingProperty.TemperatureControlPower: if (c is CompProperties_TempControl) return true; break;
                    case EntadBuildingProperty.PowerConsumption:
                        if (c is CompProperties_Power pp && pp.compClass != null && !typeof(CompPowerPlant).IsAssignableFrom(pp.compClass) && BasePowerConsumption(pp) > 0f) return true; break;
                    case EntadBuildingProperty.PowerGeneration: if (c.compClass != null && typeof(CompPowerPlant).IsAssignableFrom(c.compClass)) return true; break;
                }
            }
            return false;
        }

        public string Label
        {
            get
            {
                switch (property)
                {
                    case EntadBuildingProperty.LightRadius: return "EF_Prop_LightRadius".Translate();
                    case EntadBuildingProperty.HeatOutput: return "EF_Prop_HeatOutput".Translate();
                    case EntadBuildingProperty.FuelConsumptionRate: return "EF_Prop_FuelConsumptionRate".Translate();
                    case EntadBuildingProperty.FuelCapacity: return "EF_Prop_FuelCapacity".Translate();
                    case EntadBuildingProperty.PowerConsumption: return "EF_Prop_PowerConsumption".Translate();
                    case EntadBuildingProperty.TemperatureControlPower: return "EF_Prop_TemperatureControlPower".Translate();
                    case EntadBuildingProperty.StorageCapacity: return "EF_Prop_StorageCapacity".Translate();
                    default: return "EF_Prop_PowerGeneration".Translate();
                }
            }
        }
    }

    // Extra damage dealt alongside a weapon's normal damage on each hit; amount is rolled when applied
    public class ExtraDamageRange
    {
        public DamageDef damageType;
        public float min = 1f;
        public float max = 1f;

        private float Roll() => Rand.Range(min, max);

        public float RollScaled(float scale) => Roll() * scale;

        public float NormalizeScaled(float value, float scale)
        {
            float lo = min * scale, hi = max * scale;
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }
    }

    // Ranged weapon properties that aren't stats (they live on the verb or the projectile)
    public enum EntadWeaponProperty
    {
        BurstShotCount,   // factor on shots per burst (rounded up, like vanilla unique weapons)
        BurstShotSpeed,   // factor on how fast burst shots follow each other (divides the ticks between them)
        StoppingPower     // added to each projectile's stopping power
    }

    // A weapon property rolled between min and max when applied to an item. Burst properties are factors (rarity
    // scaling widens their distance from 1); stopping power is an offset (scaling multiplies it).
    public class WeaponPropertyRange
    {
        public EntadWeaponProperty property;
        public float min = 1f;
        public float max = 1f;

        public bool IsFactor => property != EntadWeaponProperty.StoppingPower;

        public float Neutral => IsFactor ? 1f : 0f;

        public float RollScaled(float scale) => EntadTraitDef.Scale(Rand.Range(min, max), scale, IsFactor);

        public float NormalizeScaled(float value, float scale)
        {
            float lo = EntadTraitDef.Scale(min, scale, IsFactor), hi = EntadTraitDef.Scale(max, scale, IsFactor);
            return hi - lo > 0.0001f ? UnityEngine.Mathf.InverseLerp(lo, hi, value) : 0.5f;
        }

        public string Label
        {
            get
            {
                switch (property)
                {
                    case EntadWeaponProperty.BurstShotCount: return "EF_Weapon_BurstShotCount".Translate();
                    case EntadWeaponProperty.BurstShotSpeed: return "EF_Weapon_BurstShotSpeed".Translate();
                    default: return "EF_Weapon_StoppingPower".Translate();
                }
            }
        }

        public string ValueString(float v) => IsFactor ? "x" + v.ToStringPercent() : v.ToString("+0.0#;-0.0#");

        // Burst properties only on weapons that already fire bursts: shot counts round up, so x1.25 on a single-shot
        // rifle or a one-use launcher would double its shots (vanilla limits its own to BurstFire weapons)
        public bool AppliesTo(ThingDef td)
        {
            if (!td.IsRangedWeapon || td.Verbs.NullOrEmpty()) return false;
            switch (property)
            {
                case EntadWeaponProperty.BurstShotCount:
                case EntadWeaponProperty.BurstShotSpeed: return td.Verbs.Any(v => v.burstShotCount > 1);
                case EntadWeaponProperty.StoppingPower: return td.Verbs.Any(v => v.defaultProjectile?.projectile != null);
                default: return false;
            }
        }

        // Highest value rarity scaling may push this to; past it the trait stops being offered at higher rarities
        // (x3 shots or speed, or +3 stopping power: vanilla's strongest unique traits are x2 and +1)
        public float MaxScaled => 3f;
    }

    [System.Flags]
    public enum EntadEffectKind
    {
        None = 0,
        Stat = 1,
        Mood = 2,
        Meal = 4,
        Ability = 8,
        Building = 16,
        Fuel = 32,
        Damage = 64,     // also burst, stopping power and ignoring accuracy penalties: all noticed when a shot lands
        Hediff = 128,    // equipped hediffs: they show on the health tab, so they reveal the moment the item is equipped
        Trigger = 256,   // "when X happens, do Y" effects (EntadTriggers): revealed the first time one fires
        Bond = 512,      // persona bond effects (EntadPersona): the persona tells its wielder everything when it bonds
        Space = 1024,    // portable space (EntadPortableSpace): revealed the first time its gizmo is used
        All = Stat | Mood | Meal | Ability | Building | Fuel | Damage | Hediff | Trigger | Bond | Space
    }

    public class EntadTraitDef : Def
    {
        public List<string> categories = new List<string>();
        public EntadRarity rarity = EntadRarity.Common;

        // When true, "rarity" is the lowest rarity this trait appears at. It can also appear at every higher
        // rarity up to MaxRarity, with its min/max scaled by the rarity multipliers in the mod settings (relative to
        // this def's own rarity). When false the trait only ever appears at "rarity".
        public bool scalesWithRarity;

        // Generation cost at this def's own rarity (counted against EntadApi's point target). Unset: the rarity's value from the
        // settings. Below zero: a drawback, which gives points back. Never shown to the player.
        public float points = float.NaN;

        public float BasePoints => float.IsNaN(points) ? EntadSettings.Points[rarity] : points;

        public bool IsDrawback => BasePoints < 0f;

        // Optional fixed price, replacing the point-based one (CompEntad.MarketValueOffset): a share of the item's
        // base value and/or flat silver, at this def's own rarity. For a trait that's cheap to roll but valuable to
        // own, or the reverse. Setting either one replaces the whole point price.
        public float marketValuePercent = float.NaN;
        public float marketValueOffset = float.NaN;

        public bool HasMarketValueOverride => !float.IsNaN(marketValuePercent) || !float.IsNaN(marketValueOffset);

        // A scaling trait's fixed price follows its points when it rolls above its own rarity
        public float ValueScaleAt(EntadRarity at)
        {
            float own = BasePoints;
            return scalesWithRarity && UnityEngine.Mathf.Abs(own) > 0.0001f ? PointsAt(at) / own : 1f;
        }

        // Cost when rolled at a rarity: scaling traits follow the settings' point table relative to their own rarity
        public float PointsAt(EntadRarity at)
        {
            float basePts = BasePoints;
            if (!scalesWithRarity || at == rarity) return basePts;
            float own = EntadSettings.Points[rarity];
            return own > 0.0001f ? basePts * EntadSettings.Points[at] / own : basePts;
        }

        // The item binds to the first pawn who equips it (weapons, apparel) or uses it for themselves (furniture), if
        // nobody is bound yet. From then on its traits only work for that pawn and their descendants.
        public bool bindOnFirstUse;

        public const float MinScaledFactor = 0.05f;

        public static float Scale(float value, float scale, bool factor)
        {
            return factor ? UnityEngine.Mathf.Max(MinScaledFactor, 1f + (value - 1f) * scale) : value * scale;
        }

        // Multiplier applied to this def's ranges when rolled at the given rarity (1 at its own rarity)
        public float RarityScale(EntadRarity at)
        {
            if (!scalesWithRarity) return 1f;
            float baseMult = EntadSettings.RarityMultiplier(rarity);
            return baseMult > 0.0001f ? EntadSettings.RarityMultiplier(at) / baseMult : 1f;
        }

        // Whether the scaled ranges still make sense at this rarity: a factor can't be pushed to or below zero, and an
        // offset can't exceed what its stat allows. Rarities past the first one that fails are not offered.
        private bool FitsAt(EntadRarity at)
        {
            float scale = RarityScale(at);
            foreach (var r in Ranges(statFactors))
                if (1f + (UnityEngine.Mathf.Min(r.min, r.max) - 1f) * scale < MinScaledFactor) return false;
            if (buildingFactors != null)
                foreach (var b in buildingFactors)
                    if (1f + (UnityEngine.Mathf.Min(b.min, b.max) - 1f) * scale < MinScaledFactor) return false;
            foreach (var r in Ranges(statOffsets))
                if (r.stat != null && UnityEngine.Mathf.Max(UnityEngine.Mathf.Abs(r.min), UnityEngine.Mathf.Abs(r.max)) * scale > r.stat.maxValue) return false;
            // Triggered need restores are fractions of the need: past 100% they'd be wasted
            if (triggers != null)
                foreach (var t in triggers)
                    if ((t.rest.max > 0f && t.rest.max * scale > 1f) || (t.food.max > 0f && t.food.max * scale > 1f) || (t.joy.max > 0f && t.joy.max * scale > 1f)) return false;
            // Burst factors and stopping power: a factor can't drop to nothing, and nothing may pass its cap
            if (weaponProperties != null)
                foreach (var w in weaponProperties)
                {
                    float lo = EntadTraitDef.Scale(UnityEngine.Mathf.Min(w.min, w.max), scale, w.IsFactor);
                    float hi = EntadTraitDef.Scale(UnityEngine.Mathf.Max(w.min, w.max), scale, w.IsFactor);
                    if ((w.IsFactor && lo <= MinScaledFactor) || hi > w.MaxScaled) return false;
                }
            return true;
        }

        public EntadRarity MaxRarity
        {
            get
            {
                if (!scalesWithRarity) return rarity;
                EntadRarity best = rarity;
                for (var r = rarity + 1; r <= EntadRarity.Legendary; r++)
                {
                    if (!FitsAt(r)) break;
                    best = r;
                }
                return best;
            }
        }

        // For the settings list: a trait that rolls at more than one rarity has no single rarity to show
        public string RarityLabel => scalesWithRarity && MaxRarity > rarity ? "EF_Rarity_Variable".Translate().ToString() : rarity.Label();

        public bool CanAppearAt(EntadRarity at) => scalesWithRarity ? at >= rarity && at <= MaxRarity : at == rarity;

        public EntadRarity ClampRarity(EntadRarity at)
        {
            if (!scalesWithRarity) return rarity;
            return at < rarity ? rarity : (at > MaxRarity ? MaxRarity : at);
        }

        public List<StatTraitRange> statOffsets;
        public List<StatTraitRange> statFactors;

        // Multipliers on building properties that aren't stats: light radius, heat output, fuel consumption
        // rate, fuel capacity, power generation. Only applies to buildings that have the matching component.
        public List<BuildingPropertyRange> buildingFactors;

        // Weapons only. changeDamageType replaces the damage type of the weapon's attacks (melee and ranged);
        // extraDamage adds more damage of other types to each hit.
        public DamageDef changeDamageType;
        public List<ExtraDamageRange> extraDamage;

        // Ranged weapons that don't fire a plain direct-hit projectile (explosive projectiles, beams, flame streams...)
        // deal their damage some other way, so damage added to a hit would never apply. Melee weapons are never area weapons.
        public static bool IsAreaWeapon(ThingDef td)
        {
            if (!td.IsRangedWeapon) return false;
            if (td.Verbs != null)
                foreach (var v in td.Verbs)
                {
                    var proj = v.defaultProjectile;
                    if (proj?.projectile == null) continue;
                    if (typeof(Verb_ShootBeam).IsAssignableFrom(v.verbClass)) continue;
                    if (proj.projectile.explosionRadius > 0f || typeof(Projectile_Explosive).IsAssignableFrom(proj.thingClass)) continue;
                    return false;
                }
            return true;
        }

        public bool HasDamageEffect => changeDamageType != null || !extraDamage.NullOrEmpty();

        // Ranged weapons only: burst and stopping power, and ignoring weather, smoke and other accuracy penalties
        // (vanilla's ignoresAccuracyMaluses: the weather range cap, the weather accuracy factor and blind smoke).
        // Like damage effects, these only work for a pawn the item is active for, and reveal when a shot lands.
        public List<WeaponPropertyRange> weaponProperties;
        public bool ignoreAccuracyMaluses;

        public bool HasWeaponEffect => !weaponProperties.NullOrEmpty() || ignoreAccuracyMaluses;

        // Hediffs the wielder or wearer has while the item is equipped, added to the brain like vanilla's
        // equippedHediffs and removed on unequip unless other active gear also gives them
        public List<HediffDef> equippedHediffs;

        // Weapons: a memory the wielder gets on each kill. Shorthand for a Kill trigger giving that thought: it is
        // turned into one in ResolveReferences. Weapon-only on purpose (it's the weapon doing the killing, as with
        // vanilla's kill thoughts); write a Kill trigger out in full to put it on apparel.
        public ThoughtDef killThought;
        // Which kills give killThought (the shorthand trigger's 'victims')
        public EntadKillVictims killVictims = EntadKillVictims.Any;

        // Persona weapons (EntadPersona.cs). A persona trait makes the weapon bond to the first pawn who equips it,
        // as vanilla's persona weapons do: one bonded weapon per pawn, nobody else can equip it, and the bond ends
        // when that pawn dies.
        public bool persona;
        // Freewielder: keeps the persona's other effects but the weapon never bonds
        public bool neverBond;
        // A persona tells whoever looks at it what it can do, so a weapon with one starts with every trait revealed.
        // Secretive (needs a persona) turns that off: its other traits reveal through use like any entad's.
        public bool secretive;
        // Effects of the bond (the item needs a persona trait). Bonded hediffs last while the bond does, held or not.
        public List<HediffDef> bondedHediffs;
        // A situational thought active while bonded (worker ThoughtWorker_WeaponTraitBonded, as vanilla's)
        public ThoughtDef bondedThought;
        // A memory (class Thought_WeaponTrait) given when the bonded pawn wields another weapon, as vanilla's Jealous
        public ThoughtDef otherWeaponThought;
        // A situational thought (worker ThoughtWorker_WeaponTraitKillNeed) active once the weapon has gone this many
        // days without a kill in its bonded pawn's hands, as vanilla's kill thirst
        public ThoughtDef killThirstThought;
        public float killThirstDays = 20f;
        // Never picked by random generation: only added by name (API, dev tools)
        public bool neverRandom;

        public bool HasBondEffect => !bondedHediffs.NullOrEmpty() || bondedThought != null || otherWeaponThought != null || killThirstThought != null;

        // Portable space (EntadPortableSpace.cs), worn apparel or a wielded weapon: the item can lift a rectangle of the map (buildings,
        // items, plants, floors, built roofs) into itself and set it down elsewhere. Width and height are rolled
        // separately in this range and multiplied by the rarity scale, then rounded. 0..0 means unused.
        public FloatRange portableSpace = new FloatRange(0f, 0f);

        public bool HasPortableSpace => portableSpace.max > 0f;

        // Smallest and largest side length at a rarity (at least 1)
        public int PortableSideMin(EntadRarity at) => System.Math.Max(1, UnityEngine.Mathf.RoundToInt(portableSpace.min * RarityScale(at)));
        public int PortableSideMax(EntadRarity at) => System.Math.Max(1, UnityEngine.Mathf.RoundToInt(portableSpace.max * RarityScale(at)));

        // "When X happens, do Y" (EntadTriggers.cs). Use triggers are for furniture; every other event is for worn or
        // wielded gear, so one trait can't mix the two.
        public List<EntadTrigger> triggers;

        public bool HasGearTrigger => triggers != null && triggers.Any(t => t.on != EntadTriggerEvent.Use);
        public bool HasUseTrigger => triggers != null && triggers.Any(t => t.on == EntadTriggerEvent.Use);

        public override void ResolveReferences()
        {
            base.ResolveReferences();
            if (killThought != null && (triggers == null || !triggers.Any(t => t.on == EntadTriggerEvent.Kill && t.thought == killThought)))
            {
                if (triggers == null) triggers = new List<EntadTrigger>();
                triggers.Add(new EntadTrigger { on = EntadTriggerEvent.Kill, thought = killThought, victims = killVictims });
            }
        }

        // Allow this trait on items where only some of its stats mean anything (a trait with both melee and ranged
        // stats on a melee weapon, say). Stats that don't apply to the item are skipped and not shown. Off by
        // default: a trait normally needs every one of its stats to apply.
        public bool partialStats;

        // Fuel for buildings with a refuelable component (campfires, generators...). By default these are
        // accepted in addition to the building's normal fuel; with replaceFuel only these are accepted.
        public List<ThingDef> fuelTypes;
        // Every thing in these categories is also accepted, including modded things added to them (e.g. all stone blocks)
        public List<ThingCategoryDef> fuelCategories;

        private List<ThingDef> allFuelTypes;

        // fuelTypes plus everything in fuelCategories, resolved on first use because categories fill up after loading
        public List<ThingDef> AllFuelTypes
        {
            get
            {
                if (allFuelTypes == null)
                {
                    var set = new HashSet<ThingDef>();
                    if (fuelTypes != null) foreach (var t in fuelTypes) if (t != null) set.Add(t);
                    if (fuelCategories != null) foreach (var c in fuelCategories) if (c != null) foreach (var t in c.DescendantThingDefs) set.Add(t);
                    allFuelTypes = set.ToList();
                }
                return allFuelTypes;
            }
        }
        public bool replaceFuel;

        // Mood effect: while equipped (weapons/apparel) or after use (furniture) the wearer/user gets this thought.
        // The lifetime in hours is rolled in thoughtHours when the trait is applied.
        public ThoughtDef thought;
        // Alternative to a fixed thought: when the trait is applied, a random existing memory thought whose
        // mood effect lies within this range is picked. A range of 0..0 means unused.
        public FloatRange thoughtMoodRange = new FloatRange(0f, 0f);

        public bool HasMoodRange => thought == null && (thoughtMoodRange.min != 0f || thoughtMoodRange.max != 0f);
        public bool HasMood => thought != null || HasMoodRange;
        public FloatRange thoughtHours = new FloatRange(1f, 1f);

        // Table effects on meals eaten off this furniture. Nutrition is multiplied by a factor rolled in
        // mealNutritionFactor; mealThought is added to the thoughts the eater gets from the meal (e.g. a fine meal).
        public FloatRange mealNutritionFactor = new FloatRange(1f, 1f);
        public ThoughtDef mealThought;

        // Shifts the quality of meals eaten here along simple -> fine -> lavish (e.g. +1: simple becomes fine,
        // -1: lavish becomes fine). Applied to the thoughts the meal gives, clamped to the ends of that ladder.
        public int mealQualityOffset;

        public bool HasMealEffect => mealThought != null || mealQualityOffset != 0 || mealNutritionFactor.min != 1f || mealNutritionFactor.max != 1f;

        // Weapons and apparel grant these to the pawn using them; furniture offers them as right-click
        // actions that a pawn walks over and performs (with a per-item cooldown)
        public List<AbilityDef> abilities;

        // Optional limits on the abilities above. Cooldown (ticks) overrides the ability's own cooldown. With charges,
        // the ability can be used that many times in a row, and gains a charge back at the end of each cooldown.
        public int abilityCharges;
        public int abilityCooldownTicks;
        // How long using the ability takes (ticks). Equipped abilities use it as their warmup; furniture abilities make
        // the pawn work at the furniture for this long. 0 keeps the ability's own casting time.
        public int abilityCastTicks;

        // Reloadable abilities (weapons and apparel): with an ammo def, the abilityCharges never come back on their own.
        // The pawn reloads them with abilityAmmoPerCharge of the ammo per charge, from the right-click menu on the
        // ammo or automatically once they run out, like vanilla reloadable gear. Charges are kept on the item, so
        // taking it off and putting it back on doesn't refill it. No cooldown between uses in this mode.
        public ThingDef abilityAmmo;
        public int abilityAmmoPerCharge = 1;
        public int abilityReloadTicks = 60;

        public bool IsReloadable => abilityAmmo != null && abilityCharges > 0 && !abilities.NullOrEmpty();

        // Traits that can never share an item with this one (symmetric: listing it on either def is enough)
        public List<EntadTraitDef> exclusiveWith;

        // Tags shared with other traits that mean "only one of us per item"
        public List<string> exclusivityTags;

        // By default two traits that affect the same thing (a stat, meal nutrition, meal quality, an ability)
        // can't share an item. Set to true to opt this trait out of that implicit check; explicit
        // exclusiveWith / exclusivityTags still apply.
        public bool allowOverlap;

        public bool ConflictsWith(EntadTraitDef other)
        {
            if (other == null) return false;
            if (other == this) return true;
            if (exclusiveWith != null && exclusiveWith.Contains(other)) return true;
            if (other.exclusiveWith != null && other.exclusiveWith.Contains(this)) return true;
            if (exclusivityTags != null && other.exclusivityTags != null && exclusivityTags.Any(t => other.exclusivityTags.Contains(t))) return true;
            if (allowOverlap || other.allowOverlap) return false;

            if (mealNutritionFactor.min != 1f || mealNutritionFactor.max != 1f)
                if (other.mealNutritionFactor.min != 1f || other.mealNutritionFactor.max != 1f) return true;
            if (mealQualityOffset != 0 && other.mealQualityOffset != 0) return true;
            if (!AllFuelTypes.NullOrEmpty() && !other.AllFuelTypes.NullOrEmpty()) return true;
            if (changeDamageType != null && other.changeDamageType != null) return true;
            if (!buildingFactors.NullOrEmpty() && !other.buildingFactors.NullOrEmpty()
                && buildingFactors.Any(a => other.buildingFactors.Any(b => a.property == b.property))) return true;
            if (!abilities.NullOrEmpty() && !other.abilities.NullOrEmpty() && abilities.Any(a => other.abilities.Contains(a))) return true;
            if (thought != null && thought == other.thought) return true;
            if (HasPortableSpace && other.HasPortableSpace) return true;

            foreach (var a in AllRanges())
            {
                if (a.stat == null) continue;
                foreach (var b in other.AllRanges())
                    if (a.stat == b.stat) return true;
            }
            return false;
        }

        public static float MoodEffectOf(ThoughtDef t) => t.stages[0].baseMoodEffect;

        // Plain memory thoughts with a single stage, usable on any pawn without extra context
        private List<ThoughtDef> moodCandidates;

        public List<ThoughtDef> MoodCandidates()
        {
            if (!HasMoodRange) return new List<ThoughtDef>();
            return moodCandidates ?? (moodCandidates = DefDatabase<ThoughtDef>.AllDefsListForReading.Where(t =>
                // ThoughtClass (the property), not the raw thoughtClass field: most memory defs never set <thoughtClass>,
                // so the field is null and the old field check left nearly every vanilla and modded memory out.
                // Every loaded mod's ThoughtDefs are included: this runs on first use, after all defs are loaded.
                // Memories of events that never happened ("colonist died") are deliberate: the item plants false
                // memories. What's left out is what can't work or can't read right without context: other thought
                // classes (social memories need another pawn), several stages, an invisible stage, required traits
                // or genes, hediffs, a gender, an expectation level or a non-adult life stage (TryGainMemory silently
                // refuses those for anyone who doesn't match), chained follow-ups, and text with placeholders.
                t.IsMemory && t.ThoughtClass == typeof(Thought_Memory) && t.stages != null && t.stages.Count == 1
                && t.stages[0] != null && t.stages[0].visible && t.requiredTraits.NullOrEmpty() && t.requiredGenes.NullOrEmpty()
                && t.requiredHediffs.NullOrEmpty() && t.gender == Gender.None && t.minExpectation == null
                && t.developmentalStageFilter.Has(DevelopmentalStage.Adult)
                && t.nextThought == null && t.thoughtToMake == null && t.stages[0].baseMoodEffect != 0f
                && IsSelfContained(t.stages[0])
                && thoughtMoodRange.Includes(MoodEffectOf(t))).ToList());
        }

        // A label and description that read on their own. Some memories expect context the item can't give: a
        // placeholder for another pawn or a precept ({0}, [PAWN_nameDef]) would show up literally.
        private static bool IsSelfContained(ThoughtStage s)
        {
            return !s.label.NullOrEmpty() && !s.description.NullOrEmpty()
                && s.label.IndexOfAny(PlaceholderChars) < 0 && s.description.IndexOfAny(PlaceholderChars) < 0;
        }

        private static readonly char[] PlaceholderChars = { '{', '[' };


        // Cached: IsRevealed/AnyHidden read this on stat and draw paths, and computing it allocates AllRanges
        // iterators. Everything it reads is def data fixed after loading (EntadAbilityPrep swaps ability entries
        // but never empties the list), and it is first read in play, after every reference has resolved.
        private EntadEffectKind? effectKinds;

        public EntadEffectKind EffectKinds => effectKinds ?? (effectKinds = ComputeEffectKinds()).Value;

        private EntadEffectKind ComputeEffectKinds()
        {
            {
                EntadEffectKind kinds = EntadEffectKind.None;
                if (AllRanges().Any()) kinds |= EntadEffectKind.Stat;
                if (HasMood) kinds |= EntadEffectKind.Mood;
                if (HasMealEffect) kinds |= EntadEffectKind.Meal;
                if (!buildingFactors.NullOrEmpty()) kinds |= EntadEffectKind.Building;
                if (!AllFuelTypes.NullOrEmpty()) kinds |= EntadEffectKind.Fuel;
                if (HasDamageEffect || HasWeaponEffect) kinds |= EntadEffectKind.Damage;
                if (!triggers.NullOrEmpty()) kinds |= EntadEffectKind.Trigger;
                if (!equippedHediffs.NullOrEmpty()) kinds |= EntadEffectKind.Hediff;
                if (!abilities.NullOrEmpty()) kinds |= EntadEffectKind.Ability;
                if (persona || neverBond || secretive || HasBondEffect) kinds |= EntadEffectKind.Bond;
                if (HasPortableSpace) kinds |= EntadEffectKind.Space;
                return kinds;
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;
            foreach (var r in Ranges(statOffsets))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat offset range";
            foreach (var r in Ranges(statFactors))
                if (r.stat == null || r.min > r.max) yield return $"{defName}: invalid stat factor range";
            if (thought != null && !thought.IsMemory) yield return $"{defName}: thought {thought.defName} is not a memory thought";
            if (buildingFactors != null)
                foreach (var b in buildingFactors)
                    if (b.min > b.max || b.min < 0f) yield return $"{defName}: invalid building factor range for {b.property}";
            if (extraDamage != null && extraDamage.Any(e => e.damageType == null || e.min > e.max)) yield return $"{defName}: invalid extraDamage entry";
            if (fuelCategories != null && fuelCategories.Any(c => c == null)) yield return $"{defName}: fuelCategories contains an unknown def";
            if (fuelTypes != null && fuelTypes.Any(f => f == null)) yield return $"{defName}: fuelTypes contains an unknown def";
            if (mealNutritionFactor.min > mealNutritionFactor.max) yield return $"{defName}: mealNutritionFactor min is greater than max";
            if (thought != null && (thoughtMoodRange.min != 0f || thoughtMoodRange.max != 0f)) yield return $"{defName}: specify either thought or thoughtMoodRange, not both";
            if (thoughtMoodRange.min > thoughtMoodRange.max) yield return $"{defName}: thoughtMoodRange min is greater than max";
            if (HasMood && thoughtHours.min > thoughtHours.max) yield return $"{defName}: thoughtHours min is greater than max";
            if (weaponProperties != null && weaponProperties.Any(w => w.min > w.max)) yield return $"{defName}: invalid weaponProperties range";
            // (killThought is checked as the Kill trigger it becomes)
            if (killVictims != EntadKillVictims.Any && (killThought == null || (triggers != null && triggers.Any(t =>
                    t.on == EntadTriggerEvent.Kill && t.thought == killThought && t.victims != killVictims))))
                yield return $"{defName}: killVictims only applies to killThought; set 'victims' on a written-out Kill trigger instead";
            if (triggers != null)
                foreach (var t in triggers)
                    foreach (var e in t.ConfigErrors(defName)) yield return e;
            if (HasGearTrigger && HasUseTrigger) yield return $"{defName}: Use triggers (furniture) can't be mixed with gear triggers on one trait";
            if (equippedHediffs != null && equippedHediffs.Any(h => h == null)) yield return $"{defName}: equippedHediffs contains an unknown def";
            if (abilityAmmo != null && (abilityCharges <= 0 || abilities.NullOrEmpty())) yield return $"{defName}: abilityAmmo needs abilities and abilityCharges > 0";
            if (abilityAmmo != null && abilityAmmoPerCharge <= 0) yield return $"{defName}: abilityAmmoPerCharge must be at least 1";
            if (abilityAmmo != null && abilityReloadTicks < 0) yield return $"{defName}: abilityReloadTicks can't be negative";
            if (bondedHediffs != null && bondedHediffs.Any(h => h == null)) yield return $"{defName}: bondedHediffs contains an unknown def";
            if (otherWeaponThought != null && (!otherWeaponThought.IsMemory || !typeof(Thought_WeaponTrait).IsAssignableFrom(otherWeaponThought.ThoughtClass)))
                yield return $"{defName}: otherWeaponThought {otherWeaponThought.defName} must be a memory with a Thought_WeaponTrait class";
            if (bondedThought != null && !typeof(ThoughtWorker_WeaponTraitBonded).IsAssignableFrom(bondedThought.workerClass))
                yield return $"{defName}: bondedThought {bondedThought.defName} needs workerClass ThoughtWorker_WeaponTraitBonded";
            if (killThirstThought != null && !typeof(ThoughtWorker_WeaponTraitKillNeed).IsAssignableFrom(killThirstThought.workerClass))
                yield return $"{defName}: killThirstThought {killThirstThought.defName} needs workerClass ThoughtWorker_WeaponTraitKillNeed";
            if (killThirstThought != null && killThirstDays <= 0f) yield return $"{defName}: killThirstDays must be above 0";
            if (persona && (neverBond || HasBondEffect)) yield return $"{defName}: persona goes on its own trait; neverBond and bond effects are separate traits that need one";
            if (neverBond && HasBondEffect) yield return $"{defName}: neverBond can't be combined with bond effects";
            if (!float.IsNaN(points) && points == 0f) yield return $"{defName}: points 0 lets generation add it for free until the trait cap; use a small positive value";
            if (secretive && persona) yield return $"{defName}: secretive goes on its own trait, alongside a persona trait";
            if (portableSpace.min > portableSpace.max || portableSpace.min < 0f) yield return $"{defName}: invalid portableSpace range";
            if (abilityAmmo != null && abilityCooldownTicks > 0) yield return $"{defName}: abilityCooldownTicks is ignored when abilityAmmo is set (reloadable abilities have no cooldown)";
        }

        // Categories restrict by item type; stats must additionally be meaningful for the item
        public bool CanApplyTo(Thing thing)
        {
            ThingDef td = thing?.def;
            if (td == null) return false;

            var existing = thing.TryGetComp<CompEntad>();
            if (existing != null)
                foreach (var m in existing.activeTraits)
                    if (m.def == this || ConflictsWith(m.def)) return false;

            if ((persona || neverBond || secretive || HasBondEffect) && !td.IsWeapon) return false;
            if (secretive && (existing == null || !existing.activeTraits.Any(m => m.def.persona))) return false;
            // A vanilla persona weapon's own comp takes the pawn's bonded-weapon slot first, so ours would never bond
            if (persona && td.comps != null && td.comps.Any(c => c.compClass != null && typeof(CompBladelinkWeapon).IsAssignableFrom(c.compClass))) return false;
            // Bond effects and freewielder only go on a weapon that already has a persona, and never together
            if (neverBond || HasBondEffect)
            {
                if (existing == null || !existing.activeTraits.Any(m => m.def.persona)) return false;
                if (neverBond && existing.activeTraits.Any(m => m.def.HasBondEffect)) return false;
                if (HasBondEffect && existing.activeTraits.Any(m => m.def.neverBond)) return false;
            }
            if (HasMoodRange && MoodCandidates().Count == 0) return false;
            // Furniture moods need a pawn to use the building (abilities work from the right-click menu on any furniture)
            if (HasMood && td.building != null && !EntadUtility.IsPawnUsable(td)) return false;
            if (HasDamageEffect && (!td.IsWeapon || IsAreaWeapon(td))) return false;
            if (ignoreAccuracyMaluses && !td.IsRangedWeapon) return false;
            if (!weaponProperties.NullOrEmpty() && weaponProperties.Any(w => !w.AppliesTo(td))) return false;
            if (killThought != null && !td.IsWeapon) return false;
            if (HasGearTrigger && !td.IsWeapon && !td.IsApparel) return false;
            if (HasUseTrigger && (td.building == null || !EntadUtility.IsPawnUsable(td)
                || triggers.Any(t => t.on == EntadTriggerEvent.Use && (t.use & UseKindsOf(td)) == 0))) return false;
            // Reloading works on worn and wielded gear only; furniture abilities have their own charge cooldown
            if (IsReloadable && !td.IsWeapon && !td.IsApparel) return false;
            if (!equippedHediffs.NullOrEmpty() && !td.IsWeapon && !td.IsApparel) return false;
            if (HasMealEffect && td.surfaceType != SurfaceType.Eat) return false;
            // Carried gear only (worn apparel or a wielded weapon): the holder uses the space from their gizmos
            if (HasPortableSpace && !td.IsApparel && !td.IsWeapon) return false;
            if (!buildingFactors.NullOrEmpty() && buildingFactors.Any(b => !b.AppliesTo(td))) return false;
            if (!AllFuelTypes.NullOrEmpty() && !(td.comps != null && td.comps.Any(c => c is CompProperties_Refuelable))) return false;

            if (!categories.NullOrEmpty())
            {
                bool match = false;
                foreach (string c in categories)
                {
                    if ((EntadUtility.KindOf(td) & EntadUtility.ParseKind(c)) != EntadItemKind.None)
                    { match = true; break; }
                }
                if (!match) return false;
            }

            foreach (var r in Ranges(statFactors))
                if (r.stat != null && IsWearerStat(r.stat) && !(td.statBases?.Any(m => m.stat == r.stat) ?? false)) return false;

            bool anyStat = false, anyApplies = false;
            foreach (var r in AllRanges())
            {
                if (r.stat == null) continue;
                anyStat = true;
                if (r.stat == StatDefOf.MarketValue || StatAppliesTo(r.stat, thing)) { anyApplies = true; continue; }
                if (!partialStats) return false;
            }
            // Partial: at least one stat has to mean something, unless the trait has other effects that already passed
            if (partialStats && anyStat && !anyApplies && EffectKinds == EntadEffectKind.Stat) return false;
            return true;
        }

        // The kinds of use the job-end hook can report for this building (EntadMoods): a Sleep trigger on a workbench
        // would never fire
        public static EntadUseKind UseKindsOf(ThingDef td)
        {
            var b = td.building;
            if (b == null) return 0;
            EntadUseKind k = EntadUseKind.Other;
            if (td.IsBed) k |= EntadUseKind.Sleep;
            if (b.isSittable || td.surfaceType == SurfaceType.Eat) k |= EntadUseKind.Eat;
            // Work is a bill, research, a deep drill or a scanner (the jobs the hook reports as Work), or a seat at one
            if (b.isSittable || td.IsWorkTable || !td.AllRecipes.NullOrEmpty() || typeof(Building_ResearchBench).IsAssignableFrom(td.thingClass)
                || (td.comps != null && td.comps.Any(c => c.compClass != null
                    && (typeof(CompDeepDrill).IsAssignableFrom(c.compClass) || typeof(CompScanner).IsAssignableFrom(c.compClass)))))
                k |= EntadUseKind.Work;
            if (b.isSittable || b.joyKind != null) k |= EntadUseKind.Recreation;
            return k;
        }

        // Whether a stat of this trait works on the thing. Always true for an ordinary trait (CanApplyTo already
        // required every stat to apply). Used to hide a partialStats trait's other stats on the card. The values
        // themselves still apply, but a stat that doesn't apply to the item is one the game never asks it for.
        public bool UsesStat(StatDef stat, Thing thing) => !partialStats || stat == StatDefOf.MarketValue || StatAppliesTo(stat, thing);

        private static bool StatAppliesTo(StatDef stat, Thing thing)
        {
            ThingDef td = thing.def;
            if (td.IsWeapon)
            {
                if (IsRangedOnlyStat(stat) && !td.IsRangedWeapon) return false;
                if (IsMeleeOnlyStat(stat) && !td.IsMeleeWeapon) return false;
            }
            if (td.statBases != null && td.statBases.Any(m => m.stat == stat)) return true;
            // Weapon multipliers (e.g. RangedWeapon_DamageMultiplier) have a default value and are never listed in
            // a weapon's statBases, but the game reads them from the weapon, so allow them on the matching weapon type
            if (IsWearerStat(stat) && (td.IsWeapon || td.IsApparel)) return true;
            if (stat.defName.StartsWith("RangedWeapon_")) return td.IsRangedWeapon;
            if (stat.defName.StartsWith("MeleeWeapon_")) return td.IsMeleeWeapon;
            return stat.showIfUndefined && stat.Worker.ShouldShowFor(StatRequest.For(thing)) && !stat.Worker.IsDisabledFor(thing) && StatDefHasEquippedOrBase(stat, td);
        }

        public static bool IsRangedOnlyStat(StatDef stat) =>
            stat.category == StatCategoryDefOf.Weapon_Ranged || stat.defName.StartsWith("RangedWeapon_") || stat.defName.StartsWith("Accuracy");

        public static bool IsMeleeOnlyStat(StatDef stat) =>
            stat.category == StatCategoryDefOf.Weapon_Melee || stat.defName.StartsWith("MeleeWeapon_");

        // Traits made for weapons (weapon-only categories, damage effects or weapon stats) rather than general ones
        private int weaponSpecific = -1;

        public bool IsWeaponSpecific
        {
            get
            {
                if (weaponSpecific < 0)
                {
                    bool result = HasDamageEffect || HasWeaponEffect || killThought != null || persona || neverBond || secretive || HasBondEffect
                        || (!categories.NullOrEmpty() && categories.All(c => EntadUtility.ParseKind(c) == EntadItemKind.Weapon))
                        || AllRanges().Any(r => r.stat != null && (IsRangedOnlyStat(r.stat) || IsMeleeOnlyStat(r.stat)));
                    weaponSpecific = result ? 1 : 0;
                }
                return weaponSpecific == 1;
            }
        }

        private static readonly HashSet<string> WearerCategories = new HashSet<string>
        {
            "EquippedStatOffsets", "BasicsPawn", "PawnWork", "PawnCombat", "PawnSocial", "PawnMisc",
            "PawnHealth", "PawnFood", "PawnResistances", "PawnPsyfocus", "Meditation"
        };

        // Stats of the pawn carrying a weapon or apparel. Entad traits add to these as equipped stat offsets.
        public static bool IsWearerStat(StatDef stat) => stat.category != null && WearerCategories.Contains(stat.category.defName);

        // Stats such as equipped offsets work on any equippable item
        private static bool StatDefHasEquippedOrBase(StatDef stat, ThingDef td)
        {
            return stat.category == StatCategoryDefOf.EquippedStatOffsets && (td.IsWeapon || td.IsApparel);
        }

        public IEnumerable<StatTraitRange> AllRanges()
        {
            foreach (var r in Ranges(statOffsets)) yield return r;
            foreach (var r in Ranges(statFactors)) yield return r;
        }

        private static IEnumerable<StatTraitRange> Ranges(List<StatTraitRange> l)
        {
            if (l != null) foreach (var r in l) yield return r;
        }
    }
}
