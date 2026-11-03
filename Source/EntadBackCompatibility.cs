using System;
using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using Verse;

namespace EntadFramework
{
    // Traits renamed or merged into another after release. Saves that still hold one load it as its replacement
    // instead of logging "Could not load reference" and dropping it. The old rolls came from the old trait's range,
    // which can be far from the replacement's, so AppliedEntadTrait.ExposeData notices the name changed and re-rolls
    // the values at the saved rarity. Empty until the first release: test saves from before then aren't kept.
    // Entry shape: { "Entad_OldName", "Entad_NewName" },
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.BackCompatibleDefName))]
    public static class Patch_BackCompatibleDefName
    {
        private static readonly Dictionary<string, string> RenamedTraits = new Dictionary<string, string>
        {
        };

        // Runs for every def reference while a save loads, so anything that isn't a trait leaves after one compare
        public static void Postfix(Type defType, string defName, ref string __result)
        {
            if (defType != typeof(EntadTraitDef) || defName == null) return;
            if (RenamedTraits.TryGetValue(defName, out var replacement)) __result = replacement;
        }
    }

    // Defs removed after release with no replacement. Old items drop the trait (CompEntad removes traits with a null
    // def) and pawns drop the memory, as the game already does; this only stops ScribeExtractor logging a red
    // "Could not load reference" error for each one. Empty until the first release, like the rename map above.
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.WasDefRemoved))]
    public static class Patch_WasDefRemoved
    {
        private static readonly HashSet<string> RemovedTraits = new HashSet<string>
        {
        };

        private static readonly HashSet<string> RemovedThoughts = new HashSet<string>
        {
        };

        // Traits defined by a compat module (Mods/<Mod>/Defs), which don't exist while that mod is off. Removing the
        // mod mid-save drops them from old items the same way. Named here because the module's own code isn't loaded
        // then either.
        private static readonly HashSet<string> ModuleTraits = new HashSet<string>
        {
            // Mods/DubsBadHygiene
            "Entad_Pure", "Entad_Wellspring", "Entad_Cesspit", "Entad_BottomlessPit", "Entad_Fouling", "Entad_Evaporative",
        };

        // Only reached for a def name that failed to resolve, so it never runs on a normal load
        public static void Postfix(string defName, Type type, ref bool __result)
        {
            if (__result || defName == null) return;
            if (type == typeof(EntadTraitDef)) __result = RemovedTraits.Contains(defName) || ModuleTraits.Contains(defName);
            else if (type == typeof(RimWorld.ThoughtDef)) __result = RemovedThoughts.Contains(defName);
        }
    }
}
