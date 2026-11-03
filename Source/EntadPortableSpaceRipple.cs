using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // The channel's ripple (JobDriver_UsePortableSpace). Visual only: the room still moves all at once when the
    // channel ends (EntadPortableSpace.Place / PickUp), so nothing here touches the map or is saved.
    //
    // Setting the room down, a ring grows out from where the pawn stands and leaves ghosts of the room's floors,
    // buildings, plants and items behind it. Folding an area up (capture or pick-up), the ring starts at the far edge
    // and closes in on the pawn, dimming the cells it has passed. Either way it reaches its end a little before the
    // channel does, so the swap lands on a finished picture.
    //
    // Every piece carries a key from 0 to 1, the progress at which the ring reaches it, and each list is sorted by key,
    // so a frame only walks the part already reached. Floors and the dimming are one mesh per material whose
    // triangle count grows as the ring passes, not a draw call per cell.
    internal class SpaceRipple
    {
        // Fixed colours: GhostUtility caches a ghost graphic per colour, so a fading alpha would fill its cache
        private static readonly Color GhostColor = new Color(0.75f, 0.85f, 1f, 0.45f);
        private static readonly Color FloorTint = new Color(0.85f, 0.9f, 1f, 0.55f);
        private static readonly Color FoldedTint = new Color(0.08f, 0.05f, 0.16f, 0.5f);

        // Share of the channel the ring takes to cross the area
        private const float RippleEnd = 0.9f;
        // Dust and shimmer at the ring's front, at most this many cells a tick however fast it moves
        private const int MaxFrontEffectsPerTick = 6;

        private struct Ghost
        {
            public ThingDef def;
            public Graphic graphic;
            public Thing thing;
            public IntVec3 pos;
            public Rot4 rot;
            public float key;
        }

        private readonly JobDriver driver;
        public readonly Map map;
        private readonly int workTicks;
        private readonly List<Ghost> ghosts = new List<Ghost>();
        private readonly List<RevealMesh> meshes = new List<RevealMesh>();
        private readonly List<(IntVec3 cell, float key)> front = new List<(IntVec3, float)>();
        private int nextFront;
        private bool ended;

        private SpaceRipple(JobDriver driver, Map map, int workTicks)
        {
            this.driver = driver;
            this.map = map;
            this.workTicks = Mathf.Max(1, workTicks);
        }

        public float Progress => Mathf.Clamp01((1f - driver.ticksLeftThisToil / (float)workTicks) / RippleEnd);

        public bool Live => !ended && driver.pawn.jobs?.curDriver == driver && driver.pawn.Map == map;

        public void End() => ended = true;

        // 'from' is where the pawn stands; origin, rot, w and h are the room's placement, as the job carries them
        public static SpaceRipple Start(JobDriver driver, PortableSpace space, IntVec3 from, IntVec3 origin, Rot4 rot, int w, int h, int workTicks)
        {
            Map map = driver.pawn.Map;
            bool unfolding = space.state == PortableSpaceState.Holding;
            // A pick-up folds the rectangle the room was set down on, which the job also names
            CellRect rect = EntadPortableSpace.RectAt(origin, rot, w, h);
            var ripple = new SpaceRipple(driver, map, workTicks);

            Vector3 centre = from.ToVector3Shifted();
            float maxDist = 0.5f;
            foreach (IntVec3 c in rect) maxDist = Mathf.Max(maxDist, (c.ToVector3Shifted() - centre).MagnitudeHorizontal() + 0.5f);
            float KeyAt(Vector3 at)
            {
                float d = Mathf.Clamp01((at - centre).MagnitudeHorizontal() / maxDist);
                return unfolding ? d : 1f - d;
            }

            foreach (IntVec3 c in rect) ripple.front.Add((c, KeyAt(c.ToVector3Shifted())));
            ripple.front.SortBy(f => f.key);

            if (unfolding)
            {
                ripple.AddFloors(space, origin, rot, w, h, KeyAt);
                ripple.AddGhosts(space, origin, rot, w, h, KeyAt);
            }
            else
                ripple.meshes.Add(new RevealMesh(ripple.front, SolidColorMaterials.SimpleSolidColorMaterial(FoldedTint),
                    AltitudeLayer.MetaOverlays.AltitudeFor(), Color.white));

            // Begun partway (a load mid-channel, or the map only now being looked at): no burst for the cells already passed
            float p = ripple.Progress;
            while (ripple.nextFront < ripple.front.Count && ripple.front[ripple.nextFront].key <= p) ripple.nextFront++;
            return ripple;
        }

        private void AddFloors(PortableSpace space, IntVec3 origin, Rot4 rot, int w, int h, System.Func<Vector3, float> keyAt)
        {
            // The map's own terrain material for each floor and paint, so a ghost floor tiles exactly like the real one
            var byMaterial = new Dictionary<Material, List<(IntVec3, float)>>();
            foreach (var sc in space.roomCells)
            {
                if (sc.floor == null) continue;
                IntVec3 c = EntadPortableSpace.ToMap(sc.cell, origin, rot, w, h);
                if (!c.InBounds(map)) continue;
                Material mat = map.terrainGrid.GetMaterial(sc.floor, false, sc.color);
                if (mat == null) continue;
                if (!byMaterial.TryGetValue(mat, out var cells)) byMaterial[mat] = cells = new List<(IntVec3, float)>();
                cells.Add((c, keyAt(c.ToVector3Shifted())));
            }
            // Just under the building ghosts, over whatever is standing there now
            float y = AltitudeLayer.Blueprint.AltitudeFor() - 0.01f;
            foreach (var pair in byMaterial)
            {
                pair.Value.SortBy(f => f.Item2);
                meshes.Add(new RevealMesh(pair.Value, pair.Key, y, FloorTint));
            }
        }

        private void AddGhosts(PortableSpace space, IntVec3 origin, Rot4 rot, int w, int h, System.Func<Vector3, float> keyAt)
        {
            space.PruneRoom();
            foreach (var st in space.room)
            {
                ThingDef def = st.thing.def;
                bool shown = def.category == ThingCategory.Building || def.category == ThingCategory.Item || def.category == ThingCategory.Plant;
                if (!shown || def.graphicData == null || EntadPortableSpace.TurnBlocks(st, rot)) continue;
                EntadPortableSpace.Transform(def, st.pos, st.rot, l => EntadPortableSpace.ToMap(l, origin, rot, w, h), rot.AsInt, st.thing.Rotation, out IntVec3 p, out Rot4 r);
                if (!p.InBounds(map)) continue;
                // A held thing is off every map; a graphic getter written for spawned things (some modded plants and
                // buildings read their Map) would throw here, and that thing just goes without a ghost
                Graphic graphic;
                try { graphic = st.thing.Graphic; }
                catch { continue; }
                if (graphic == null || graphic == BaseContent.BadGraphic) continue;
                ghosts.Add(new Ghost
                {
                    def = def,
                    graphic = graphic,
                    thing = st.thing,
                    pos = p,
                    rot = r,
                    key = keyAt(GenThing.TrueCenter(p, r, def.size, 0f))
                });
            }
            ghosts.SortBy(g => g.key);
        }

        public void Tick()
        {
            float p = Progress;
            int start = nextFront;
            while (nextFront < front.Count && front[nextFront].key <= p) nextFront++;
            int crossed = nextFront - start;
            if (crossed == 0 || map != Find.CurrentMap) return;
            int step = Mathf.Max(1, Mathf.CeilToInt(crossed / (float)MaxFrontEffectsPerTick));
            for (int i = start; i < nextFront; i += step)
            {
                Vector3 loc = front[i].cell.ToVector3Shifted() + new Vector3(Rand.Range(-0.3f, 0.3f), 0f, Rand.Range(-0.3f, 0.3f));
                if (!loc.ShouldSpawnMotesAt(map, drawOffscreen: false)) continue;
                FleckMaker.Static(loc, map, EntadSpaceFleckDefOf.Entad_SpaceShimmer, Rand.Range(0.8f, 1.3f));
                FleckMaker.ThrowDustPuffThick(loc, map, Rand.Range(0.8f, 1.3f), new Color(1f, 1f, 1f, 0.6f));
                if (Rand.Chance(0.2f)) FleckMaker.ThrowMicroSparks(loc, map);
            }
        }

        public void Draw()
        {
            float p = Progress;
            for (int i = 0; i < meshes.Count; i++) meshes[i].Draw(p);
            for (int i = 0; i < ghosts.Count; i++)
            {
                Ghost g = ghosts[i];
                if (g.key > p) break;
                GhostDrawer.DrawGhostThing(g.pos, g.rot, g.def, g.graphic, GhostColor, AltitudeLayer.Blueprint, g.thing, false, g.thing.Stuff);
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < meshes.Count; i++) meshes[i].Dispose();
            meshes.Clear();
        }
    }

    // One quad per cell in a single mesh, the cells in the order they appear. Only the triangles of the cells
    // reached so far are set, and only when that count changes.
    internal class RevealMesh
    {
        private readonly Mesh mesh = new Mesh();
        private readonly Material mat;
        private readonly int[] tris;
        private readonly float[] keys;
        private int shown;

        public RevealMesh(List<(IntVec3 cell, float key)> cells, Material mat, float y, Color color)
        {
            this.mat = mat;
            var verts = new List<Vector3>(cells.Count * 4);
            var colors = new List<Color>(cells.Count * 4);
            tris = new int[cells.Count * 6];
            keys = new float[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                IntVec3 c = cells[i].cell;
                keys[i] = cells[i].key;
                int v = verts.Count;
                verts.Add(new Vector3(c.x, y, c.z));
                verts.Add(new Vector3(c.x, y, c.z + 1));
                verts.Add(new Vector3(c.x + 1, y, c.z + 1));
                verts.Add(new Vector3(c.x + 1, y, c.z));
                for (int k = 0; k < 4; k++) colors.Add(color);
                int t = i * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }
            mesh.MarkDynamic();
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
        }

        public void Draw(float progress)
        {
            int reached = shown;
            while (reached < keys.Length && keys[reached] <= progress) reached++;
            if (reached != shown)
            {
                mesh.SetTriangles(tris, 0, reached * 6, 0);
                shown = reached;
            }
            if (shown > 0) Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, 0);
        }

        public void Dispose() => Object.Destroy(mesh);
    }

    // Draws the ripples of every channel on the map being looked at. Not saved: a channel loaded mid-way starts its
    // ripple again on its next tick (JobDriver_UsePortableSpace). With no channel running this is an empty-list check
    // per frame.
    public class EntadSpaceRipples : GameComponent
    {
        private static readonly List<SpaceRipple> active = new List<SpaceRipple>();

        public EntadSpaceRipples(Game game)
        {
            for (int i = 0; i < active.Count; i++) active[i].Dispose();
            active.Clear();
        }

        internal static void Add(SpaceRipple ripple) => active.Add(ripple);

        public override void GameComponentUpdate()
        {
            if (active.Count == 0) return;
            Map current = Find.CurrentMap;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                SpaceRipple r = active[i];
                if (!r.Live)
                {
                    r.Dispose();
                    active.RemoveAt(i);
                    continue;
                }
                if (r.map == current && !WorldRendererUtility.WorldRendered) r.Draw();
            }
        }
    }
}
