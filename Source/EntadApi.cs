using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // Limits which entad traits may be applied. Every set left null/empty imposes no restriction.
    public class EntadTraitFilter
    {
        // Trait must change at least one of these stats (when non-empty)
        public HashSet<StatDef> statWhitelist = new HashSet<StatDef>();
        // Trait must not change any of these stats
        public HashSet<StatDef> statBlacklist = new HashSet<StatDef>();
        public HashSet<EntadTraitDef> defWhitelist = new HashSet<EntadTraitDef>();
        public HashSet<EntadTraitDef> defBlacklist = new HashSet<EntadTraitDef>();
        // Allowed rarities (all when empty)
        public HashSet<EntadRarity> rarities = new HashSet<EntadRarity>();
        // Only traits declaring one of these categories (all when empty)
        public HashSet<string> categories = new HashSet<string>();
        // Trait must have at least one of these kinds of effect (all when None)
        public EntadEffectKind effectKinds = EntadEffectKind.None;
        // Additional arbitrary check
        public Predicate<EntadTraitDef> predicate;

        public bool Allows(EntadTraitDef def)
        {
            if (defWhitelist.Count > 0 && !defWhitelist.Contains(def)) return false;
            if (defBlacklist.Contains(def)) return false;
            if (rarities.Count > 0 && !rarities.Any(def.CanAppearAt)) return false;
            if (categories.Count > 0 && (def.categories == null || !def.categories.Any(categories.Contains))) return false;

            if (effectKinds != EntadEffectKind.None && (def.EffectKinds & effectKinds) == EntadEffectKind.None) return false;

            var stats = def.AllRanges().Select(r => r.stat).Where(s => s != null).ToList();
            if (statBlacklist.Count > 0 && stats.Any(statBlacklist.Contains)) return false;
            if (statWhitelist.Count > 0 && !stats.Any(statWhitelist.Contains)) return false;

            return predicate == null || predicate(def);
        }
    }

    // Options for choosing and applying traits to an existing thing
    public class EntadApplyRequest
    {
        public EntadTraitFilter traitFilter = new EntadTraitFilter();
        public EntadRarityChances rarityChances = EntadRarityChances.Default;
        // Number of traits to add (inclusive range)
        public IntRange traitCount = new IntRange(1, 1);
    }

    // Options for generating a brand new entad item
    public class EntadItemRequest : EntadApplyRequest
    {
        public EntadItemKind kinds = EntadItemKind.All;
        public HashSet<ThingDef> thingWhitelist = new HashSet<ThingDef>();
        public HashSet<ThingDef> thingBlacklist = new HashSet<ThingDef>();
        public Predicate<ThingDef> thingPredicate;
        // Forced stuff; chosen randomly for stuff-made items when null
        public ThingDef stuff;
        // Forced quality, applied to items that have a quality
        public QualityCategory? quality;
    }

    // Entry points intended for use by other mods
    public static class EntadApi
    {
        public static List<EntadTraitDef> GetApplicableTraits(Thing thing, EntadTraitFilter filter = null)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return new List<EntadTraitDef>();

            return DefDatabase<EntadTraitDef>.AllDefsListForReading
                .Where(d => !EntadSettings.IsDisabled(d) && !comp.HasTrait(d) && d.CanApplyTo(thing) && (filter == null || filter.Allows(d)))
                .ToList();
        }

        // Applies random traits to the thing; returns the ones added (possibly fewer than requested)
        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, EntadApplyRequest request = null)
        {
            request = request ?? new EntadApplyRequest();
            var added = new List<EntadTraitDef>();
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return added;

            int count = request.traitCount.RandomInRange;
            for (int i = 0; i < count; i++)
            {
                var candidates = GetApplicableTraits(thing, request.traitFilter);
                System.Func<EntadTraitDef, float> weight = null;
                if (thing.def.IsWeapon && EntadSettings.WeaponSpecificWeight > 1f)
                    weight = d => d.IsWeaponSpecific ? EntadSettings.WeaponSpecificWeight : 1f;
                var pick = (request.rarityChances ?? EntadRarityChances.Default).Pick(candidates, weight, request.traitFilter?.rarities, out EntadRarity rolled);
                if (pick == null) break;
                if (!comp.AddTrait(pick, rolled)) break;
                added.Add(pick);
            }
            return added;
        }

        public static List<ThingDef> GetCandidateItemDefs(EntadItemRequest request)
        {
            request = request ?? new EntadItemRequest();
            return DefDatabase<ThingDef>.AllDefsListForReading.Where(d =>
                EntadUtility.IsEntadCompatible(d)
                && (EntadUtility.KindOf(d) & request.kinds) != EntadItemKind.None
                && (request.thingWhitelist.Count == 0 || request.thingWhitelist.Contains(d))
                && !request.thingBlacklist.Contains(d)
                && (request.thingPredicate == null || request.thingPredicate(d))).ToList();
        }

        // Generates an unspawned item with entad traits, or null if no item/trait combination fits the request.
        // Item defs are tried in random order until one can receive at least one trait.
        public static Thing GenerateEntadItem(int traitCount, EntadItemRequest request = null)
        {
            request = request ?? new EntadItemRequest();
            request.traitCount = new IntRange(traitCount, traitCount);
            return GenerateEntadItem(request);
        }

        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, int traitCount, EntadApplyRequest request = null)
        {
            request = request ?? new EntadApplyRequest();
            request.traitCount = new IntRange(traitCount, traitCount);
            return ApplyRandomTraits(thing, request);
        }

        public static Thing GenerateEntadItem(EntadItemRequest request = null)
        {
            request = request ?? new EntadItemRequest();
            foreach (ThingDef def in GetCandidateItemDefs(request).InRandomOrder())
            {
                ThingDef stuff = request.stuff;
                if (stuff == null && def.MadeFromStuff) stuff = GenStuff.RandomStuffFor(def);

                Thing thing = ThingMaker.MakeThing(def, stuff);
                var quality = thing.TryGetComp<CompQuality>();
                if (quality != null && request.quality.HasValue)
                    quality.SetQuality(request.quality.Value, ArtGenerationContext.Outsider);

                if (ApplyRandomTraits(thing, request).Count > 0) return thing;
                thing.Destroy();
            }
            return null;
        }
    }
}
