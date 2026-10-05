using System;
using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;

namespace EntadFramework
{
    [Flags]
    public enum EntadItemKind
    {
        None = 0,
        Weapon = 1,
        Apparel = 2,
        Furniture = 4,
        All = Weapon | Apparel | Furniture
    }

    public static class EntadUtility
    {
        // After the durability settings change: every entad item in the game (on maps, inside containers and pawn
        // gear, in caravans) keeps its health fraction under the new maximum. One-off, when the settings window closes.
        public static void RescaleDurability(Dictionary<EntadRarity, float> previous)
        {
            bool changed = false;
            foreach (var kv in previous) if (EntadSettings.Durability[kv.Key] != kv.Value) changed = true;
            if (!changed) return;

            // GetAllThingsRecursively clears its output list on every call, so collect each into a scratch list
            var things = new List<Thing>();
            var scratch = new List<Thing>();
            foreach (var map in Find.Maps)
            {
                ThingOwnerUtility.GetAllThingsRecursively(map, ThingRequest.ForGroup(ThingRequestGroup.Everything), scratch);
                things.AddRange(scratch);
            }
            foreach (var caravan in Find.WorldObjects.Caravans)
            {
                ThingOwnerUtility.GetAllThingsRecursively(caravan, scratch);
                things.AddRange(scratch);
            }

            // Each item knows the factor its HP was scaled to, so it rescales itself. Anything not reached here
            // (world pawns, pods in flight) catches up the next time the save loads.
            foreach (var t in things.Distinct())
                (t as ThingWithComps)?.GetComp<CompEntad>()?.SyncDurability();
        }

        public static bool IsFurniture(ThingDef def)
        {
            if (def.building == null || def.designationCategory == null) return false;
            if (IsSingleUse(def)) return false;
            // Quality is not required: workbenches, lights, heaters etc. count too
            return FurnitureCategories.Contains(def.designationCategory.defName);
        }

        // Traps and explosive buildings (mines, firefoam poppers, ...) are used up when they trigger, so they never
        // become entads. Detected from the def itself so buildings from other mods are covered too.
        public static bool IsSingleUse(ThingDef def)
        {
            if (def.building == null) return false;
            if (def.building.isTrap) return true;
            if (def.comps != null)
                foreach (var c in def.comps)
                    if (c is CompProperties_Explosive) return true;
            return false;
        }

        private static readonly HashSet<string> FurnitureCategories = new HashSet<string>
        {
            "Furniture", "Production", "Power", "Temperature", "Joy", "Misc", "Security", "Ship"
        };

        // Buildings pawns actually use: seats, beds, eating surfaces, and anything with an interaction cell (workbenches etc.)
        public static bool IsPawnUsable(ThingDef def)
        {
            return def.building != null
                && (def.hasInteractionCell || def.building.isSittable || def.IsBed || def.surfaceType == SurfaceType.Eat);
        }

        public static EntadItemKind KindOf(ThingDef def)
        {
            EntadItemKind kind = EntadItemKind.None;
            if (def.IsWeapon) kind |= EntadItemKind.Weapon;
            if (def.IsApparel) kind |= EntadItemKind.Apparel;
            if (IsFurniture(def)) kind |= EntadItemKind.Furniture;
            return kind;
        }

        public static bool IsEntadCompatible(ThingDef def)
        {
            return KindOf(def) != EntadItemKind.None;
        }

        public static EntadItemKind ParseKind(string category)
        {
            switch (category)
            {
                case "Weapon": return EntadItemKind.Weapon;
                case "Apparel": return EntadItemKind.Apparel;
                case "Furniture": return EntadItemKind.Furniture;
                default: return EntadItemKind.None;
            }
        }
    }

    // Attaches CompEntad to every compatible item def, regardless of which XML parent it uses
    // (e.g. beds do not inherit from FurnitureWithQualityBase).
    [StaticConstructorOnStartup]
    public static class EntadCompInjector
    {
        // Per ThingDef index: true when the def carries CompEntad. Lets hot paths (StatPart_Entad runs for every
        // MaxHitPoints and MarketValue read of every thing) skip the comp lookup for everything else.
        private static bool[] hasComp;

        // Before this class has run (null table) every def answers true, so callers fall back to the comp lookup
        public static bool MayHaveComp(ThingDef def) => hasComp == null || (def.index < hasComp.Length && hasComp[def.index]);

        static EntadCompInjector()
        {
            var defs = DefDatabase<ThingDef>.AllDefsListForReading;
            var table = new bool[defs.Count];
            foreach (ThingDef def in defs)
            {
                if (def.comps != null && def.comps.Any(c => c is CompProperties_Entad)) { table[def.index] = true; continue; }
                // Comps only exist on ThingWithComps; a plain Thing would never create one
                if (!EntadUtility.IsEntadCompatible(def) || !typeof(ThingWithComps).IsAssignableFrom(def.thingClass)) continue;
                if (def.comps == null) def.comps = new List<CompProperties>();
                def.comps.Add(new CompProperties_Entad());
                table[def.index] = true;
            }
            hasComp = table;
        }
    }
}
