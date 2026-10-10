using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace EntadFramework
{
    // Portable space (trait property portableSpace, worn apparel or a wielded weapon). The item holds one rectangle of a map.
    //
    // Empty: the holder captures a rectangle of exactly the rolled size. Everything in it except pawns goes into the
    // item: buildings, items, plants, chunks, filth, blueprints and frames, built floors and built roofs. Natural
    // terrain, natural rock and thick roofs stay on the map.
    // Holding: the holder sets the room down somewhere, rotated as they like. What is already there is swapped into
    // the item and kept apart from the room: player buildings, plants, chunks, filth, floors and roofs. Other items
    // and pawns are pushed out of the rectangle. Natural rock, non-player buildings and fog refuse the spot.
    // Placed: the item remembers the map, rectangle and rotation, and can only pick that same rectangle up again. The
    // room comes back into the item as it is now (anything built inside meanwhile included) and the swapped-out
    // contents go back exactly where they were.
    //
    // Things are moved the way Odyssey moves a gravship (GravshipUtility.GenerateGravship and
    // GravshipPlacementUtility): PreSwapMap, DeSpawn(WillReplace) so they keep their state, deep-saved while away,
    // then GenSpawn and PostSwapMap. None of that needs Odyssey.
    public enum PortableSpaceState
    {
        Empty,
        Holding,
        Placed
    }

    // A thing held by a portable space and where it goes: in room coordinates for the room, in map coordinates for
    // what the room displaced
    public class StoredThing : IExposable
    {
        public Thing thing;
        public IntVec3 pos;
        public Rot4 rot;

        public void ExposeData()
        {
            Scribe_Deep.Look(ref thing, "thing");
            Scribe_Values.Look(ref pos, "pos");
            Scribe_Values.Look(ref rot, "rot");
        }
    }

    // A cell's built floor (with its paint) and roof
    public class StoredCell : IExposable
    {
        public IntVec3 cell;
        public TerrainDef floor;
        public ColorDef color;
        public RoofDef roof;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref floor, "floor");
            Scribe_Defs.Look(ref color, "color");
            Scribe_Defs.Look(ref roof, "roof");
        }
    }

    public class PortableSpace : IExposable
    {
        public PortableSpaceState state = PortableSpaceState.Empty;

        // The room, in room coordinates: (0,0) to (width-1, height-1) as captured
        public List<StoredThing> room = new List<StoredThing>();
        public List<StoredCell> roomCells = new List<StoredCell>();

        // While placed: where, and what the room displaced (map coordinates)
        public int mapId = -1;
        public IntVec3 origin;
        public Rot4 rot;
        public List<StoredThing> displaced = new List<StoredThing>();
        public List<StoredCell> displacedCells = new List<StoredCell>();

        public bool IsEmpty => state == PortableSpaceState.Empty;

        // Thing's private flag behind PreSwapMap. Null if a game version renames it: things then load unflagged.
        private static readonly HarmonyLib.AccessTools.FieldRef<Thing, bool> BeingMoved = MakeBeingMoved();

        private static HarmonyLib.AccessTools.FieldRef<Thing, bool> MakeBeingMoved()
        {
            var field = HarmonyLib.AccessTools.Field(typeof(Thing), "beingTransportedOnGravship");
            return field != null ? HarmonyLib.AccessTools.FieldRefAccess<Thing, bool>(field) : null;
        }

        public Map PlacedMap
        {
            get
            {
                if (state != PortableSpaceState.Placed) return null;
                var maps = Find.Maps;
                for (int i = 0; i < maps.Count; i++)
                    if (maps[i].uniqueID == mapId) return maps[i];
                return null;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref state, "state", PortableSpaceState.Empty);
            Scribe_Collections.Look(ref room, "room", LookMode.Deep);
            Scribe_Collections.Look(ref roomCells, "roomCells", LookMode.Deep);
            Scribe_Values.Look(ref mapId, "mapId", -1);
            Scribe_Values.Look(ref origin, "origin");
            Scribe_Values.Look(ref rot, "rot");
            Scribe_Collections.Look(ref displaced, "displaced", LookMode.Deep);
            Scribe_Collections.Look(ref displacedCells, "displacedCells", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                room = room ?? new List<StoredThing>();
                roomCells = roomCells ?? new List<StoredCell>();
                displaced = displaced ?? new List<StoredThing>();
                displacedCells = displacedCells ?? new List<StoredCell>();
                room.RemoveAll(s => s?.thing == null);
                displaced.RemoveAll(s => s?.thing == null);
                // The 'being moved' flag PreSwapMap set isn't saved; without it they'd spawn as newly built
                // (plants re-checking leaflessness, quest spawn signals, spawn-time comp logic). Only the flag is set:
                // PreSwapMap itself runs comp logic that needs a map (the fleshmass heart's reads Map).
                if (BeingMoved != null)
                {
                    foreach (var s in room) BeingMoved(s.thing) = true;
                    foreach (var s in displaced) BeingMoved(s.thing) = true;
                }
            }
        }
    }

    public static class EntadPortableSpace
    {
        // Work per cell: 150 ticks per 10 cells, so a 5x5 takes about 6 seconds and a 21x21 about a minute
        public const int TicksPerCell = 15;

        public static int Width(AppliedEntadTrait trait) => trait.spaceValues.Count > 0 ? Mathf.Max(1, Mathf.RoundToInt(trait.spaceValues[0])) : 1;
        public static int Height(AppliedEntadTrait trait) => trait.spaceValues.Count > 1 ? Mathf.Max(1, Mathf.RoundToInt(trait.spaceValues[1])) : 1;

        public static PortableSpace SpaceOf(AppliedEntadTrait trait) => trait.space ?? (trait.space = new PortableSpace());

        public static int WorkTicks(AppliedEntadTrait trait) => Mathf.Max(60, Width(trait) * Height(trait) * TicksPerCell);

        // ---- Geometry ----

        // The map rectangle a w x h room covers at this origin and rotation (quarter turns swap the sides)
        public static CellRect RectAt(IntVec3 origin, Rot4 rot, int w, int h)
        {
            bool turned = rot.IsHorizontal;
            return new CellRect(origin.x, origin.z, turned ? h : w, turned ? w : h);
        }

        // Origin that puts the rectangle's middle under the mouse
        public static IntVec3 OriginFor(IntVec3 mouse, Rot4 rot, int w, int h)
        {
            bool turned = rot.IsHorizontal;
            int rw = turned ? h : w, rh = turned ? w : h;
            return new IntVec3(mouse.x - (rw - 1) / 2, 0, mouse.z - (rh - 1) / 2);
        }

        // Room cell to map cell. Each quarter turn is clockwise seen from above: north becomes east.
        public static IntVec3 ToMap(IntVec3 local, IntVec3 origin, Rot4 rot, int w, int h)
        {
            int x = local.x, z = local.z;
            switch (rot.AsInt)
            {
                case 1: return new IntVec3(origin.x + z, 0, origin.z + (w - 1 - x));
                case 2: return new IntVec3(origin.x + (w - 1 - x), 0, origin.z + (h - 1 - z));
                case 3: return new IntVec3(origin.x + (h - 1 - z), 0, origin.z + x);
                default: return new IntVec3(origin.x + x, 0, origin.z + z);
            }
        }

        public static IntVec3 ToLocal(IntVec3 cell, IntVec3 origin, Rot4 rot, int w, int h)
        {
            int dx = cell.x - origin.x, dz = cell.z - origin.z;
            switch (rot.AsInt)
            {
                case 1: return new IntVec3(w - 1 - dz, 0, dx);
                case 2: return new IntVec3(w - 1 - dx, 0, h - 1 - dz);
                case 3: return new IntVec3(dz, 0, h - 1 - dx);
                default: return new IntVec3(dx, 0, dz);
            }
        }

        // Where a thing goes after its cells are mapped through 'mapCell' and it turns 'turns' quarter turns. Worked
        // out from the footprint rather than the position, because a building with an even side doesn't sit on its
        // middle cell and rotating its position alone would shift it by one.
        public static void Transform(ThingDef def, IntVec3 pos, Rot4 thingRot, System.Func<IntVec3, IntVec3> mapCell, int turns,
            out IntVec3 newPos, out Rot4 newRot)
        {
            newRot = def.rotatable ? new Rot4((thingRot.AsInt + turns + 4) % 4) : thingRot;
            if (def.size.x == 1 && def.size.z == 1) { newPos = mapCell(pos); return; }
            CellRect from = GenAdj.OccupiedRect(pos, thingRot, def.size);
            IntVec3 a = mapCell(from.Min), b = mapCell(from.Max);
            var target = CellRect.FromLimits(a, b);
            foreach (IntVec3 c in target)
                if (GenAdj.OccupiedRect(c, newRot, def.size).Equals(target)) { newPos = c; return; }
            newPos = mapCell(pos);
        }

        // ---- What counts as what ----

        private static bool Ignored(Thing t)
        {
            if (t is Pawn || t is Mote || t is Skyfaller || t is PawnFlyer || t is Projectile) return true;
            if (t.def.category == ThingCategory.Ethereal && !(t is Blueprint) && !(t is Frame)) return true;
            return false;
        }

        private static bool IsNaturalRock(Thing t) => t.def.building != null && t.def.building.isNaturalRock;

        private static bool IsChunk(Thing t) => t.def.category == ThingCategory.Item && t.def.IsWithinCategory(ThingCategoryDefOf.Chunks);

        // Buildings the space may not take or cover: someone else's, or ones that can't be destroyed (map features)
        private static bool IsForeignBuilding(Thing t) =>
            t.def.category == ThingCategory.Building && !IsNaturalRock(t) && (t.Faction != Faction.OfPlayer || !t.def.destroyable);

        // Things whose origin cell is in the rectangle; a thing can cover several cells, so each is listed once
        private static List<Thing> ThingsIn(Map map, CellRect rect)
        {
            var seen = new HashSet<Thing>();
            var list = new List<Thing>();
            foreach (IntVec3 c in rect)
            {
                var here = map.thingGrid.ThingsListAtFast(c);
                for (int i = 0; i < here.Count; i++)
                    if (seen.Add(here[i])) list.Add(here[i]);
            }
            return list;
        }

        // ---- Checks ----

        private static AcceptanceReport CheckArea(Map map, CellRect rect, bool placing)
        {
            if (!rect.InBounds(map)) return "EF_Space_OutOfBounds".Translate();
            foreach (IntVec3 c in rect)
                if (c.Fogged(map)) return "EF_Space_Fogged".Translate();
            foreach (Thing t in ThingsIn(map, rect))
            {
                if (Ignored(t)) continue;
                if (IsNaturalRock(t))
                {
                    if (placing) return "EF_Space_NaturalRock".Translate();
                    continue;
                }
                if (IsForeignBuilding(t)) return "EF_Space_ForeignBuilding".Translate(t.LabelShort);
                // Pawns and pushed items are moved aside, so only what stays in the rectangle must fit inside it
                bool moves = placing && t.def.category == ThingCategory.Item && !IsChunk(t);
                CellRect occ = t.OccupiedRect();
                if (!moves && !(rect.Contains(occ.Min) && rect.Contains(occ.Max))) return "EF_Space_CrossesEdge".Translate(t.LabelShort);
            }
            return true;
        }

        public static AcceptanceReport CanCapture(Map map, CellRect rect) => CheckArea(map, rect, false);

        public static AcceptanceReport CanPlace(PortableSpace space, Map map, IntVec3 origin, Rot4 rot, int w, int h)
        {
            CellRect rect = RectAt(origin, rot, w, h);
            var report = CheckArea(map, rect, true);
            if (!report.Accepted) return report;

            // The ground left once the target's own floors are swapped out, with the room's floors on top
            var floors = new Dictionary<IntVec3, TerrainDef>();
            foreach (var sc in space.roomCells)
            {
                if (sc.floor == null) continue;
                IntVec3 c = ToMap(sc.cell, origin, rot, w, h);
                TerrainDef ground = NaturalTerrain(map, c);
                if (sc.floor.terrainAffordanceNeeded != null && !ground.affordances.Contains(sc.floor.terrainAffordanceNeeded))
                    return "EF_Space_Unsupported".Translate(sc.floor.label, ground.label);
                floors[c] = sc.floor;
            }
            foreach (var st in space.room)
            {
                var need = st.thing.def.terrainAffordanceNeeded;
                if (st.thing.def.category != ThingCategory.Building || need == null) continue;
                Transform(st.thing.def, st.pos, st.rot, l => ToMap(l, origin, rot, w, h), rot.AsInt, out IntVec3 p, out Rot4 r);
                foreach (IntVec3 c in GenAdj.OccupiedRect(p, r, st.thing.def.size))
                {
                    TerrainDef ground = floors.TryGetValue(c, out var f) ? f : NaturalTerrain(map, c);
                    if (!ground.affordances.Contains(need)) return "EF_Space_Unsupported".Translate(st.thing.LabelShort, ground.label);
                }
            }
            return true;
        }

        // The cell's terrain without a built floor on top
        private static TerrainDef NaturalTerrain(Map map, IntVec3 c) => map.terrainGrid.UnderTerrainAt(c) ?? map.terrainGrid.TerrainAt(c);

        // ---- Moving things ----

        // Lower numbers spawn first: buildings, then what attaches to them, then everything that sits on them.
        // Despawning runs the other way.
        private static int SpawnOrder(Thing t)
        {
            if (t.def.category == ThingCategory.Building) return t.def.building != null && t.def.building.isAttachment ? 1 : 0;
            return 2;
        }

        // Lifts every thing in the list, last-spawned first. Like GenerateGravship, every thing is flagged as being
        // moved before any of them despawns, so a despawning building never sees an unflagged neighbour.
        private static void LiftAll(List<Thing> things, Map map)
        {
            things.SortByDescending(SpawnOrder);
            things.RemoveAll(t => t.Destroyed || !t.Spawned);
            foreach (Thing t in things)
            {
                map.reservationManager.ReleaseAllForTarget(t);
                map.physicalInteractionReservationManager.ReleaseAllForTarget(t);
                map.designationManager.RemoveAllDesignationsOn(t);
                t.PreSwapMap();
            }
            var fogBlockers = new List<Thing>();
            foreach (Thing t in things)
            {
                if (!t.Spawned) continue;
                t.DeSpawn(DestroyMode.WillReplace);
                if (t.def.MakeFog) fogBlockers.Add(t);
            }
            // DeSpawn skips the fog update under WillReplace: a lifted wall must still reveal what was behind it
            foreach (Thing t in fogBlockers) map.fogGrid.Notify_FogBlockerRemoved(t);
        }

        // Spawns stored things in order, pushing pawns off each footprint first
        private static void SetDown(List<(Thing thing, IntVec3 pos, Rot4 rot)> things, Map map, CellRect keepOut)
        {
            things.SortBy(x => SpawnOrder(x.thing));
            var spawned = new List<Thing>();
            foreach (var (thing, pos, rot) in things)
            {
                if (thing.Destroyed) continue;
                if (thing.def.category == ThingCategory.Building)
                    foreach (IntVec3 c in GenAdj.OccupiedRect(pos, rot, thing.def.size)) PushPawnsFrom(c, map, keepOut);
                GenSpawn.Spawn(thing, pos, map, rot, WipeMode.VanishOrMoveAside);
                spawned.Add(thing);
            }
            foreach (Thing t in spawned) if (!t.Destroyed) t.PostSwapMap();
        }

        // Moves pawns standing in a cell to the nearest standable cell outside 'keepOut'
        private static void PushPawnsFrom(IntVec3 cell, Map map, CellRect keepOut)
        {
            var here = map.thingGrid.ThingsListAt(cell);
            for (int i = here.Count - 1; i >= 0; i--)
                if (here[i] is Pawn p) PushPawn(p, map, keepOut);
        }

        private static void PushPawn(Pawn p, Map map, CellRect keepOut)
        {
            if (!TryFindCellOutside(p.Position, map, keepOut, out IntVec3 dest)) return;
            p.Position = dest;
            p.Notify_Teleported(endCurrentJob: true, resetTweenedPos: true);
        }

        private static bool TryFindCellOutside(IntVec3 near, Map map, CellRect keepOut, out IntVec3 dest)
        {
            int radius = Mathf.Max(keepOut.Width, keepOut.Height) + 2;
            for (int r = 2; r <= radius + 10; r += 2)
                if (CellFinder.TryFindRandomCellNear(near, map, r, c => !keepOut.Contains(c) && c.Standable(map) && !c.Fogged(map), out dest))
                    return true;
            dest = IntVec3.Invalid;
            return false;
        }

        private static void PushItem(Thing t, Map map, CellRect keepOut)
        {
            IntVec3 from = t.Position;
            t.DeSpawn();
            if (!GenPlace.TryPlaceThing(t, from, map, ThingPlaceMode.Near, null, c => !keepOut.Contains(c), null, Mathf.Max(keepOut.Width, keepOut.Height))
                && !GenPlace.TryPlaceThing(t, from, map, ThingPlaceMode.Near))
                GenSpawn.Spawn(t, from, map); // nowhere else: leave it where it was rather than lose it
        }

        // Takes the rectangle's floors and built roofs off the map
        private static List<StoredCell> LiftCells(Map map, CellRect rect, bool allRoofs)
        {
            var cells = new List<StoredCell>();
            foreach (IntVec3 c in rect)
            {
                var sc = new StoredCell { cell = c };
                if (map.terrainGrid.UnderTerrainAt(c) != null)
                {
                    sc.floor = map.terrainGrid.TerrainAt(c);
                    sc.color = map.terrainGrid.ColorAt(c);
                    map.terrainGrid.RemoveTopLayer(c, false);
                }
                RoofDef roof = map.roofGrid.RoofAt(c);
                if (roof != null && (allRoofs || !roof.isThickRoof))
                {
                    sc.roof = roof;
                    map.roofGrid.SetRoof(c, null);
                }
                if (sc.floor != null || sc.roof != null) cells.Add(sc);
            }
            return cells;
        }

        private static void SetDownFloors(IEnumerable<(IntVec3 cell, StoredCell sc)> cells, Map map)
        {
            foreach (var (c, sc) in cells)
                if (sc.floor != null)
                {
                    map.terrainGrid.SetTerrain(c, sc.floor);
                    if (sc.color != null) map.terrainGrid.SetTerrainColor(c, sc.color);
                }
        }

        private static void SetDownRoofs(IEnumerable<(IntVec3 cell, StoredCell sc)> cells, Map map)
        {
            foreach (var (c, sc) in cells)
                if (sc.roof != null) map.roofGrid.SetRoof(c, sc.roof);
        }

        // ---- The three actions ----

        // Lifts the rectangle into the room store, in room coordinates. Shared by capture and pick-up.
        private static void LiftRoom(PortableSpace space, Map map, IntVec3 origin, Rot4 rot, int w, int h)
        {
            CellRect rect = RectAt(origin, rot, w, h);
            var things = ThingsIn(map, rect).Where(t => !Ignored(t) && !IsNaturalRock(t)).ToList();
            int back = (4 - rot.AsInt) % 4;
            var stored = new List<StoredThing>();
            foreach (Thing t in things)
            {
                Transform(t.def, t.Position, t.Rotation, c => ToLocal(c, origin, rot, w, h), back, out IntVec3 p, out Rot4 r);
                stored.Add(new StoredThing { thing = t, pos = p, rot = r });
            }
            LiftAll(things, map);
            space.room = stored;
            space.roomCells = LiftCells(map, rect, false);
            foreach (var sc in space.roomCells) sc.cell = ToLocal(sc.cell, origin, rot, w, h);
            AfterChange(map, rect);
        }

        // The moment the area folds away or unfolds: a skip flash and sound at the middle, smaller skip flashes
        // scattered over the area and a dust puff from each cell. Large areas sample their cells so a 21x21 room
        // doesn't spawn hundreds of flecks in one frame.
        private const int MaxPuffs = 80;

        public static void PlayEffect(Map map, CellRect rect, bool unfolding)
        {
            var center = rect.CenterVector3;
            float size = Mathf.Max(rect.Width, rect.Height);
            SoundDef sound = unfolding && SoundDefOf.Psycast_Skip_Exit != null ? SoundDefOf.Psycast_Skip_Exit : SoundDefOf.Psycast_Skip_Entry;
            sound?.PlayOneShot(new TargetInfo(rect.CenterCell, map));

            FleckDef flash = DefDatabase<FleckDef>.GetNamedSilentFail("PlainFlash");
            if (flash != null) FleckMaker.Static(center, map, flash, size * 1.5f);
            FleckMaker.Static(center, map, FleckDefOf.PsycastSkipFlashEntry, size * 0.6f);

            var cells = rect.Cells.ToList();
            if (cells.Count > MaxPuffs) cells = cells.InRandomOrder().Take(MaxPuffs).ToList();
            foreach (IntVec3 c in cells)
            {
                Vector3 loc = c.ToVector3Shifted() + new Vector3(Rand.Range(-0.4f, 0.4f), 0f, Rand.Range(-0.4f, 0.4f));
                FleckMaker.ThrowDustPuffThick(loc, map, Rand.Range(1.2f, 2f), new Color(1f, 1f, 1f, 0.8f));
                if (Rand.Chance(0.15f)) FleckMaker.Static(loc, map, FleckDefOf.PsycastSkipFlashEntry, Rand.Range(0.8f, 1.4f));
            }
        }

        public static void Capture(PortableSpace space, Map map, IntVec3 origin, Rot4 rot, int w, int h)
        {
            LiftRoom(space, map, origin, rot, w, h);
            PlayEffect(map, RectAt(origin, rot, w, h), false);
            space.state = PortableSpaceState.Holding;
        }

        public static void Place(PortableSpace space, Map map, IntVec3 origin, Rot4 rot, int w, int h)
        {
            CellRect rect = RectAt(origin, rot, w, h);

            // Clear the spot: pawns and loose items step aside, the rest is swapped into the item
            foreach (IntVec3 c in rect) PushPawnsFrom(c, map, rect);
            var swap = new List<Thing>();
            foreach (Thing t in ThingsIn(map, rect))
            {
                if (Ignored(t) || IsNaturalRock(t)) continue;
                if (t.def.category == ThingCategory.Item && !IsChunk(t)) PushItem(t, map, rect);
                else swap.Add(t);
            }
            space.displaced = swap.Select(t => new StoredThing { thing = t, pos = t.Position, rot = t.Rotation }).ToList();
            LiftAll(swap, map);
            space.displacedCells = LiftCells(map, rect, true);

            // Set the room down
            var cells = space.roomCells.Select(sc => (ToMap(sc.cell, origin, rot, w, h), sc)).ToList();
            SetDownFloors(cells, map);
            var things = new List<(Thing, IntVec3, Rot4)>();
            foreach (var st in space.room)
            {
                Transform(st.thing.def, st.pos, st.rot, l => ToMap(l, origin, rot, w, h), rot.AsInt, out IntVec3 p, out Rot4 r);
                things.Add((st.thing, p, r));
            }
            SetDown(things, map, rect);
            SetDownRoofs(cells, map);

            space.room = new List<StoredThing>();
            space.roomCells = new List<StoredCell>();
            space.state = PortableSpaceState.Placed;
            space.mapId = map.uniqueID;
            space.origin = origin;
            space.rot = rot;
            AfterChange(map, rect);
            PlayEffect(map, rect, true);
        }

        public static void PickUp(PortableSpace space, Map map, int w, int h)
        {
            IntVec3 origin = space.origin;
            Rot4 rot = space.rot;
            CellRect rect = RectAt(origin, rot, w, h);
            LiftRoom(space, map, origin, rot, w, h);

            var cells = space.displacedCells.Select(sc => (sc.cell, sc)).ToList();
            SetDownFloors(cells, map);
            SetDown(space.displaced.Select(st => (st.thing, st.pos, st.rot)).ToList(), map, rect);
            SetDownRoofs(cells, map);

            space.displaced = new List<StoredThing>();
            space.displacedCells = new List<StoredCell>();
            space.state = PortableSpaceState.Holding;
            space.mapId = -1;
            AfterChange(map, rect);
            PlayEffect(map, rect, false);
        }

        private static void AfterChange(Map map, CellRect rect)
        {
            foreach (IntVec3 c in rect)
            {
                map.mapDrawer.MapMeshDirty(c, MapMeshFlagDefOf.Terrain | MapMeshFlagDefOf.Roofs | MapMeshFlagDefOf.Buildings | MapMeshFlagDefOf.Things);
                map.glowGrid.DirtyCell(c);
                map.pathing.RecalculatePerceivedPathCostAt(c);
            }
            map.roofGrid.Drawer.SetDirty();
            RoofCollapseCellsFinder.CheckAndRemoveCollpsingRoofs(map);
        }

        // The placed room's map is gone (abandoned, destroyed): the room went with it and the item is empty again
        public static void CheckPlacedMap(PortableSpace space)
        {
            if (space.state != PortableSpaceState.Placed || space.PlacedMap != null) return;
            space.displaced = new List<StoredThing>();
            space.displacedCells = new List<StoredCell>();
            space.state = PortableSpaceState.Empty;
            space.mapId = -1;
        }

        // The item was destroyed. What it holds lands near where it was; buildings that can't be packed up are lost
        // along with floors and roofs. A placed room stays where it is, and what it displaced lands beside it.
        public static void Spill(PortableSpace space, Map map, IntVec3 near, Thing item)
        {
            if (space == null) return;
            if (space.state == PortableSpaceState.Holding && map == null && space.room.Count > 0)
                Messages.Message((item.Destroyed ? "EF_Space_LostOffMap" : "EF_Space_LostOnRemove").Translate(item.LabelNoCount), MessageTypeDefOf.NegativeEvent, false);
            var loose = new List<Thing>();
            if (space.state == PortableSpaceState.Holding) loose.AddRange(space.room.Select(s => s.thing));
            Map placedMap = space.PlacedMap;
            if (space.state == PortableSpaceState.Placed && placedMap != null)
            {
                foreach (var s in space.displaced) Drop(s.thing, placedMap, space.origin);
            }
            space.room = new List<StoredThing>();
            space.displaced = new List<StoredThing>();
            space.state = PortableSpaceState.Empty;
            if (map == null) return;
            if (!near.IsValid || !near.InBounds(map)) near = DropCellFinder.TradeDropSpot(map);
            foreach (Thing t in loose) Drop(t, map, near);
        }

        private static void Drop(Thing t, Map map, IntVec3 near)
        {
            if (t == null || t.Destroyed) return;
            Thing drop = t;
            if (t.def.category == ThingCategory.Building)
            {
                if (!t.def.Minifiable) return;
                drop = t.MakeMinified();
            }
            else if (t.def.category != ThingCategory.Item) return;
            if (GenPlace.TryPlaceThing(drop, near, map, ThingPlaceMode.Near)) t.PostSwapMap();
        }

        // ---- UI ----

        public static string CardLine(AppliedEntadTrait trait)
        {
            var space = SpaceOf(trait);
            string state;
            switch (space.state)
            {
                case PortableSpaceState.Holding: state = "EF_Space_StateHolding".Translate(); break;
                case PortableSpaceState.Placed: state = "EF_Space_StatePlaced".Translate(); break;
                default: state = "EF_Space_StateEmpty".Translate(); break;
            }
            return "EF_Space_Card".Translate(Width(trait), Height(trait), state);
        }

        // The working pawn's spot: a reachable standable cell just outside the rectangle, nearest the pawn
        public static bool TryFindStandCell(Pawn pawn, CellRect rect, out IntVec3 cell)
        {
            cell = IntVec3.Invalid;
            float best = float.MaxValue;
            foreach (IntVec3 c in rect.ExpandedBy(1).EdgeCells)
            {
                if (!c.InBounds(pawn.Map) || !c.Standable(pawn.Map) || c.Fogged(pawn.Map)) continue;
                float d = (c - pawn.Position).LengthHorizontalSquared;
                if (d >= best || !pawn.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
                best = d;
                cell = c;
            }
            return cell.IsValid;
        }

        public static void StartJob(Pawn pawn, CompEntad comp, int traitIndex, IntVec3 origin, Rot4 rot, IntVec3 stand)
        {
            Job job = JobMaker.MakeJob(EntadSpaceJobDefOf.Entad_UsePortableSpace, stand, origin, comp.parent);
            job.count = traitIndex;
            // A job has no rotation field; takeInventoryDelay is otherwise unused by this driver, so it carries it
            job.takeInventoryDelay = rot.AsInt;
            pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }

    [DefOf]
    public static class EntadSpaceJobDefOf
    {
        public static JobDef Entad_UsePortableSpace;

        static EntadSpaceJobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadSpaceJobDefOf)); }
    }

    [DefOf]
    public static class EntadSpaceFleckDefOf
    {
        public static FleckDef Entad_SpaceShimmer;

        static EntadSpaceFleckDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadSpaceFleckDefOf)); }
    }

    // A: where the pawn stands. B: the room's origin cell. C: the worn item. count: the trait's index on the item.
    // takeInventoryDelay: the rotation. What the job does follows the space's state when it finishes.
    public class JobDriver_UsePortableSpace : JobDriver
    {
        private CompEntad Comp => (job.targetC.Thing as ThingWithComps)?.GetComp<CompEntad>();

        private AppliedEntadTrait Trait
        {
            get
            {
                var comp = Comp;
                return comp != null && job.count >= 0 && job.count < comp.activeTraits.Count ? comp.activeTraits[job.count] : null;
            }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

        private bool StillWorn() => Comp != null && Comp.Holder == pawn && Trait?.def.HasPortableSpace == true;

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOn(() => !StillWorn());
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);

            var trait = Trait;
            int ticks = trait != null ? EntadPortableSpace.WorkTicks(trait) : 60;
            Toil work = Toils_General.Wait(ticks).WithProgressBarToilDelay(TargetIndex.A);
            work.initAction = () =>
            {
                var t = Trait;
                if (t == null) return;
                var rect = EntadPortableSpace.RectAt(job.targetB.Cell, new Rot4(job.takeInventoryDelay), EntadPortableSpace.Width(t), EntadPortableSpace.Height(t));
                pawn.rotationTracker.FaceCell(rect.CenterCell);
            };
            work.tickAction = Shimmer;
            yield return work;

            Toil finish = ToilMaker.MakeToil("UsePortableSpace");
            finish.initAction = () => Finish();
            finish.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return finish;
        }

        // While the pawn works, the air over the area shimmers: a few faint distortions and sparks every few ticks,
        // more for a bigger area. Visual only, and skipped when nobody can see the map.
        private void Shimmer()
        {
            if (!pawn.IsHashIntervalTick(10) || pawn.Map != Find.CurrentMap) return;
            var t = Trait;
            if (t == null) return;
            CellRect rect = EntadPortableSpace.RectAt(job.targetB.Cell, new Rot4(job.takeInventoryDelay), EntadPortableSpace.Width(t), EntadPortableSpace.Height(t));
            Map map = pawn.Map;
            int count = Mathf.Clamp(rect.Area / 20 + 1, 1, 6);
            for (int i = 0; i < count; i++)
            {
                Vector3 loc = new Vector3(Rand.Range(rect.minX, rect.maxX + 1f), 0f, Rand.Range(rect.minZ, rect.maxZ + 1f));
                if (!loc.ShouldSpawnMotesAt(map, drawOffscreen: false)) continue;
                FleckMaker.Static(loc, map, EntadSpaceFleckDefOf.Entad_SpaceShimmer, Rand.Range(0.7f, 1.3f));
                if (Rand.Chance(0.35f)) FleckMaker.ThrowMicroSparks(loc, map);
            }
        }

        private void Finish()
        {
            var comp = Comp;
            var trait = Trait;
            if (comp == null || trait == null) return;
            var space = EntadPortableSpace.SpaceOf(trait);
            int w = EntadPortableSpace.Width(trait), h = EntadPortableSpace.Height(trait);
            IntVec3 origin = job.targetB.Cell;
            var rot = new Rot4(job.takeInventoryDelay);
            Map map = pawn.Map;

            AcceptanceReport report;
            switch (space.state)
            {
                case PortableSpaceState.Empty:
                    report = EntadPortableSpace.CanCapture(map, EntadPortableSpace.RectAt(origin, rot, w, h));
                    if (report.Accepted) EntadPortableSpace.Capture(space, map, origin, rot, w, h);
                    break;
                case PortableSpaceState.Holding:
                    report = EntadPortableSpace.CanPlace(space, map, origin, rot, w, h);
                    if (report.Accepted) EntadPortableSpace.Place(space, map, origin, rot, w, h);
                    break;
                default:
                    if (space.PlacedMap != map) { report = "EF_Space_OtherMap".Translate(); break; }
                    report = EntadPortableSpace.CanCapture(map, EntadPortableSpace.RectAt(space.origin, space.rot, w, h));
                    if (report.Accepted) EntadPortableSpace.PickUp(space, map, w, h);
                    break;
            }
            if (!report.Accepted)
            {
                Messages.Message(report.Reason, pawn, MessageTypeDefOf.RejectInput, false);
                return;
            }
            comp.TryBindOnUse(pawn);
            trait.Reveal(EntadEffectKind.Space);
        }
    }

    // Picks the rectangle to capture, or where to set the room down. R/Q rotate, like placing a building.
    public class Designator_PortableSpace : Designator
    {
        private readonly CompEntad comp;
        private readonly int traitIndex;
        private readonly Pawn pawn;
        private Rot4 placingRot = Rot4.North;

        private IntVec3 cachedMouse = IntVec3.Invalid;
        private Rot4 cachedRot;
        private int cachedFrame = -1;
        private AcceptanceReport cachedReport;

        public Designator_PortableSpace(CompEntad comp, int traitIndex, Pawn pawn)
        {
            this.comp = comp;
            this.traitIndex = traitIndex;
            this.pawn = pawn;
            useMouseIcon = false;
            soundSucceeded = SoundDefOf.Designate_PlaceBuilding;
        }

        private AppliedEntadTrait Trait => traitIndex < comp.activeTraits.Count ? comp.activeTraits[traitIndex] : null;

        public override bool CanRemainSelected()
        {
            var t = Trait;
            return t != null && t.def.HasPortableSpace && comp.Holder == pawn && pawn.Spawned && pawn.Map == Map
                && EntadPortableSpace.SpaceOf(t).state != PortableSpaceState.Placed;
        }

        public override AcceptanceReport CanDesignateCell(IntVec3 loc)
        {
            // The checks scan the whole rectangle, so they run once per frame and mouse cell, not once per draw
            if (loc == cachedMouse && placingRot == cachedRot && Time.frameCount == cachedFrame) return cachedReport;
            cachedMouse = loc;
            cachedRot = placingRot;
            cachedFrame = Time.frameCount;
            return cachedReport = Check(loc);
        }

        private AcceptanceReport Check(IntVec3 loc)
        {
            var t = Trait;
            if (t == null) return false;
            int w = EntadPortableSpace.Width(t), h = EntadPortableSpace.Height(t);
            IntVec3 origin = EntadPortableSpace.OriginFor(loc, placingRot, w, h);
            var space = EntadPortableSpace.SpaceOf(t);
            AcceptanceReport report = space.state == PortableSpaceState.Empty
                ? EntadPortableSpace.CanCapture(Map, EntadPortableSpace.RectAt(origin, placingRot, w, h))
                : EntadPortableSpace.CanPlace(space, Map, origin, placingRot, w, h);
            if (!report.Accepted) return report;
            if (!EntadPortableSpace.TryFindStandCell(pawn, EntadPortableSpace.RectAt(origin, placingRot, w, h), out _))
                return "EF_Space_NoStandCell".Translate(pawn.LabelShort);
            return true;
        }

        public override void DesignateSingleCell(IntVec3 c)
        {
            var t = Trait;
            if (t == null) return;
            int w = EntadPortableSpace.Width(t), h = EntadPortableSpace.Height(t);
            IntVec3 origin = EntadPortableSpace.OriginFor(c, placingRot, w, h);
            if (!EntadPortableSpace.TryFindStandCell(pawn, EntadPortableSpace.RectAt(origin, placingRot, w, h), out IntVec3 stand)) return;
            EntadPortableSpace.StartJob(pawn, comp, traitIndex, origin, placingRot, stand);
            Find.DesignatorManager.Deselect();
        }

        public override void SelectedUpdate()
        {
            var t = Trait;
            IntVec3 mouse = UI.MouseCell();
            if (t == null || !mouse.InBounds(Map)) return;
            int w = EntadPortableSpace.Width(t), h = EntadPortableSpace.Height(t);
            IntVec3 origin = EntadPortableSpace.OriginFor(mouse, placingRot, w, h);
            CellRect rect = EntadPortableSpace.RectAt(origin, placingRot, w, h);
            bool ok = CanDesignateCell(mouse).Accepted;
            Color col = ok ? Designator_Place.CanPlaceColor : Designator_Place.CannotPlaceColor;
            GenDraw.DrawFieldEdges(rect.ClipInsideMap(Map).Cells.ToList(), ok ? Color.white : Color.red);

            var space = EntadPortableSpace.SpaceOf(t);
            if (space.state != PortableSpaceState.Holding) return;
            foreach (var st in space.room)
            {
                ThingDef def = st.thing.def;
                if (def.category != ThingCategory.Building || def.graphic == null) continue;
                EntadPortableSpace.Transform(def, st.pos, st.rot, l => EntadPortableSpace.ToMap(l, origin, placingRot, w, h), placingRot.AsInt, out IntVec3 p, out Rot4 r);
                if (!p.InBounds(Map)) continue;
                GhostDrawer.DrawGhostThing(p, r, def, def.graphic, col, AltitudeLayer.Blueprint, st.thing, false, st.thing.Stuff);
            }
        }

        public override void SelectedProcessInput(Event ev)
        {
            base.SelectedProcessInput(ev);
            if (KeyBindingDefOf.Designator_RotateRight.KeyDownEvent)
            {
                placingRot = placingRot.Rotated(RotationDirection.Clockwise);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                Event.current.Use();
            }
            if (KeyBindingDefOf.Designator_RotateLeft.KeyDownEvent)
            {
                placingRot = placingRot.Rotated(RotationDirection.Counterclockwise);
                SoundDefOf.DragSlider.PlayOneShotOnCamera();
                Event.current.Use();
            }
        }

        public override void DoExtraGuiControls(float leftX, float bottomY)
        {
            DesignatorUtility.GUIDoRotationControls(leftX, bottomY, placingRot, r => placingRot = r);
        }
    }

    // The gizmo. While placed, hovering it outlines the area it will pick up.
    public class Command_PortableSpace : Command_Action
    {
        public Map map;
        public List<IntVec3> highlight;

        public override void GizmoUpdateOnMouseover()
        {
            base.GizmoUpdateOnMouseover();
            if (highlight != null && map == Find.CurrentMap) GenDraw.DrawFieldEdges(highlight, Color.cyan);
        }
    }

    public partial class CompEntad
    {
        public override IEnumerable<Gizmo> CompGetWornGizmosExtra()
        {
            foreach (var g in base.CompGetWornGizmosExtra()) yield return g;
            foreach (var g in SpaceGizmos()) yield return g;
        }

        // Worn apparel (above) and equipped weapons (Patch_EquipmentTracker_GetGizmos_Space) share these
        internal IEnumerable<Gizmo> SpaceGizmos()
        {
            if (activeTraits.NullOrEmpty()) yield break;
            Pawn wearer = Holder;
            if (wearer == null || !wearer.Spawned || !wearer.IsColonistPlayerControlled) yield break;
            lastWearer = wearer; // also catches a wearer from before a load
            if (!ActiveFor(wearer) && !(BindsOnFirstUse && !IsBound)) yield break;
            for (int i = 0; i < activeTraits.Count; i++)
                if (activeTraits[i].def.HasPortableSpace) yield return SpaceGizmo(wearer, i);
        }

        private Gizmo SpaceGizmo(Pawn wearer, int index)
        {
            var trait = activeTraits[index];
            var space = EntadPortableSpace.SpaceOf(trait);
            EntadPortableSpace.CheckPlacedMap(space);
            int w = EntadPortableSpace.Width(trait), h = EntadPortableSpace.Height(trait);
            bool known = trait.IsRevealed(EntadEffectKind.Space);

            string key = space.state == PortableSpaceState.Empty ? "EF_Space_Capture"
                : space.state == PortableSpaceState.Holding ? "EF_Space_Place" : "EF_Space_PickUp";
            var cmd = new Command_PortableSpace
            {
                defaultLabel = known ? key.Translate().ToString() : "EF_Unknown".Translate().ToString(),
                defaultDesc = known ? (key + "Desc").Translate(parent.LabelNoCount, w, h).ToString() : "EF_Space_UnknownDesc".Translate(parent.LabelNoCount).ToString(),
                icon = parent.def.uiIcon,
                iconAngle = parent.def.uiIconAngle,
                defaultIconColor = parent.DrawColor
            };

            if (space.state != PortableSpaceState.Placed)
            {
                cmd.action = () => Find.DesignatorManager.Select(new Designator_PortableSpace(this, index, wearer));
                return cmd;
            }

            Map placed = space.PlacedMap;
            CellRect rect = EntadPortableSpace.RectAt(space.origin, space.rot, w, h);
            cmd.map = placed;
            cmd.highlight = rect.Cells.ToList();
            if (placed != wearer.Map) { cmd.Disable("EF_Space_OtherMap".Translate()); return cmd; }
            cmd.action = () =>
            {
                var report = EntadPortableSpace.CanCapture(placed, rect);
                if (!report.Accepted) { Messages.Message(report.Reason, wearer, MessageTypeDefOf.RejectInput, false); return; }
                if (!EntadPortableSpace.TryFindStandCell(wearer, rect, out IntVec3 stand))
                {
                    Messages.Message("EF_Space_NoStandCell".Translate(wearer.LabelShort), wearer, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                EntadPortableSpace.StartJob(wearer, this, index, space.origin, space.rot, stand);
            };
            return cmd;
        }

        // The pawn wearing or wielding this now, kept past the moment the item is destroyed: a destroyed item has already
        // left its wearer when PostDestroy runs, and its own Position is wherever it last lay on the ground.
        // Not saved: after a load it is set again the first time the holder's gizmos are drawn.
        private Pawn lastWearer;

        public override void Notify_Unequipped(Pawn pawn)
        {
            base.Notify_Unequipped(pawn);
            // A destroyed item is unequipped on its way out, before PostDestroy needs to know who wore it
            if (lastWearer == pawn && !parent.Destroyed) lastWearer = null;
        }

        // Called from PostDestroy (EntadPersona.cs)
        private void SpillSpaces(Map map)
        {
            IntVec3 near = parent.Position;
            if (lastWearer != null && lastWearer.MapHeld == map) near = lastWearer.PositionHeld;
            else if (lastWearer != null) near = IntVec3.Invalid;
            for (int i = 0; i < activeTraits.Count; i++)
                SpillSpace(activeTraits[i], map, near);
        }

        private void SpillSpace(AppliedEntadTrait trait, Map map, IntVec3 near)
        {
            if (trait.space != null && !trait.space.IsEmpty) EntadPortableSpace.Spill(trait.space, map, near, parent);
        }

        // RemoveTrait: whatever the space holds lands next to the item
        private void SpillSpaceOnRemove(AppliedEntadTrait trait)
        {
            Pawn holder = Holder;
            SpillSpace(trait, parent.MapHeld, holder != null && holder.Spawned ? holder.Position : parent.PositionHeld);
        }
    }

    // Equipped weapons: vanilla only asks the weapon's CompEquippable for extra gizmos (ThingComp has no equipped
    // hook), so the space gizmo is appended to the equipment tracker's own list. Shown drafted or not, like worn
    // apparel's. Wraps the iterator rather than replacing it, so other mods' gizmos and postfixes are untouched.
    [HarmonyLib.HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.GetGizmos))]
    public static class Patch_EquipmentTracker_GetGizmos_Space
    {
        public static void Postfix(Pawn_EquipmentTracker __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = With(__result, __instance);
        }

        private static IEnumerable<Gizmo> With(IEnumerable<Gizmo> original, Pawn_EquipmentTracker tracker)
        {
            foreach (var g in original) yield return g;
            var list = tracker.AllEquipmentListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                var comp = list[i].GetComp<CompEntad>();
                if (comp == null) continue;
                foreach (var g in comp.SpaceGizmos()) yield return g;
            }
        }
    }
}
