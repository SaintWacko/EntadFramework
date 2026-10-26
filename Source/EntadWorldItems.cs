using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    public enum EntadThingSetKind { Trader, Reward, Loot }

    // Marks a ThingSetMakerDef whose generated sets may hold entads. Trader: the TraderStock def, rolled once per
    // stock and converting a few items (TraderKindDefs may override with EntadTraderExtension). Reward and Loot: each
    // eligible item rolls on its own, up to a most per set. Negative values mean "use the settings for this kind".
    // Content mods can add this to their own defs.
    public class EntadThingSetExtension : DefModExtension
    {
        public EntadThingSetKind kind = EntadThingSetKind.Loot;
        public float chance = -1f;
        public int maxEntads = -1;
        public float pointFactor = 1f;
        // Loot only: the chance an entad binds to the first colonist to use it
        public float bindChance = -1f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;
            if (chance > 1f) yield return "EntadThingSetExtension chance is above 1";
            if (bindChance > 1f) yield return "EntadThingSetExtension bindChance is above 1";
            if (pointFactor < 0f) yield return "EntadThingSetExtension pointFactor is negative";
        }
    }

    // Per-trader override for stocks: the chance a stock holds entads (absolute, or a multiplier on the settings'
    // chance so the slider at 0% still means none), how many, and a points multiplier
    public class EntadTraderExtension : DefModExtension
    {
        public float chance = -1f;
        public float chanceFactor = 1f;
        public IntRange count = new IntRange(-1, -1);
        public float pointFactor = 1f;

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (var e in base.ConfigErrors()) yield return e;
            if (chance > 1f) yield return "EntadTraderExtension chance is above 1";
            if (chanceFactor < 0f) yield return "EntadTraderExtension chanceFactor is negative";
            if (count.min >= 0 && count.max < count.min) yield return "EntadTraderExtension count max is below min";
            if (pointFactor < 0f) yield return "EntadTraderExtension pointFactor is negative";
        }
    }

    // Entads in trader stock, quest rewards and site loot: items the set already made are turned into entads after
    // it is generated, so the set keeps its size. Its value budget was spent on ordinary items, so a converted set is
    // worth more than was asked for (a quest reward reads above its budget); like carried entads, they are extra.
    // Points come from the set's market value before any conversion, silver left out (a trader's cash would
    // otherwise outweigh its goods), so a rich trader or a big reward carries stronger entads.
    public static class EntadWorldItems
    {
        // Root maker of each marked def. ThingSetMaker.Generate also runs for every nested maker (Sum, RandomOption,
        // Conditional...), which know nothing of the def; only a root is looked up here.
        private static Dictionary<ThingSetMaker, EntadThingSetExtension> roots;

        // The outermost marked maker generating right now. A marked def nested in another (a mod's def whose options
        // call Reward_ItemsStandard) converts once, by the outer def's rules.
        [System.ThreadStatic] private static ThingSetMaker active;

        // Item stashes and ancient complex reward crates draw from Reward_ItemsStandard, VE Events' cargo chunks from
        // TraderStock; nobody is there to say what's in them, so while one is being filled its set counts as loot.
        // A depth, since a subclass's override may call the base one.
        [System.ThreadStatic] internal static int foundDepth;
        private static bool FoundAsLoot => foundDepth > 0;

        private static EntadThingSetExtension ExtensionFor(ThingSetMaker maker)
        {
            if (roots == null)
            {
                var found = new Dictionary<ThingSetMaker, EntadThingSetExtension>();
                foreach (var def in DefDatabase<ThingSetMakerDef>.AllDefsListForReading)
                {
                    var ext = def.GetModExtension<EntadThingSetExtension>();
                    if (ext != null && def.root != null) found[def.root] = ext;
                }
                roots = found;
            }
            return roots.TryGetValue(maker, out var e) ? e : null;
        }

        public static void Begin(ThingSetMaker maker)
        {
            if (active != null || !AnyItemsEnabled() || ExtensionFor(maker) == null) return;
            active = maker;
        }

        public static void End(ThingSetMaker maker, ThingSetMakerParams parms, List<Thing> things)
        {
            if (active != maker) return;
            active = null;
            bool wasQuiet = CompEntad.quietReveals;
            CompEntad.quietReveals = true;
            try { Convert(ExtensionFor(maker), parms, things); }
            catch (System.Exception e) { Log.ErrorOnce("[Entad Framework] Could not add entads to a generated thing set: " + e, 0x45465749); }
            finally { CompEntad.quietReveals = wasQuiet; }
        }

        private static bool AnyItemsEnabled() => EntadWorld.Enabled(EntadWorldFeature.Traders)
            || EntadWorld.Enabled(EntadWorldFeature.Rewards) || EntadWorld.Enabled(EntadWorldFeature.Loot);

        private static EntadWorldFeature FeatureFor(EntadThingSetKind kind)
            => kind == EntadThingSetKind.Trader ? EntadWorldFeature.Traders
            : kind == EntadThingSetKind.Reward ? EntadWorldFeature.Rewards : EntadWorldFeature.Loot;

        public static void Clear(ThingSetMaker maker)
        {
            if (active == maker) active = null;
        }

        // The settings' four anchors over the set's market value, joined by straight lines and flat past either end
        public static float BasePoints(float setValue)
        {
            var curve = new SimpleCurve();
            for (int i = 0; i < EntadSettings.ItemCurveValues.Length; i++)
                curve.Add(EntadSettings.ItemCurveValues[i], EntadSettings.ItemCurvePoints[i]);
            return curve.Evaluate(setValue);
        }

        private static void Convert(EntadThingSetExtension ext, ThingSetMakerParams parms, List<Thing> things)
        {
            if (ext == null || things.NullOrEmpty()) return;
            var candidates = new List<CompEntad>();
            float setValue = 0f;
            foreach (Thing t in things)
            {
                if (t.def != ThingDefOf.Silver) setValue += t.MarketValue * t.stackCount;
                var comp = t.stackCount == 1 ? Candidate(t.GetInnerIfMinified() as ThingWithComps) : null;
                if (comp != null) candidates.Add(comp);
            }
            if (candidates.Count == 0) return;

            EntadThingSetKind kind = FoundAsLoot ? EntadThingSetKind.Loot : ext.kind;
            if (!EntadWorld.Enabled(FeatureFor(kind))) return;
            // VE Events' cargo chunk makes a whole trader stock and keeps a few hundred silver of it; points from
            // the whole stock would give that handful the strength of a rich trader's goods
            if (FoundAsLoot && ext.kind == EntadThingSetKind.Trader) setValue = Mathf.Min(setValue, EntadSettings.ItemCurveValues[0]);
            float basePoints = BasePoints(setValue) * Mathf.Max(0f, ext.pointFactor);
            if (kind == EntadThingSetKind.Trader)
            {
                var trader = parms.traderDef?.GetModExtension<EntadTraderExtension>();
                float chance = trader != null && trader.chance >= 0f ? trader.chance
                    : (ext.chance >= 0f ? ext.chance : EntadSettings.TraderStockChance) * Mathf.Max(0f, trader?.chanceFactor ?? 1f);
                if (!Rand.Chance(chance)) return;
                IntRange range = trader != null && trader.count.min >= 0 ? trader.count : EntadSettings.TraderEntadCount;
                int count = Mathf.Max(0, range.RandomInRange);
                basePoints *= Mathf.Max(0f, trader?.pointFactor ?? 1f);
                int converted = 0;
                foreach (var comp in candidates.InRandomOrder())
                {
                    if (converted >= count) break;
                    if (!TryConvert(comp, basePoints)) continue;
                    converted++;
                    if (EntadSettings.HideTraits) RevealShare(comp);
                }
            }
            else
            {
                bool reward = kind == EntadThingSetKind.Reward;
                // Found loot reads the loot settings: the extension it passed through belongs to the reward or
                // trader def
                bool useExt = !FoundAsLoot;
                float chance = useExt && ext.chance >= 0f ? ext.chance : reward ? EntadSettings.RewardItemChance : EntadSettings.LootItemChance;
                int max = useExt && ext.maxEntads >= 0 ? ext.maxEntads : reward ? EntadSettings.RewardMaxEntads : EntadSettings.LootMaxEntads;
                float bindChance = reward ? 0f : useExt && ext.bindChance >= 0f ? ext.bindChance : EntadSettings.LootBindChance;
                int converted = 0;
                foreach (var comp in candidates.InRandomOrder())
                {
                    if (converted >= max) break;
                    if (!Rand.Chance(chance) || !TryConvert(comp, basePoints)) continue;
                    converted++;
                    // Bought and given entads are never bound; a found one may take to whoever picks it up first.
                    // Nothing on the item says so.
                    if (bindChance > 0f && Rand.Chance(bindChance)) comp.bindOnFirstUse = true;
                    if (!EntadSettings.HideTraits) continue;
                    // The quest giver knows part of what it hands over, as a trader does; loot is found unknown
                    if (reward) RevealShare(comp);
                    else foreach (var trait in comp.activeTraits) trait.Hide();
                }
            }
        }

        // Gear already holding traits (a scenario's, another mod's) is left as it is
        private static CompEntad Candidate(ThingWithComps thing)
        {
            var comp = thing?.GetComp<CompEntad>();
            return comp != null && comp.activeTraits.NullOrEmpty() && EntadUtility.IsEntadCompatible(thing.def) ? comp : null;
        }

        private static bool TryConvert(CompEntad comp, float basePoints)
        {
            float points = basePoints * Rand.Range(1f - EntadSettings.ItemPointVariance, 1f + EntadSettings.ItemPointVariance);
            return EntadApi.ApplyRandomTraits(comp.parent, Mathf.Max(0f, points)).Count > 0;
        }

        // A trader or quest giver knows part of what it offers: one share per item, uniform from none to all, and
        // that many of its traits shown, picked at random
        private static void RevealShare(CompEntad comp)
        {
            var traits = comp.activeTraits;
            foreach (var trait in traits) trait.Hide();
            int known = Rand.RangeInclusive(0, traits.Count);
            foreach (var trait in traits.InRandomOrder())
            {
                if (known-- <= 0) break;
                trait.Reveal(EntadEffectKind.All, false);
            }
        }
    }

    [HarmonyPatch(typeof(ThingSetMaker), nameof(ThingSetMaker.Generate), new[] { typeof(ThingSetMakerParams) })]
    public static class Patch_ThingSetMaker_Generate_Entads
    {
        public static void Prefix(ThingSetMaker __instance) => EntadWorldItems.Begin(__instance);

        public static void Postfix(ThingSetMaker __instance, ThingSetMakerParams parms, List<Thing> __result) =>
            EntadWorldItems.End(__instance, parms, __result);

        // Another mod's prefix throwing would skip the postfix and leave every later set unconverted
        public static void Finalizer(ThingSetMaker __instance) => EntadWorldItems.Clear(__instance);
    }

    // Generators whose sets are found rather than given. Every stash worker that declares its own override: VFE
    // Deserters' dead drop replaces the vanilla one without calling it, and makes its set from Reward_ItemsStandard
    // itself.
    [HarmonyPatch]
    public static class Patch_FoundLoot_Entads
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var types = new List<System.Type> { typeof(SitePartWorker_ItemStash) };
            types.AddRange(typeof(SitePartWorker_ItemStash).AllSubclassesNonAbstract());
            foreach (var type in types)
            {
                var method = AccessTools.DeclaredMethod(type, nameof(SitePartWorker_ItemStash.Notify_GeneratedByQuestGen));
                if (method != null) yield return method;
            }
            var complexRewards = AccessTools.DeclaredMethod(typeof(LayoutWorkerComplex), "SpawnRoomRewards");
            if (complexRewards != null) yield return complexRewards;
            // VE Events: a falling trade ship's cargo is made from its stock when the chunk is destroyed
            var cargo = AccessTools.TypeByName("VEE.CargoChunkSpawner");
            var destroy = cargo == null ? null : AccessTools.DeclaredMethod(cargo, nameof(Thing.Destroy));
            if (destroy != null) yield return destroy;
        }

        public static void Prefix() => EntadWorldItems.foundDepth++;

        public static void Finalizer() => EntadWorldItems.foundDepth--;
    }
}
