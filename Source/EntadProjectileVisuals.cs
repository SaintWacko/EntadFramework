using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // A trail of flecks behind every shot the weapon fires
    public class EntadVisual_ProjectileTrail : EntadVisual
    {
        public FleckDef fleck;
        // Flecks per tile of flight, spread along the path the shot covered since the last tick. Per tile and not per
        // tick because a bullet crosses about a tile a tick, so a per-tick count left sparse dots a tile apart.
        public float perCell = 3f;
        public FloatRange scale = new FloatRange(0.15f, 0.25f);
        public FloatRange speed = new FloatRange(0f, 0.2f);
        public FloatRange rotationRate = FloatRange.Zero;
        public float jitter = 0.05f;
        public Color color = Color.white;

        public virtual void Spawn(Vector3 from, Vector3 to, Map map)
        {
            float n = perCell * Mathf.Max(0.3f, Vector3.Distance(from, to));
            while (n > 0f)
            {
                if (n < 1f && !Rand.Chance(n)) break;
                n -= 1f;
                Vector3 p = Vector3.Lerp(from, to, Rand.Value) + new Vector3(Rand.Range(-jitter, jitter), 0f, Rand.Range(-jitter, jitter));
                var data = FleckMaker.GetDataStatic(p, map, fleck, scale.RandomInRange);
                data.velocityAngle = Rand.Range(0f, 360f);
                data.velocitySpeed = speed.RandomInRange;
                data.rotationRate = rotationRate.RandomInRange;
                data.rotation = Rand.Range(0f, 360f);
                if (color != Color.white) data.instanceColor = color;
                map.flecks.CreateFleck(data);
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (fleck == null) yield return $"{defName}: projectile trail visual needs a fleck";
            if (perCell <= 0f) yield return $"{defName}: projectile trail perCell must be above 0";
        }
    }

    // Little lightning zaps jumping from the shot to spots around it as it flies, in the same arc sprites the weapon
    // crackles with. A trail subclass so the launch hook and the per-tick walk treat it like any other trail; the arcs
    // themselves are drawn every frame by EntadVisuals.DrawZaps, since a stretched sprite isn't something a fleck does.
    public class EntadVisual_ProjectileArcs : EntadVisual_ProjectileTrail
    {
        [NoTranslate] public string texFolder = "Things/Mote/EntadArc";
        public Color arcColor = new Color(0.7f, 0.85f, 1f, 0.9f);
        // How far from the shot a zap lands, its width in cells, and how many game ticks it shows
        public FloatRange reach = new FloatRange(0.5f, 1.3f);
        public float width = 0.18f;
        // Short, because the near end follows the shot: a bullet flies about a tile a tick, so each tick stretches it
        public int arcTicks = 2;
        // Spread of zap directions, centred on the shot's heading (360 = any direction)
        public float arcAngle = 180f;

        [Unsaved] private List<Material> materials;

        public EntadVisual_ProjectileArcs() { perCell = 0.8f; }

        internal List<Material> Materials
        {
            get
            {
                if (materials == null)
                {
                    materials = new List<Material>();
                    foreach (var tex in ContentFinder<Texture2D>.GetAllInFolder(texFolder))
                        materials.Add(MaterialPool.MatFrom(tex, ShaderDatabase.MoteGlow, arcColor));
                    if (materials.Count == 0) Log.ErrorOnce($"[Entad Framework] no textures in arc folder '{texFolder}'", texFolder.GetHashCode());
                }
                return materials;
            }
        }

        public override void Spawn(Vector3 from, Vector3 to, Map map)
        {
            var mats = Materials;
            if (mats.Count == 0) return;
            float n = perCell * Mathf.Max(0.3f, Vector3.Distance(from, to));
            while (n > 0f)
            {
                if (n < 1f && !Rand.Chance(n)) break;
                n -= 1f;
                Vector3 a = Vector3.Lerp(from, to, Rand.Value);
                // Only ahead of the shot (within arcAngle/2 of its heading), so the zaps lead the bullet instead of trailing it
                Vector3 heading = to - from;
                float headingDeg = heading.MagnitudeHorizontalSquared() > 0.0001f ? -Mathf.Atan2(heading.z, heading.x) * Mathf.Rad2Deg : Rand.Range(0f, 360f);
                float half = arcAngle * 0.5f;
                Vector3 b = a + Quaternion.AngleAxis(headingDeg + Rand.Range(-half, half), Vector3.up) * new Vector3(reach.RandomInRange, 0f, 0f);
                EntadVisuals.AddZap(map, a, b, mats.RandomElement(), width, arcTicks);
                if (fleck != null)
                {
                    var data = FleckMaker.GetDataStatic(b, map, fleck, scale.RandomInRange);
                    data.velocityAngle = Rand.Range(0f, 360f);
                    data.velocitySpeed = speed.RandomInRange;
                    if (color != Color.white) data.instanceColor = color;
                    map.flecks.CreateFleck(data);
                }
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (perCell <= 0f) yield return $"{defName}: projectile arcs perCell must be above 0";
            if (arcTicks < 1) yield return $"{defName}: projectile arcs arcTicks must be at least 1";
        }
    }

    public static partial class EntadVisuals
    {
        // Thing rather than Projectile so a module can register shots that aren't vanilla projectiles (Combat
        // Extended's ProjectileCE); everything here only needs DrawPos, Map and Spawned
        private class Trail
        {
            public Thing projectile;
            public Vector3 last;
            public bool hasLast;
            public int stillTicks;
            public EntadVisual_ProjectileTrail[] visuals;
        }
        private static readonly List<Trail> trails = new List<Trail>();

        public static void Launched(Thing projectile, Thing equipment)
        {
            if (!EntadSettings.Visuals || !EntadSettings.VisualsProjectiles || !(equipment is ThingWithComps eq)) return;
            var set = eq.GetComp<CompEntad>()?.Visuals;
            if (set == null || set.trails.Length == 0) return;
            trails.Add(new Trail { projectile = projectile, visuals = set.trails });
        }

        // Every game tick while a shot with a trail is in the air
        public static void TickTrails()
        {
            // Expired zaps also go here, not only in DrawZaps: nothing draws them while the world map is open, and
            // shots on the colony map keep adding them meanwhile
            int now = Find.TickManager.TicksGame;
            for (int i = zaps.Count - 1; i >= 0; i--)
                if (now >= zaps[i].until) zaps.RemoveAt(i);
            Map current = Find.CurrentMap;
            CellRect view = current != null ? Find.CameraDriver.CurrentViewRect.ExpandedBy(2) : CellRect.Empty;
            for (int i = trails.Count - 1; i >= 0; i--)
            {
                var trail = trails[i];
                var projectile = trail.projectile;
                if (projectile.Destroyed || !projectile.Spawned) { trails.RemoveAt(i); continue; }
                // Forget the last spot while the shot is on another map, or switching to it would spawn one long burst
                // over the whole unseen stretch
                if (projectile.Map != current) { trail.hasLast = false; continue; }
                Vector3 pos = projectile.DrawPos;
                pos.y = AltitudeLayer.MoteLow.AltitudeFor();
                // A shot that stopped has landed but is still spawned: CE's delayed-fuse grenades and shells, and its
                // flares, sit where they fell until they go off, and would otherwise keep shedding flecks on the spot.
                // Counted over ticks rather than dropped on the first still one, because a vanilla shot out of view
                // only moves every 15 ticks (Projectile.UpdateRateTicks).
                if (trail.hasLast && (pos - trail.last).MagnitudeHorizontalSquared() < 0.0001f)
                {
                    if (++trail.stillTicks > 20) trails.RemoveAt(i);
                    continue;
                }
                trail.stillTicks = 0;
                Vector3 from = trail.hasLast ? trail.last : pos;
                trail.last = pos;
                trail.hasLast = true;
                if (!view.Contains(pos.ToIntVec3()) || !pos.ShouldSpawnMotesAt(current)) continue;
                // Seeded from the shot and tick so that where the camera is doesn't change the game's random stream
                spawningFor = projectile;
                Rand.PushState(Gen.HashCombineInt(projectile.thingIDNumber, now));
                try
                {
                    for (int k = 0; k < trail.visuals.Length; k++) trail.visuals[k].Spawn(from, pos, current);
                }
                finally
                {
                    Rand.PopState();
                    spawningFor = null;
                }
            }
        }

        public static bool AnyTrails => trails.Count > 0;
        public static bool AnyZaps => zaps.Count > 0;

        private class Zap
        {
            public Map map;
            public Thing projectile;
            public Vector3 near;
            public Vector3 far;
            public Material mat;
            public float width;
            public int until;
        }

        // Zaps from shots (EntadVisual_ProjectileArcs). They expire by game tick, so a pause holds them on screen like
        // the weapon's own arcs; a bullet lives a few ticks, so the list stays a handful long. The near end follows the
        // shot every frame and the far end stays where it struck, so a zap reads as the bullet arcing to the ground
        // rather than a stray bolt it left behind (which is how they looked paused when both ends were fixed).
        private static readonly List<Zap> zaps = new List<Zap>();

        // The shot TickTrails is spawning for, so the trail visuals don't need a projectile in their signature
        internal static Thing spawningFor;

        internal static void AddZap(Map map, Vector3 a, Vector3 b, Material mat, float width, int ticks)
        {
            zaps.Add(new Zap
            {
                map = map,
                projectile = spawningFor,
                near = a,
                far = b,
                mat = mat,
                width = width,
                until = Find.TickManager.TicksGame + ticks,
            });
        }

        internal static void DrawZaps(Map map)
        {
            if (zaps.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            float y = AltitudeLayer.MoteOverhead.AltitudeFor();
            for (int i = zaps.Count - 1; i >= 0; i--)
            {
                var z = zaps[i];
                if (now >= z.until) { zaps.RemoveAt(i); continue; }
                if (z.map != map) continue;
                // Once the shot lands the zap keeps its last spot for the few ticks it has left
                if (z.projectile != null && z.projectile.Spawned && !z.projectile.Destroyed) z.near = z.projectile.DrawPos;
                Vector3 dir = z.far - z.near;
                float length = Mathf.Sqrt(dir.MagnitudeHorizontalSquared());
                if (length < 0.05f) continue;
                float deg = -Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
                Vector3 mid = (z.near + z.far) * 0.5f;
                mid.y = y;
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(mid, Quaternion.AngleAxis(deg, Vector3.up), new Vector3(length * 1.15f, 1f, z.width)), z.mat, 0);
            }
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new[] {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags),
        typeof(bool), typeof(Thing), typeof(ThingDef) })]
    public static class Patch_Projectile_Launch_Visuals
    {
        public static void Postfix(Projectile __instance, Thing equipment)
        {
            if (equipment == null || !EntadTraitDef.AnyTrailVisuals) return;
            EntadVisuals.Launched(__instance, equipment);
        }
    }
}
