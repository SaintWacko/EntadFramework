using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace EntadFramework
{
    // Traits that were removed as duplicates of another trait on the same stat. Saves that still hold one load it as
    // its replacement instead of logging "Could not load reference" and dropping it. Each pair changes the same stats
    // in the same order, so the item's stored rolls carry over unchanged.
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.BackCompatibleDefName))]
    public static class Patch_BackCompatibleDefName
    {
        private static readonly Dictionary<string, string> RenamedTraits = new Dictionary<string, string>
        {
            { "Entad_RapidCycling", "Entad_HairTrigger" },
            { "Entad_Stormfeed", "Entad_HairTrigger" },
            { "Entad_AcceleratedRounds", "Entad_HeavySlugs" },
            { "Entad_BrutalEdge", "Entad_HeavyHitter" },
            { "Entad_Parrying", "Entad_Evasion" },
            { "Entad_MarksmansEye", "Entad_SteadyHands" },
            { "Entad_BlessedComfort", "Entad_SoothingPresence" },
        };

        // Runs for every def reference while a save loads, so anything that isn't a trait leaves after one compare
        public static void Postfix(Type defType, string defName, ref string __result)
        {
            if (defType != typeof(EntadTraitDef) || defName == null) return;
            if (RenamedTraits.TryGetValue(defName, out var replacement)) __result = replacement;
        }
    }
}
