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

    /// <summary>Entry points intended for use by other mods.</summary>
    /// <remarks>
    /// Request objects passed in are never modified, so a mod can keep one and reuse it.
    /// Traits the player disabled in the settings are never picked.
    /// </remarks>
    public static class EntadApi
    {
        /// <summary>Every trait that could still be added to <paramref name="thing"/>: enabled, not already on it, valid
        /// for its def and allowed by <paramref name="filter"/>. Empty when the thing cannot be an entad.</summary>
        public static List<EntadTraitDef> GetApplicableTraits(Thing thing, EntadTraitFilter filter = null)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return new List<EntadTraitDef>();

            return DefDatabase<EntadTraitDef>.AllDefsListForReading
                .Where(d => !EntadSettings.IsDisabled(d) && !comp.HasTrait(d) && d.CanApplyTo(thing) && (filter == null || filter.Allows(d)))
                .ToList();
        }

        /// <summary>Adds random traits to <paramref name="thing"/>, as many as <c>request.traitCount</c> rolls.
        /// A vanilla unique or persona weapon only gets random traits at the player's "unique and persona weapons"
        /// chance (0% by default); adding a trait by name with <see cref="CompEntad.AddTrait"/> is not limited.</summary>
        /// <returns>The traits added; fewer than requested when the candidates run out, and none when the unique
        /// weapon chance fails.</returns>
        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, EntadApplyRequest request = null)
        {
            request = request ?? new EntadApplyRequest();
            if (!PassesUniqueWeaponChance(thing?.def)) return new List<EntadTraitDef>();
            return ApplyRandomTraits(thing, request, request.traitCount.RandomInRange);
        }

        // The dev tools' "add random traits": skips the unique weapon chance, which a developer pointing at a weapon
        // means to override (GenerateEntadItem's dev action still goes through it)
        internal static List<EntadTraitDef> ApplyRandomTraitsIgnoringUniqueChance(Thing thing)
        {
            var request = new EntadApplyRequest();
            return ApplyRandomTraits(thing, request, request.traitCount.RandomInRange);
        }

        /// <summary>Vanilla unique and persona weapons only roll random traits at the settings' chance (0% by default).
        /// Traits added by name (<see cref="CompEntad.AddTrait"/>) are not affected.</summary>
        private static bool PassesUniqueWeaponChance(ThingDef def) =>
            !EntadUtility.IsUniqueWeapon(def) || Rand.Chance(EntadSettings.UniqueWeaponChance);

        /// <summary>As <see cref="ApplyRandomTraits(Thing, EntadApplyRequest)"/>, with an exact trait count that
        /// overrides <c>request.traitCount</c>. The unique weapon chance applies here too.</summary>
        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, int traitCount, EntadApplyRequest request = null)
        {
            if (!PassesUniqueWeaponChance(thing?.def)) return new List<EntadTraitDef>();
            return ApplyRandomTraits(thing, request ?? new EntadApplyRequest(), traitCount);
        }

        private static List<EntadTraitDef> ApplyRandomTraits(Thing thing, EntadApplyRequest request, int count)
        {
            var added = new List<EntadTraitDef>();
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return added;

            for (int i = 0; i < count; i++)
            {
                var candidates = GetApplicableTraits(thing, request.traitFilter);
                candidates.RemoveAll(d => d.neverRandom);
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

        /// <summary>Binds <paramref name="thing"/> to <paramref name="pawns"/>, in addition to any pawns it is already
        /// bound to. A bound entad works as an ordinary item for anyone, but its traits only work for the bound pawns
        /// and their descendants. Item-level effects (durability, market value, building properties) apply to all.</summary>
        /// <returns>False when the thing is not an entad.</returns>
        public static bool Bind(Thing thing, IEnumerable<Pawn> pawns)
        {
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return false;
            comp.Bind(pawns);
            return true;
        }

        /// <summary>Removes every binding, so the traits work for anyone again.</summary>
        public static void Unbind(Thing thing) => thing?.TryGetComp<CompEntad>()?.Unbind();

        /// <summary>The pawns <paramref name="thing"/> is bound to; empty when it is unbound.</summary>
        public static IReadOnlyList<Pawn> GetBoundPawns(Thing thing) =>
            thing?.TryGetComp<CompEntad>()?.BoundPawns ?? (IReadOnlyList<Pawn>)Array.Empty<Pawn>();

        /// <summary>Whether the entad traits on <paramref name="thing"/> work for <paramref name="pawn"/>: true for an
        /// unbound item, or for a bound pawn or one of their descendants.</summary>
        public static bool TraitsActiveFor(Thing thing, Pawn pawn) => thing?.TryGetComp<CompEntad>()?.ActiveFor(pawn) ?? true;

        /// <summary>Item defs that can become entads and match the request's kinds, lists and predicate.</summary>
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

        /// <summary>As <see cref="GenerateEntadItem(EntadItemRequest)"/>, with an exact trait count that overrides
        /// <c>request.traitCount</c>.</summary>
        public static Thing GenerateEntadItem(int traitCount, EntadItemRequest request = null)
        {
            return GenerateEntadItem(request ?? new EntadItemRequest(), traitCount);
        }

        /// <summary>Generates an unspawned item with entad traits. Item defs are tried in random order until one can
        /// receive at least one trait. Vanilla unique and persona weapon defs are skipped unless the player's "unique
        /// and persona weapons" chance passes for them.</summary>
        /// <returns>The item, not yet spawned, or null when no item and trait combination fits the request.</returns>
        public static Thing GenerateEntadItem(EntadItemRequest request = null)
        {
            request = request ?? new EntadItemRequest();
            return GenerateEntadItem(request, request.traitCount.RandomInRange);
        }

        private static Thing GenerateEntadItem(EntadItemRequest request, int traitCount)
        {
            foreach (ThingDef def in GetCandidateItemDefs(request).InRandomOrder())
            {
                // Rolled per candidate, so at a low chance unique weapons come up that much less often
                if (!PassesUniqueWeaponChance(def)) continue;
                ThingDef stuff = request.stuff;
                if (stuff == null && def.MadeFromStuff) stuff = GenStuff.RandomStuffFor(def);

                Thing thing = ThingMaker.MakeThing(def, stuff);
                var quality = thing.TryGetComp<CompQuality>();
                if (quality != null && request.quality.HasValue)
                    quality.SetQuality(request.quality.Value, ArtGenerationContext.Outsider);

                // A rejected candidate was never spawned or registered anywhere, so it is simply dropped for the GC.
                // (Thing.Discard only accepts destroyed things and would log a warning for each one.)
                if (ApplyRandomTraits(thing, request, traitCount).Count > 0) return thing;
            }
            return null;
        }
    }
}
