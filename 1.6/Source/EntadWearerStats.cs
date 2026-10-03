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

        // Pawn stats that at least one entad modifier offsets
        public static HashSet<StatDef> Stats
        {
            get
            {
                if (stats == null)
                {
                    stats = new HashSet<StatDef>();
                    foreach (var d in DefDatabase<EntadModifierDef>.AllDefsListForReading)
                        if (d.statOffsets != null)
                            foreach (var r in d.statOffsets)
                                if (r.stat != null && EntadModifierDef.IsWearerStat(r.stat)) stats.Add(r.stat);
                }
                return stats;
            }
        }

        public static float OffsetFrom(Thing gear, StatDef stat, out bool hidden)
        {
            hidden = false;
            var comp = (gear as ThingWithComps)?.GetComp<CompEntad>();
            return comp == null ? 0f : comp.WearerOffset(stat, out hidden);
        }
    }

    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.GearAffectsStat))]
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
            if (!EntadWearerStats.Stats.Contains(stat)) return;
            __result += EntadWearerStats.OffsetFrom(gear, stat, out _);
        }
    }

    // Hidden offsets still apply but are not listed in the stat explanation
    [HarmonyPatch(typeof(StatWorker), nameof(StatWorker.InfoTextLineFromGear))]
    public static class Patch_InfoTextLineFromGear
    {
        public static void Postfix(Thing gear, StatDef stat, ref string __result)
        {
            if (!EntadWearerStats.Stats.Contains(stat)) return;
            EntadWearerStats.OffsetFrom(gear, stat, out bool hidden);
            if (hidden) __result = "";
        }
    }
}
