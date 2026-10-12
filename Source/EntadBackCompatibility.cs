using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace EntadFramework
{
    // Traits that were removed as duplicates of another trait on the same stat. Saves that still hold one load it as
    // its replacement instead of logging "Could not load reference" and dropping it. The old rolls came from the
    // removed trait's range, which can be far from the replacement's (Marksman's Eye 0.03-0.1 vs Steady Hands 1-4),
    // so AppliedEntadTrait.ExposeData notices the name changed and re-rolls the values at the saved rarity.
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

    // Defs removed with no replacement (the general drawbacks, build 149: only persona drawbacks remain). Old items
    // drop the trait (CompEntad removes traits with a null def) and pawns drop the memory, as the game already does;
    // this only stops ScribeExtractor logging a red "Could not load reference" error for each one.
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.WasDefRemoved))]
    public static class Patch_WasDefRemoved
    {
        private static readonly HashSet<string> RemovedTraits = new HashSet<string>
        {
            "Entad_Haunting", "Entad_Heavy", "Entad_Brittle", "Entad_Garish",
            "Entad_Kindling", "Entad_Draining", "Entad_Unnerving", "Entad_RestlessDreams",
        };

        private static readonly HashSet<string> RemovedThoughts = new HashSet<string>
        {
            "Entad_Thought_Haunted", "Entad_Thought_BadDreams",
        };

        // Only reached for a def name that failed to resolve, so it never runs on a normal load
        public static void Postfix(string defName, Type type, ref bool __result)
        {
            if (__result || defName == null) return;
            if (type == typeof(EntadTraitDef)) __result = RemovedTraits.Contains(defName);
            else if (type == typeof(RimWorld.ThoughtDef)) __result = RemovedThoughts.Contains(defName);
        }
    }
}
