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
        // Point target, a minimum: traits are picked until the item's points reach it, and the last one may go over
        // (EntadTraitDef.PointsAt). Null: the settings' target. A trait at its own rarity is worth 1 (Common) to 11
        // (Legendary) by default, so 2-6 is an everyday item.
        public FloatRange? points;
        // How strongly a trait that would overshoot the target is held back: it keeps (points left / its cost) to
        // this power of its weight. 0 = a pure minimum. Null: the settings'.
        public float? overshootStrictness;
        // Most traits to add. Null: the settings' cap.
        public int? maxTraits;
        // Chance the item may take drawbacks (negative-point traits that add to the target), and how many. Null: the settings'.
        public float? drawbackChance;
        public int? maxDrawbacks;
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

        /// <summary>Adds random traits to <paramref name="thing"/> until its points reach a target rolled from
        /// <c>request.points</c>; the last trait may go over it.
        /// A vanilla unique or persona weapon only gets random traits at the player's "unique and persona weapons"
        /// chance (0% by default); adding a trait by name with <see cref="CompEntad.AddTrait"/> is not limited.</summary>
        /// <returns>The traits added; none when the unique weapon chance fails.</returns>
        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, EntadApplyRequest request = null)
        {
            request = request ?? new EntadApplyRequest();
            if (!PassesUniqueWeaponChance(thing?.def)) return new List<EntadTraitDef>();
            return ApplyRandomTraits(thing, request, RollBudget(request));
        }

        private static float RollBudget(EntadApplyRequest request) => (request.points ?? EntadSettings.PointTarget).RandomInRange;

        // The dev tools' "add random traits": skips the unique weapon chance, which a developer pointing at a weapon
        // means to override (GenerateEntadItem's dev action still goes through it)
        internal static List<EntadTraitDef> ApplyRandomTraitsIgnoringUniqueChance(Thing thing)
        {
            var request = new EntadApplyRequest();
            return ApplyRandomTraits(thing, request, RollBudget(request));
        }

        /// <summary>Vanilla unique and persona weapons only roll random traits at the settings' chance (0% by default).
        /// Traits added by name (<see cref="CompEntad.AddTrait"/>) are not affected.</summary>
        private static bool PassesUniqueWeaponChance(ThingDef def) =>
            !EntadUtility.IsUniqueWeapon(def) || Rand.Chance(EntadSettings.UniqueWeaponChance);

        /// <summary>As <see cref="ApplyRandomTraits(Thing, EntadApplyRequest)"/>, with a fixed point target (a
        /// minimum) that overrides <c>request.points</c>. The unique weapon chance applies here too.</summary>
        public static List<EntadTraitDef> ApplyRandomTraits(Thing thing, float points, EntadApplyRequest request = null)
        {
            if (!PassesUniqueWeaponChance(thing?.def)) return new List<EntadTraitDef>();
            return ApplyRandomTraits(thing, request ?? new EntadApplyRequest(), points);
        }

        // Picks traits until the item's points reach the target (a minimum: the last pick may go over). Each pick is a
        // (trait, rarity) pair chosen with the usual rarity and weapon-specific odds, then reweighted:
        //  - a pair costing more than what's left keeps (left / cost)^strictness of its weight, so a cheap item can
        //    still roll a big trait, just rarely (strictness 0 is a pure minimum, higher approaches a maximum);
        //  - a pair much cheaper than the target per remaining trait slot is made less likely, so a big target buys
        //    rarer traits instead of running into the trait cap on Commons.
        // An item allowed drawbacks takes one as soon as one is eligible after its first real trait, even if that
        // trait already reached the target, as long as the drawback's points put the item back under it; generation
        // then carries on and pays for it with more or better traits.
        // The trait cap is the one thing that can stop an item short of its target.
        private static List<EntadTraitDef> ApplyRandomTraits(Thing thing, EntadApplyRequest request, float budget)
        {
            var added = new List<EntadTraitDef>();
            var comp = thing?.TryGetComp<CompEntad>();
            if (comp == null) return added;

            const float Epsilon = 0.001f;
            // Never zero, so even a zero target gets one trait (overshoot weights would all be 0 otherwise)
            float remaining = Math.Max(budget, 0.1f);
            float strictness = Math.Max(0f, request.overshootStrictness ?? EntadSettings.OvershootStrictness);
            int cap = Math.Max(1, request.maxTraits ?? EntadSettings.MaxTraits);
            int drawbacksLeft = Rand.Chance(request.drawbackChance ?? EntadSettings.DrawbackChance) ? (request.maxDrawbacks ?? EntadSettings.MaxDrawbacks) : 0;
            var chances = request.rarityChances ?? EntadRarityChances.Default;
            var allowed = request.traitFilter?.rarities;
            Func<EntadTraitDef, float> weight = null;
            if (thing.def.IsWeapon && EntadSettings.WeaponSpecificWeight > 1f)
                weight = d => d.IsWeaponSpecific ? EntadSettings.WeaponSpecificWeight : 1f;

            bool hasRealTrait = false;
            // Runs past the target only to take a drawback the item rolled for, whose points reopen the target
            while (added.Count < cap)
            {
                if (remaining <= Epsilon && !(drawbacksLeft > 0 && hasRealTrait)) break;
                var candidates = GetApplicableTraits(thing, request.traitFilter);
                candidates.RemoveAll(d => d.neverRandom);
                if (candidates.Count == 0) break;

                int slotsLeft = cap - added.Count;
                float perSlot = remaining / slotsLeft;
                float left = remaining;
                EntadTraitDef pick = null;
                EntadRarity rolled = EntadRarity.Common;
                // A drawback needs a real trait before it and a free slot after it to spend its points on
                if (drawbacksLeft > 0 && hasRealTrait && slotsLeft > 1)
                {
                    // ...and a real trait that can still go on with it, or the drawback would never be paid back
                    pick = chances.PickWhere(candidates, weight, allowed,
                        (d, r) => d.IsDrawback && left - d.PointsAt(r) > Epsilon
                            && candidates.Any(p => !p.IsDrawback && !p.ConflictsWith(d) && !d.ConflictsWith(p)), null, out rolled);
                    if (pick != null) drawbacksLeft--;
                }
                if (pick == null && remaining <= Epsilon) break;
                if (pick == null)
                {
                    pick = chances.PickWhere(candidates, weight, allowed,
                        (d, r) => !d.IsDrawback,
                        (d, r) =>
                        {
                            float c = d.PointsAt(r);
                            if (c > left + Epsilon) return (float)Math.Pow(left / c, strictness);
                            if (c >= perSlot) return 1f;
                            float f = c / perSlot;
                            return Math.Max(0.05f, f * f);
                        }, out rolled);
                }
                if (pick == null) break;
                rolled = pick.ClampRarity(rolled);
                if (!comp.AddTrait(pick, rolled)) break;
                added.Add(pick);
                if (!pick.IsDrawback) hasRealTrait = true;
                remaining -= pick.PointsAt(rolled);
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

        /// <summary>As <see cref="GenerateEntadItem(EntadItemRequest)"/>, with a fixed point target (a minimum)
        /// that overrides <c>request.points</c>.</summary>
        public static Thing GenerateEntadItem(float points, EntadItemRequest request = null)
        {
            return GenerateEntadItem(request ?? new EntadItemRequest(), points);
        }

        /// <summary>Generates an unspawned item with entad traits. Item defs are tried in random order until one can
        /// receive at least one trait. Vanilla unique and persona weapon defs are skipped unless the player's "unique
        /// and persona weapons" chance passes for them.</summary>
        /// <returns>The item, not yet spawned, or null when no item and trait combination fits the request.</returns>
        public static Thing GenerateEntadItem(EntadItemRequest request = null)
        {
            request = request ?? new EntadItemRequest();
            return GenerateEntadItem(request, RollBudget(request));
        }

        private static Thing GenerateEntadItem(EntadItemRequest request, float points)
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
                if (ApplyRandomTraits(thing, request, points).Count > 0) return thing;
            }
            return null;
        }
    }
}
