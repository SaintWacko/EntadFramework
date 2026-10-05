using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Entad offsets on pawn stats (move speed, work speed...) are added through the same path vanilla uses for
    // equippedStatOffsets, so they follow the item's wearer and show up in the pawn's stat explanations.
    public static class EntadWearerStats
    {
        private static HashSet<StatDef> stats;

        // Pawn stats that at least one entad trait offsets
        public static HashSet<StatDef> Stats
        {
            get
            {
                if (stats == null)
                {
                    // Built in a local and published at the end, like the flag tables below
                    var set = new HashSet<StatDef>();
                    foreach (var d in DefDatabase<EntadTraitDef>.AllDefsListForReading)
                        if (d.statOffsets != null)
                            foreach (var r in d.statOffsets)
                                if (r.stat != null && EntadTraitDef.IsWearerStat(r.stat)) set.Add(r.stat);
                    stats = set;
                }
                return stats;
            }
        }

        // Flat lookups by def index keep the per-call cost of the stat hooks tiny; they run for every gear piece on every stat evaluation.
        private static bool[] statFlags;

        public static bool AffectsStat(StatDef stat)
        {
            if (statFlags == null)
            {
                var flags = new bool[DefDatabase<StatDef>.DefCount];
                foreach (var s in Stats) flags[s.index] = true;
                statFlags = flags;
            }
            return stat.index < statFlags.Length && statFlags[stat.index];
        }

        public static bool IsEntadGear(Thing gear) => gear is ThingWithComps twc && IsEntadGearDef(twc.def);

        // EntadCompInjector's per-def table, built right after it adds the comps. (A separate lazy table here could be
        // built before the injector ran and then cache every injected def as false for the session.)
        public static bool IsEntadGearDef(ThingDef def) => EntadCompInjector.MayHaveComp(def);

        public static float OffsetFrom(Thing gear, StatDef stat, out float hiddenPart)
        {
            hiddenPart = 0f;
            var comp = (gear as ThingWithComps)?.GetComp<CompEntad>();
            return comp == null ? 0f : comp.WearerOffset(stat, out hiddenPart);
        }

        // The pawn whose stat explanation / hyperlinks are being built right now. Vanilla's GearAffectsStat only
        // receives the ThingDef, but entad offsets live on the item instance, so without this every def that can
        // carry an entad comp (i.e. nearly all apparel) looked relevant to every stat any trait touches, and showed
        // up under "Relevant gear" as +0.00. Set around the two vanilla callers that list gear; null anywhere else,
        // in which case the postfix leaves vanilla's answer alone.
        [System.ThreadStatic] internal static Pawn explainingPawn;

        // True only inside InfoTextLineFromGear: stat math then leaves out unrevealed traits so the displayed gear
        // line doesn't give them away. Never set around StatWorker.GetValue, whose results are cached.
        [System.ThreadStatic] internal static bool displayExcludesHidden;

        // The entad item whose info card ThingDef.SpecialDisplayStats is building right now. Narrower than the flag
        // above: only StatOffsetFromGear for THIS item (its own "equipped stat offsets" rows) and StatPart_Entad on
        // wearer stats of this item drop hidden traits. Item stats the card reads through GetStatValue (warmup,
        // damage...) are not wearer stats, so their cached values keep the hidden effects. Storing the Thing rather
        // than a bool keeps any other mod's comp row that computes some other gear's offset unaffected.
        [System.ThreadStatic] internal static Thing gearCardThing;

        // The entad item whose info card is drawing right now. StatPart_Entad leaves out unrevealed traits for every
        // stat of this one item (cooldown, damage, warmup...). Those values do land in the stat cache, so
        // Patch_DrawStatsReport clears the item's caches when each frame's draw ends. Only set when the item
        // actually has hidden traits, so ordinary items cost nothing.
        [System.ThreadStatic] internal static Thing infoCardThing;

        public static bool HidingFor(Thing gear) => displayExcludesHidden || (gearCardThing != null && gearCardThing == gear);

        public static IEnumerable<T> WithGearCard<T>(IEnumerable<T> source, Thing gear)
        {
            using (var e = source.GetEnumerator())
            {
                while (true)
                {
                    var prev = gearCardThing;
                    gearCardThing = gear;
                    bool more;
                    try { more = e.MoveNext(); }
                    finally { gearCardThing = prev; }
                    if (!more) yield break;
                    yield return e.Current;
                }
            }
        }

        // True when this pawn holds an item of gearDef whose revealed entad offset for stat is non-zero.
        // Hidden-only effects deliberately don't count: they still apply, but listing the item would give them away.
        public static bool PawnHasVisibleEntadOffset(Pawn pawn, ThingDef gearDef, StatDef stat)
        {
            var apparel = pawn.apparel?.WornApparel;
            if (apparel != null)
                for (int i = 0; i < apparel.Count; i++)
                    if (apparel[i].def == gearDef && HasVisibleOffset(apparel[i], stat)) return true;
            var equipment = pawn.equipment?.AllEquipmentListForReading;
            if (equipment != null)
                for (int i = 0; i < equipment.Count; i++)
                    if (equipment[i].def == gearDef && HasVisibleOffset(equipment[i], stat)) return true;
            return false;
        }

        private static bool HasVisibleOffset(Thing gear, StatDef stat)
        {
            if (!IsEntadGear(gear)) return false;
            float total = OffsetFrom(gear, stat, out float hiddenPart);
            // Real tolerance, not float.Epsilon: total and hiddenPart are separate float sums, so revealed traits that
            // cancel out leave rounding noise that would otherwise list the item as +0.00
            return System.Math.Abs(total - hiddenPart) > 1e-4f;
        }

        // Runs an enumerable with explainingPawn set during each MoveNext, for vanilla iterators (GetInfoCardHyperlinks)
        // whose bodies run lazily, long after a plain prefix would have returned.
        public static IEnumerable<T> WithPawn<T>(IEnumerable<T> source, Pawn pawn)
        {
            using (var e = source.GetEnumerator())
            {
                while (true)
                {
                    var prev = explainingPawn;
                    explainingPawn = pawn;
                    bool more;
                    try { more = e.MoveNext(); }
                    finally { explainingPawn = prev; }
                    if (!more) yield break;
                    yield return e.Current;
                }
            }
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetExplanationUnfinalized))]
    public static class Patch_GetExplanationUnfinalized
    {
        public static void Prefix(StatRequest req, out Pawn __state)
        {
            __state = EntadWearerStats.explainingPawn;
            EntadWearerStats.explainingPawn = req.Thing as Pawn;
        }

        // Finalizer rather than postfix so the context is restored even if the explanation throws
        public static void Finalizer(Pawn __state)
        {
            EntadWearerStats.explainingPawn = __state;
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GetInfoCardHyperlinks))]
    public static class Patch_GetInfoCardHyperlinks
    {
        public static void Postfix(StatRequest statRequest, ref IEnumerable<Dialog_InfoCard.Hyperlink> __result)
        {
            if (statRequest.Thing is Pawn pawn && __result != null)
                __result = EntadWearerStats.WithPawn(__result, pawn);
        }
    }

    [HarmonyPatch(typeof(StatWorker), "GearAffectsStat")]
    public static class Patch_GearAffectsStat
    {
        public static void Postfix(ThingDef gearDef, StatDef stat, ref bool __result)
        {
            if (__result || !EntadWearerStats.AffectsStat(stat)) return;
            var pawn = EntadWearerStats.explainingPawn;
            if (pawn == null || !EntadWearerStats.IsEntadGearDef(gearDef)) return;
            __result = EntadWearerStats.PawnHasVisibleEntadOffset(pawn, gearDef, stat);
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.StatOffsetFromGear))]
    public static class Patch_StatOffsetFromGear
    {
        public static void Postfix(Thing gear, StatDef stat, ref float __result)
        {
            if (!EntadWearerStats.AffectsStat(stat) || !EntadWearerStats.IsEntadGear(gear)) return;
            float total = EntadWearerStats.OffsetFrom(gear, stat, out float hiddenPart);
            __result += EntadWearerStats.HidingFor(gear) ? total - hiddenPart : total;
        }
    }

    // The item's own info card: its equipped-offset rows call StatOffsetFromGear directly for the "Final value",
    // which would otherwise include unrevealed traits while the explanation above it lists only revealed ones.
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.SpecialDisplayStats))]
    public static class Patch_ThingDefSpecialDisplayStats
    {
        public static void Postfix(StatRequest req, ref IEnumerable<StatDrawEntry> __result)
        {
            if (__result != null && req.Thing != null && EntadWearerStats.IsEntadGear(req.Thing))
                __result = EntadWearerStats.WithGearCard(__result, req.Thing);
        }
    }

    // Hidden effects still apply but are left out of the number on the gear line. The flag makes both halves of
    // StatOffsetFromGear skip unrevealed traits: the entad offset (postfix above) and any entad statFactors that
    // StatPart_Entad applies to the def's own equipped offset. StatOffsetFromGear runs the parts directly, never
    // through StatWorker.GetValue, so its result isn't cached. Other stats read meanwhile (LabelCap reads
    // MaxHitPoints) do go through the cache, which is why StatPart_Entad honours the flag on wearer stats only.
    // (Gear whose only effect is hidden never gets a line at all: see GearAffectsStat.)
    [HarmonyPatch(typeof(StatsReportUtility), nameof(StatsReportUtility.DrawStatsReport), new[] { typeof(UnityEngine.Rect), typeof(Thing) })]
    public static class Patch_DrawStatsReport
    {
        public static void Prefix(Thing thing, out KeyValuePair<CompEntad, Thing> __state)
        {
            // Saves the previous value like the other context patches, in case another mod nests a stats report
            __state = new KeyValuePair<CompEntad, Thing>(null, EntadWearerStats.infoCardThing);
            var comp = (thing as ThingWithComps)?.GetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty() || !comp.HasHidden || !comp.HasHiddenStat) return;
            __state = new KeyValuePair<CompEntad, Thing>(comp, __state.Value);
            EntadWearerStats.infoCardThing = thing;
        }

        // Finalizer so the flag is restored and the caches dropped even if drawing throws
        public static void Finalizer(KeyValuePair<CompEntad, Thing> __state)
        {
            if (__state.Key == null) return;
            EntadWearerStats.infoCardThing = __state.Value;
            __state.Key.ClearCardStatCaches();
        }
    }

    [HarmonyPatch(typeof(StatWorker), "InfoTextLineFromGear")]
    public static class Patch_InfoTextLineFromGear
    {
        public static void Prefix(out bool __state)
        {
            __state = EntadWearerStats.displayExcludesHidden;
            EntadWearerStats.displayExcludesHidden = true;
        }

        public static void Finalizer(bool __state)
        {
            EntadWearerStats.displayExcludesHidden = __state;
        }
    }
}
