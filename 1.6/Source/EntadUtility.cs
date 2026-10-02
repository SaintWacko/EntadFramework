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
        public static bool IsFurniture(ThingDef def)
        {
            return def.building != null && def.designationCategory != null && def.designationCategory.defName == "Furniture";
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
        static EntadCompInjector()
        {
            foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (!EntadUtility.IsEntadCompatible(def)) continue;
                if (def.comps == null) def.comps = new List<CompProperties>();
                if (def.comps.Any(c => c is CompProperties_Entad)) continue;
                def.comps.Add(new CompProperties_Entad());
            }
        }
    }
}
