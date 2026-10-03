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
                    stats = new HashSet<StatDef>();
                    foreach (var d in DefDatabase<EntadTraitDef>.AllDefsListForReading)
                        if (d.statOffsets != null)
                            foreach (var r in d.statOffsets)
                                if (r.stat != null && EntadTraitDef.IsWearerStat(r.stat)) stats.Add(r.stat);
                }
                return stats;
            }
        }

        // Flat lookups by def index keep the per-call cost of the stat hooks tiny; they run for every gear piece on every stat evaluation.
        private static bool[] statFlags;
        private static bool[] gearFlags;

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

        public static bool IsEntadGear(Thing gear)
        {
            if (!(gear is ThingWithComps twc)) return false;
            if (gearFlags == null)
            {
                var flags = new bool[DefDatabase<ThingDef>.DefCount];
                foreach (var d in DefDatabase<ThingDef>.AllDefsListForReading)
                    flags[d.index] = d.GetCompProperties<CompProperties_Entad>() != null;
                gearFlags = flags;
            }
            return twc.def.index < gearFlags.Length && gearFlags[twc.def.index];
        }

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
            return System.Math.Abs(total - hiddenPart) > float.Epsilon;
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
            if (pawn == null || gearDef.GetCompProperties<CompProperties_Entad>() == null) return;
            __result = EntadWearerStats.PawnHasVisibleEntadOffset(pawn, gearDef, stat);
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.StatOffsetFromGear))]
    public static class Patch_StatOffsetFromGear
    {
        public static void Postfix(Thing gear, StatDef stat, ref float __result)
        {
            if (!EntadWearerStats.AffectsStat(stat) || !EntadWearerStats.IsEntadGear(gear)) return;
            __result += EntadWearerStats.OffsetFrom(gear, stat, out _);
        }
    }

    // Hidden offsets still apply but are left out of the number on the gear line: the line shows the vanilla
    // offset plus revealed traits only. (Gear whose only effect is hidden never gets a line: see GearAffectsStat.)
    [HarmonyPatch(typeof(StatWorker), "InfoTextLineFromGear")]
    public static class Patch_InfoTextLineFromGear
    {
        public static void Postfix(Thing gear, StatDef stat, ref string __result)
        {
            if (!EntadWearerStats.AffectsStat(stat) || !EntadWearerStats.IsEntadGear(gear)) return;
            EntadWearerStats.OffsetFrom(gear, stat, out float hiddenPart);
            if (System.Math.Abs(hiddenPart) <= float.Epsilon) return;
            float shown = StatWorker.StatOffsetFromGear(gear, stat) - hiddenPart;
            // Same format vanilla's InfoTextLineFromGear uses
            __result = "    " + gear.LabelCap + ": " + shown.ToStringByStyle(
                stat.finalizeEquippedStatOffset ? stat.toStringStyle : stat.ToStringStyleUnfinalized, ToStringNumberSense.Offset);
        }
    }
}
