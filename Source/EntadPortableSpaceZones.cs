using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Zones and areas carried by a portable space (EntadPortableSpace.cs).
    //
    // Kept as copies, the way vanilla's gravship moves them (MoveableStockpile / MoveableGrowZone /
    // MoveableArea_Allowed): the zone on the map is deleted or shrunk and a new one is made where the room lands.
    // Holding the Zone object itself would mean saving a zone with no zone manager, and Zone's PostLoadInit reaches
    // for its map. Bills that pointed at a lifted stockpile fall back the way vanilla's do when a stockpile is deleted.
    //
    // Zones: stockpiles and growing zones of exactly those types. A zone wholly inside the rectangle keeps its name
    // and colour; one crossing the edge is split, and the inside part becomes a new zone with the same settings.
    // Fishing zones (and modded zone types) stay on the map: their water stays too.
    // Areas: every area's cells inside the rectangle, by area type, allowed areas by name. A missing allowed area is
    // made on the destination map, unless the map already has the most allowed areas vanilla permits.
    public class StoredZone : IExposable
    {
        public int kind;
        public int id = -1;
        public bool whole;
        public string label;
        public Color color;
        public bool hidden;
        public StorageSettings settings;
        public ThingDef plantDef;
        public bool allowSow = true;
        public bool allowCut = true;
        public List<IntVec3> cells = new List<IntVec3>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind");
            Scribe_Values.Look(ref id, "id", -1);
            Scribe_Values.Look(ref whole, "whole");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref color, "color");
            Scribe_Values.Look(ref hidden, "hidden");
            Scribe_Deep.Look(ref settings, "settings");
            Scribe_Defs.Look(ref plantDef, "plantDef");
            Scribe_Values.Look(ref allowSow, "allowSow", true);
            Scribe_Values.Look(ref allowCut, "allowCut", true);
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && cells == null) cells = new List<IntVec3>();
        }
    }

    public class StoredArea : IExposable
    {
        public string type;
        public string label;
        public Color color;
        public List<IntVec3> cells = new List<IntVec3>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref type, "type");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref color, "color");
            Scribe_Collections.Look(ref cells, "cells", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && cells == null) cells = new List<IntVec3>();
        }
    }

    public static class EntadSpaceZones
    {
        private const int Stockpile = 0;
        private const int Growing = 1;

        // Exact types only: a modded subclass would come back as a plain stockpile and lose whatever it adds
        private static int KindOf(Zone z)
        {
            Type t = z.GetType();
            if (t == typeof(Zone_Stockpile)) return Stockpile;
            if (t == typeof(Zone_Growing)) return Growing;
            return -1;
        }

        // Takes the rectangle's zone cells off the map. 'store' turns a map cell into the coordinates kept
        // (room coordinates for the room, the same map cell for what a placed room displaced).
        public static List<StoredZone> LiftZones(Map map, CellRect rect, Func<IntVec3, IntVec3> store)
        {
            var result = new List<StoredZone>();
            var zones = new List<Zone>();
            foreach (IntVec3 c in rect)
            {
                Zone z = map.zoneManager.ZoneAt(c);
                if (z != null && KindOf(z) >= 0 && !zones.Contains(z)) zones.Add(z);
            }
            foreach (Zone z in zones)
            {
                var inside = z.cells.Where(c => rect.Contains(c)).ToList();
                var sz = new StoredZone
                {
                    kind = KindOf(z),
                    id = z.ID,
                    whole = inside.Count == z.cells.Count,
                    label = z.label,
                    color = z.color,
                    hidden = z.Hidden,
                    cells = inside.Select(store).ToList()
                };
                if (z is Zone_Stockpile sp && sp.settings != null)
                {
                    sz.settings = new StorageSettings();
                    sz.settings.CopyFrom(sp.settings);
                }
                if (z is Zone_Growing g)
                {
                    sz.plantDef = g.GetPlantDefToGrow();
                    sz.allowSow = g.allowSow;
                    sz.allowCut = g.allowCut;
                }
                // No CheckContiguous on what stays: vanilla's would delete the smaller piece of a zone cut in two
                if (sz.whole) z.Delete(false);
                else foreach (IntVec3 c in inside) z.RemoveCell(c);
                result.Add(sz);
            }
            return result;
        }

        // Sets stored zones down. 'mergeIntoOriginal' (picking a room up): the cells a placed room took from a zone
        // that is still there go back into it; otherwise each stored zone becomes a new one, kept separate from
        // any neighbour of the same kind. Cells that can't hold a zone now are left out.
        public static void SetDownZones(List<StoredZone> zones, Map map, Func<IntVec3, IntVec3> toMap, bool mergeIntoOriginal)
        {
            if (zones.NullOrEmpty()) return;
            foreach (StoredZone sz in zones)
            {
                if (sz?.cells == null) continue;
                var cells = sz.cells.Select(toMap).Where(c => CanHoldZone(c, map, sz)).ToList();
                if (cells.Count == 0) continue;
                Zone zone = null;
                if (mergeIntoOriginal && !sz.whole)
                    zone = map.zoneManager.AllZones.FirstOrDefault(z => z.ID == sz.id && KindOf(z) == sz.kind);
                bool made = zone == null;
                if (made)
                {
                    zone = Make(sz, map);
                    if (zone == null) continue;
                }
                foreach (IntVec3 c in cells) zone.AddCell(c);
                if (made && sz.hidden) zone.Hidden = true;
            }
        }

        private static Zone Make(StoredZone sz, Map map)
        {
            ZoneManager zm = map.zoneManager;
            Zone zone;
            switch (sz.kind)
            {
                case Stockpile:
                    var sp = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, zm);
                    if (sz.settings != null) sp.settings.CopyFrom(sz.settings);
                    zone = sp;
                    break;
                case Growing:
                    var g = new Zone_Growing(zm);
                    if (sz.plantDef != null) g.SetPlantDefToGrow(sz.plantDef);
                    g.allowSow = sz.allowSow;
                    g.allowCut = sz.allowCut;
                    zone = g;
                    break;
                default:
                    return null;
            }
            // A whole zone keeps its name (unless this map already uses it) and colour; a split-off part gets its
            // own, so it can be told apart from the zone it was cut from
            if (sz.whole)
            {
                if (!sz.label.NullOrEmpty() && !zm.AllZones.Any(z => z.label == sz.label)) zone.label = sz.label;
                zone.color = sz.color;
            }
            zm.RegisterZone(zone);
            return zone;
        }

        private static bool CanHoldZone(IntVec3 c, Map map, StoredZone sz)
        {
            if (!c.InBounds(map) || map.zoneManager.ZoneAt(c) != null) return false;
            List<Thing> things = c.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
                if (!things[i].def.CanOverlapZones) return false;
            // A growing zone carried onto bare rock would only be an empty outline
            if (sz.kind == Growing && map.fertilityGrid.FertilityAt(c) <= 0f) return false;
            return true;
        }

        // Takes every area's cells inside the rectangle off the map
        public static List<StoredArea> LiftAreas(Map map, CellRect rect, Func<IntVec3, IntVec3> store)
        {
            var result = new List<StoredArea>();
            foreach (Area area in map.areaManager.AllAreas)
            {
                StoredArea sa = null;
                foreach (IntVec3 c in rect)
                {
                    if (!area[c]) continue;
                    if (sa == null) sa = new StoredArea { type = area.GetType().FullName, label = area.Label, color = area.Color };
                    sa.cells.Add(store(c));
                    area[c] = false;
                }
                if (sa != null) result.Add(sa);
            }
            return result;
        }

        // Sets stored area cells down. Areas only ever gain cells here, so what the map had already stays.
        public static void SetDownAreas(List<StoredArea> areas, Map map, Func<IntVec3, IntVec3> toMap)
        {
            if (areas.NullOrEmpty()) return;
            AreaManager am = map.areaManager;
            foreach (StoredArea sa in areas)
            {
                if (sa?.cells == null || sa.type == null) continue;
                Type type = GenTypes.GetTypeInAnyAssembly(sa.type);
                if (type == null) continue;
                Area area;
                if (typeof(Area_Allowed).IsAssignableFrom(type))
                {
                    area = am.GetLabeled(sa.label) as Area_Allowed;
                    if (area == null)
                    {
                        if (!am.TryMakeNewAllowed(out Area_Allowed madeArea))
                        {
                            Messages.Message("EF_Space_AreaLimit".Translate(sa.label), MessageTypeDefOf.NegativeEvent, false);
                            continue;
                        }
                        if (!sa.label.NullOrEmpty()) madeArea.SetLabel(sa.label);
                        madeArea.SetColor(sa.color);
                        area = madeArea;
                    }
                }
                else area = am.AllAreas.FirstOrDefault(a => a.GetType() == type);
                if (area == null) continue;
                foreach (IntVec3 local in sa.cells)
                {
                    IntVec3 c = toMap(local);
                    if (c.InBounds(map)) area[c] = true;
                }
            }
        }
    }
}
