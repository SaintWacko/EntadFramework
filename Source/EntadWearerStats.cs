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

        public static float OffsetFrom(Thing gear, StatDef stat, out bool hidden)
        {
            hidden = false;
            var comp = (gear as ThingWithComps)?.GetComp<CompEntad>();
            return comp == null ? 0f : comp.WearerOffset(stat, out hidden);
        }
    }

    [HarmonyPatch(typeof(StatWorker), "GearAffectsStat")]
    public static class Patch_GearAffectsStat
    {
        public static void Postfix(ThingDef gearDef, StatDef stat, ref bool __result)
        {
            if (__result || !EntadWearerStats.Stats.Contains(stat)) return;
            if (gearDef.GetCompProperties<CompProperties_Entad>() != null) __result = true;
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

    // Hidden offsets still apply but are not listed in the stat explanation
    [HarmonyPatch(typeof(StatWorker), "InfoTextLineFromGear")]
    public static class Patch_InfoTextLineFromGear
    {
        public static void Postfix(Thing gear, StatDef stat, ref string __result)
        {
            if (!EntadWearerStats.AffectsStat(stat)) return;
            EntadWearerStats.OffsetFrom(gear, stat, out bool hidden);
            if (hidden) __result = "";
        }
    }
}
