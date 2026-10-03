using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    // Limits which entad modifiers may be applied. Every set left null/empty imposes no restriction.
    public class EntadModifierFilter
    {
        // Modifier must change at least one of these stats (when non-empty)
        public HashSet<StatDef> statWhitelist = new HashSet<StatDef>();
        // Modifier must not change any of these stats
        public HashSet<StatDef> statBlacklist = new HashSet<StatDef>();
        public HashSet<EntadModifierDef> defWhitelist = new HashSet<EntadModifierDef>();
        public HashSet<EntadModifierDef> defBlacklist = new HashSet<EntadModifierDef>();
        // Allowed rarities (all when empty)
        public HashSet<EntadRarity> rarities = new HashSet<EntadRarity>();
        // Only modifiers declaring one of these categories (all when empty)
        public HashSet<string> categories = new HashSet<string>();
        // Modifier must have at least one of these kinds of effect (all when None)
        public EntadEffectKind effectKinds = EntadEffectKind.None;
        // Additional arbitrary check
        public Predicate<EntadModifierDef> predicate;

        public bool Allows(EntadModifierDef def)
        {
            if (defWhitelist.Count > 0 && !defWhitelist.Contains(def)) return false;
            if (defBlacklist.Contains(def)) return false;
            if (rarities.Count > 0 && !rarities.Contains(def.rarity)) return false;
            if (categories.Count > 0 && (def.categories == null || !def.categories.Any(categories.Contains))) return false;

            if (effectKinds != EntadEffectKind.None && (def.EffectKinds & effectKinds) == EntadEffectKind.None) return false;

            var stats = def.AllRanges().Select(r => r.stat).Where(s => s != null).ToList();
            if (statBlacklist.Count > 0 && stats.Any(statBlacklist.Contains)) return false;
            if (statWhitelist.Count > 0 && !stats.Any(statWhitelist.Contains)) return false;

            return predicate == null || predicate(def);
        }
    }

    // Options for choosing and applying modifiers to an existing thing
    public class EntadApplyRequest
    {
        public EntadModifierFilter modifierFilter = new EntadModifierFilter();
        public EntadRarityChances rarityChances = EntadRarityChances.Default;
        // Number of modifiers to add (inclusive range)
        public IntRange modifierCount = new IntRange(1, 1);
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
        public static List<EntadModifierDef> GetApplicableModifiers(Thing thing, EntadModifierFilter filter = null)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return new List<EntadModifierDef>();

            return DefDatabase<EntadModifierDef>.AllDefsListForReading
                .Where(d => !comp.HasModifier(d) && d.CanApplyTo(thing) && (filter == null || filter.Allows(d)))
                .ToList();
        }

        // Applies random modifiers to the thing; returns the ones added (possibly fewer than requested)
        public static List<EntadModifierDef> ApplyRandomModifiers(Thing thing, EntadApplyRequest request = null)
        {
            request = request ?? new EntadApplyRequest();
            var added = new List<EntadModifierDef>();
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return added;

            int count = request.modifierCount.RandomInRange;
            for (int i = 0; i < count; i++)
            {
                var candidates = GetApplicableModifiers(thing, request.modifierFilter);
                System.Func<EntadModifierDef, float> weight = null;
                if (thing.def.IsWeapon && EntadSettings.WeaponSpecificWeight > 1f)
                    weight = d => d.IsWeaponSpecific ? EntadSettings.WeaponSpecificWeight : 1f;
                var pick = (request.rarityChances ?? EntadRarityChances.Default).Pick(candidates, weight);
                if (pick == null) break;
                comp.AddModifier(pick);
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

        // Generates an unspawned item with entad modifiers, or null if no item/modifier combination fits the request.
        // Item defs are tried in random order until one can receive at least one modifier.
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

                if (ApplyRandomModifiers(thing, request).Count > 0) return thing;
                thing.Destroy();
            }
            return null;
        }
    }
}
