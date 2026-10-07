using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // The StorageCapacity building property: more item stacks per cell on a storage building.
    //
    // Vanilla, and every storage mod that leaves capacity to vanilla, asks GridsUtility.GetMaxItemsAllowedInCell,
    // which reads the cell's edifice's MaxItemsInCell. A postfix there covers all of them, including subclasses that
    // override MaxItemsInCell. Adaptive Storage Framework is the exception: it keeps its own per-cell capacity table
    // (which its hauling prefilter, valid-item tracking and UI all read), so for its buildings the factor goes into
    // that table instead (EntadStorage_ASF) and the grid postfix leaves them alone, or they'd be scaled twice.
    //
    // LWM's Deep Storage decides capacity in its own comp and isn't supported: BuildingPropertyRange.AppliesTo keeps
    // the trait off buildings carrying its comp, so it never rolls where it would do nothing.
    public static class EntadStorage
    {
        // Spawned storage buildings whose factor isn't 1. The grid postfix runs for every capacity check in the game,
        // so it starts with an empty-check and then a single dictionary lookup on the edifice. ASF buildings stay
        // out: they're scaled through their own table.
        private static readonly Dictionary<Building, CompEntad> active = new Dictionary<Building, CompEntad>();

        public static void Clear() => active.Clear();

        private static readonly List<Building> stale = new List<Building>();

        // From CompEntad on spawn, despawn and trait changes. fromSpawn: ASF buildings are recalculated after their own
        // spawn has finished instead (Patch_ASF_OnSpawn), since ASF unpacks a reinstalled building's contents late
        // in OnSpawn and rebuilding its stored list before that would lose them.
        public static void Refresh(CompEntad comp, bool spawned, bool fromSpawn = false)
        {
            if (!(comp.parent is Building_Storage b)) return;
            // A removed map despawns only its pawns, so its buildings never reach PostDeSpawn; drop them here
            stale.Clear();
            foreach (var k in active.Keys) if (!k.Spawned) stale.Add(k);
            foreach (var k in stale) active.Remove(k);
            bool scaled = spawned && b.def.building?.maxItemsInCell >= 2 && IsScaled(comp);
            bool asf = EntadStorage_ASF.Is(b);
            if (scaled && !asf) active[b] = comp;
            else active.Remove(b);
            if (asf && spawned && !fromSpawn) EntadStorage_ASF.Recalculate(b);
        }

        public static bool IsScaled(CompEntad comp) => Mathf.Abs(comp.PropertyFactor(EntadBuildingProperty.StorageCapacity) - 1f) > 0.001f;

        public static int Scaled(int baseCount, float factor) =>
            factor <= 1f ? Mathf.Max(1, Mathf.RoundToInt(baseCount * factor)) : Mathf.Max(baseCount + 1, Mathf.RoundToInt(baseCount * factor));

        internal static void Postfix_GetMaxItemsAllowedInCell(IntVec3 c, Map map, ref int __result)
        {
            if (active.Count == 0 || map == null) return;
            var edifice = c.GetEdifice(map);
            if (edifice == null || !active.TryGetValue(edifice, out var comp)) return;
            __result = Scaled(__result, comp.PropertyFactor(EntadBuildingProperty.StorageCapacity));
        }
    }

    // Revealed the first time an item is stored in it. ASF's override calls this base method, so it's covered too.
    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.Notify_ReceivedThing))]
    public static class Patch_Building_Storage_ReceivedThing_EntadReveal
    {
        public static void Postfix(Building_Storage __instance)
        {
            // Only the player's own storage, like vanilla's body of this method: not an item dropped on an enemy shelf
            if (__instance.Faction != Faction.OfPlayer || !EntadCompInjector.MayHaveComp(__instance.def)) return;
            __instance.TryGetComp<CompEntad>()?.RevealProperty(EntadBuildingProperty.StorageCapacity, 1);
        }
    }

    [HarmonyPatch(typeof(GridsUtility), nameof(GridsUtility.GetMaxItemsAllowedInCell))]
    public static class Patch_GetMaxItemsAllowedInCell_EntadStorage
    {
        public static void Postfix(IntVec3 c, Map map, ref int __result) => EntadStorage.Postfix_GetMaxItemsAllowedInCell(c, map, ref __result);
    }

    // Clears the registry for each new or loaded game; spawning re-registers every building
    public class EntadStorageReset : GameComponent
    {
        public EntadStorageReset(Game game) { EntadStorage.Clear(); EntadTrade.Reset(); }
    }

    // Adaptive Storage Framework (adaptive.storage.framework), reached by reflection so nothing here needs it loaded.
    // Its ThingClass fills a per-cell capacity table from DefaultMaxItemsInCell() when it initialises (PostMake, and
    // on load), and sums it into TotalSlots. We scale DefaultMaxItemsInCell, then rebuild the table on spawn and on
    // trait changes the way its own PostInitialize does, since traits are usually added after PostMake already ran.
    public static class EntadStorage_ASF
    {
        public static readonly System.Type ThingClass = AccessTools.TypeByName("AdaptiveStorage.ThingClass");
        private static readonly System.Type ExtensionType = AccessTools.TypeByName("AdaptiveStorage.Extension");

        private static readonly MethodInfo initMaxItems = ThingClass == null ? null : AccessTools.Method(ThingClass, "InitializeMaxItemsByCell");
        private static readonly MethodInfo initStored = ThingClass == null ? null : AccessTools.Method(ThingClass, "InitializeStoredThings");
        private static readonly FieldInfo maxItemsByCell = ThingClass == null ? null : AccessTools.Field(ThingClass, "_maxItemsByCell");
        private static readonly FieldInfo currentSlotLimit = ThingClass == null ? null : AccessTools.Field(ThingClass, "_currentSlotLimit");
        private static readonly PropertyInfo totalSlots = ThingClass == null ? null : AccessTools.Property(ThingClass, "TotalSlots");
        private static readonly PropertyInfo currentSlotLimitProp = ThingClass == null ? null : AccessTools.Property(ThingClass, "CurrentSlotLimit");
        // The info card's "stacks per cell" and "total storage capacity" rows, which ASF builds once from the values
        // at that time. Optional: without it capacity still works, the card just shows the unscaled numbers.
        private static readonly FieldInfo statDrawEntries = ThingClass == null ? null : AccessTools.Field(ThingClass, "_statDrawEntries");
        private static readonly ConstructorInfo statDrawEntriesCtor = statDrawEntries == null ? null : AccessTools.Constructor(statDrawEntries.FieldType, new[] { ThingClass });

        // Every member we touch was found; if ASF renames one, its buildings are simply left out (AppliesTo)
        public static readonly bool Supported = ThingClass != null && initMaxItems != null && initStored != null && maxItemsByCell != null
            && currentSlotLimit != null && totalSlots?.GetSetMethod(true) != null && currentSlotLimitProp?.GetSetMethod(true) != null
            && AccessTools.Method(ThingClass, "DefaultMaxItemsInCell") != null && AccessTools.Method(ThingClass, "OnSpawn") != null;

        public static bool Is(Thing t) => ThingClass != null && ThingClass.IsInstanceOfType(t);
        public static bool IsDef(ThingDef td) => ThingClass != null && td.thingClass != null && ThingClass.IsAssignableFrom(td.thingClass);

        // A def with ASF's per-cell capacity table only uses DefaultMaxItemsInCell for cells the table leaves out,
        // so the factor would barely apply; the trait stays off those
        public static bool HasCellTable(ThingDef td)
        {
            if (td.modExtensions == null || ExtensionType == null) return false;
            foreach (var ext in td.modExtensions)
            {
                if (ext == null || !ExtensionType.IsInstanceOfType(ext)) continue;
                var f = AccessTools.Field(ext.GetType(), "maxItemsByCell");
                return f == null || f.GetValue(ext) != null;
            }
            return false;
        }

        private static bool warned;

        public static void Recalculate(Building b)
        {
            if (!Supported) return;
            try
            {
                initMaxItems.Invoke(b, null);
                int sum = 0;
                foreach (int n in (int[])maxItemsByCell.GetValue(b)) sum += n;
                totalSlots.SetValue(b, sum);
                // What PostInitialize does: nudge the stored limit so the setter reruns UpdateMaxItemsInCell with it
                int limit = (int)currentSlotLimit.GetValue(b);
                currentSlotLimit.SetValue(b, limit == int.MaxValue ? limit - 1 : limit + 1);
                currentSlotLimitProp.SetValue(b, limit);
                // Re-sort what's on the cells into stored and overflow against the new capacity
                if (b.Spawned) initStored.Invoke(b, null);
                if (statDrawEntriesCtor != null) statDrawEntries.SetValue(b, statDrawEntriesCtor.Invoke(new object[] { b }));
            }
            catch (System.Exception e)
            {
                if (!warned) Log.Warning($"[Entad Framework] Couldn't apply an entad storage trait to an Adaptive Storage building ({b}); it keeps its normal capacity: {e}");
                warned = true;
            }
        }
    }

    // After ASF's own spawn (unpacking included), so the capacity table is rebuilt with the item's traits
    [HarmonyPatch]
    public static class Patch_ASF_OnSpawn
    {
        public static bool Prepare() => EntadStorage_ASF.Supported;

        public static MethodBase TargetMethod() => AccessTools.Method(EntadStorage_ASF.ThingClass, "OnSpawn");

        public static void Postfix(Building __instance)
        {
            var comp = __instance.TryGetComp<CompEntad>();
            // Any traits, not only scaled ones: a trait change while minified may have returned the factor to 1, and
            // ASF keeps the table it built back then
            if (comp == null || comp.activeTraits.Count == 0) return;
            EntadStorage_ASF.Recalculate(__instance);
        }
    }

    [HarmonyPatch]
    public static class Patch_ASF_DefaultMaxItemsInCell
    {
        public static bool Prepare() => EntadStorage_ASF.Supported;

        public static MethodBase TargetMethod() => AccessTools.Method(EntadStorage_ASF.ThingClass, "DefaultMaxItemsInCell");

        public static void Postfix(Building __instance, ref int __result)
        {
            // While loading, the item's traits aren't settled yet (defs removed since the save are still null);
            // the spawn that follows recalculates
            if (Scribe.mode != LoadSaveMode.Inactive) return;
            var comp = __instance.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return;
            float factor = comp.PropertyFactor(EntadBuildingProperty.StorageCapacity);
            if (Mathf.Abs(factor - 1f) <= 0.001f) return;
            __result = EntadStorage.Scaled(__result, factor);
        }
    }
}
