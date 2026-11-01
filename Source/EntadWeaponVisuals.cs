using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Drawn on a weapon where vanilla draws it: in a pawn's hands while drafted, aiming or carrying it openly, and
    // lying on the map. Everything here runs per frame on the main thread, from the weapon's own draw.
    public abstract class EntadWeaponVisual : EntadVisual
    {
        public abstract void Draw(EntadWeaponDraw draw);
    }

    // Shared state for one weapon's draw this frame, filled once and handed to each of its visuals
    public class EntadWeaponDraw
    {
        public Thing weapon;
        public Map map;
        public Matrix4x4 matrix;
        public bool flipped;
        public EntadWeaponShape shape;
        public float size;
        public int seed;
        public int ticks;
        // Game ticks since this weapon last spawned flecks (0 while paused), capped so a weapon coming back into view
        // doesn't burst out a pile of them
        public int newTicks;
        public float altitude;

        // A spot on the weapon's opaque pixels, in world space
        public Vector3 Point(int index)
        {
            Vector2 p = shape.points[(index & 0x7FFFFFFF) % shape.points.Length];
            Vector3 world = matrix.MultiplyPoint3x4(new Vector3(flipped ? -p.x : p.x, 0f, p.y));
            world.y = altitude;
            return world;
        }
    }

    // A coloured halo around the weapon's silhouette, pulsing and flickering. The halo is the weapon's own alpha,
    // blurred outward (EntadWeaponShape), so it fits any weapon with no extra art.
    public class EntadVisual_WeaponGlow : EntadWeaponVisual
    {
        public Color color = new Color(1f, 0.5f, 0.15f, 0.5f);
        // Pulses per second, and the strength at the bottom of a pulse as a fraction of the colour's alpha
        public float pulseSpeed = 0.6f;
        public float pulseMin = 0.6f;
        // Random flicker on top of the pulse, as a fraction of the strength (fire 0.2, static 0.5)
        public float flicker = 0.15f;
        public float flickerSpeed = 0.15f;

        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();

        public override void Draw(EntadWeaponDraw d)
        {
            float phase = (d.seed & 0xFF) / 255f;
            float wave = pulseSpeed > 0f ? (Mathf.Sin((d.ticks / 60f * pulseSpeed + phase) * Mathf.PI * 2f) + 1f) * 0.5f : 1f;
            float noise = flicker > 0f ? Mathf.PerlinNoise(d.ticks * flickerSpeed, phase * 37f) : 0f;
            Color c = color;
            c.a *= Mathf.Lerp(pulseMin, 1f, wave) * (1f - flicker * noise);
            block.SetColor(ShaderPropertyIDs.Color, c);
            Matrix4x4 m = d.matrix * Matrix4x4.Scale(new Vector3(EntadWeaponShape.HaloScale, 1f, EntadWeaponShape.HaloScale));
            m.m13 = d.altitude;
            Graphics.DrawMesh(d.flipped ? MeshPool.plane10Flip : MeshPool.plane10, m, d.shape.HaloMaterial, 0, null, 0, block);
        }
    }

    // Small flickering fire sprites on the weapon, rising from spots on its blade or body, like Bastle Framework's
    // flaming creatures. Each flame keeps its spot; vanilla's fire frames by default.
    public class EntadVisual_WeaponFlames : EntadWeaponVisual
    {
        [NoTranslate] public string texFolder = "Things/Special/Fire";
        public int count = 3;
        // Sprite size as a fraction of the weapon's draw size
        public FloatRange size = new FloatRange(0.22f, 0.32f);
        public Color color = new Color(1f, 1f, 1f, 0.85f);
        public int ticksPerFrame = 10;

        [Unsaved] private List<Material> materials;

        private List<Material> Materials
        {
            get
            {
                if (materials == null)
                {
                    materials = new List<Material>();
                    foreach (var tex in ContentFinder<Texture2D>.GetAllInFolder(texFolder))
                        materials.Add(MaterialPool.MatFrom(tex, ShaderDatabase.TransparentPostLight, color));
                    if (materials.Count == 0) Log.ErrorOnce($"[Entad Framework] no textures in flame folder '{texFolder}'", texFolder.GetHashCode());
                }
                return materials;
            }
        }

        public override void Draw(EntadWeaponDraw d)
        {
            var mats = Materials;
            if (mats.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                int seed = Gen.HashCombineInt(d.seed, i * 7919 + 17);
                // A flame's spot moves now and then, so a still weapon doesn't burn in the same three places forever
                int frame = (d.ticks + (seed & 0xFFFF)) / Mathf.Max(1, ticksPerFrame);
                Vector3 pos = d.Point(EntadGlint.Mix(seed ^ (frame / 12) * 0x2C1B3C6D));
                float s = Mathf.Lerp(size.min, size.max, Rand.ValueSeeded(seed + 2)) * d.size;
                // Fire sprites sit on their base, so the flame rises from the spot
                pos.z += s * 0.35f;
                pos.y += 0.001f * (i + 1);
                var mat = mats[((frame ^ seed) & 0x7FFFFFFF) % mats.Count];
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(s, 1f, s)), mat, 0);
            }
        }
    }

    // Flecks thrown off the weapon: embers, sparks, frost mist. Spawned on game ticks only, so nothing piles up while
    // the game is paused, and only for weapons being drawn, so nothing spawns off screen.
    public class EntadVisual_WeaponFlecks : EntadWeaponVisual
    {
        public FleckDef fleck;
        public float perSecond = 3f;
        public FloatRange scale = new FloatRange(0.1f, 0.2f);
        // Direction (0 = up the screen) and speed in cells per second
        public FloatRange angle = new FloatRange(-30f, 30f);
        public FloatRange speed = new FloatRange(0.2f, 0.5f);
        public FloatRange rotationRate = FloatRange.Zero;
        public Color color = Color.white;

        public override void Draw(EntadWeaponDraw d)
        {
            if (d.newTicks <= 0 || fleck == null) return;
            float chance = perSecond / 60f;
            for (int t = 0; t < d.newTicks; t++)
            {
                if (!Rand.Chance(chance)) continue;
                Vector3 pos = d.Point(Rand.Int);
                if (!pos.ShouldSpawnMotesAt(d.map)) continue;
                var data = FleckMaker.GetDataStatic(pos, d.map, fleck, scale.RandomInRange);
                data.velocityAngle = angle.RandomInRange;
                data.velocitySpeed = speed.RandomInRange;
                data.rotationRate = rotationRate.RandomInRange;
                data.rotation = Rand.Range(0f, 360f);
                if (color != Color.white) data.instanceColor = color;
                d.map.flecks.CreateFleck(data);
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (fleck == null) yield return $"{defName}: weapon flecks visual needs a fleck";
        }
    }

    // Static arcs jumping between two spots on the weapon for a few ticks at a time. Picked from the game tick and
    // the weapon's id, so there's no state to keep and an arc holds still while the game is paused.
    public class EntadVisual_WeaponArcs : EntadWeaponVisual
    {
        [NoTranslate] public string texFolder = "Things/Mote/EntadArc";
        public Color color = new Color(0.7f, 0.85f, 1f, 0.9f);
        // Each interval may hold one arc, shown for arcTicks
        public int intervalTicks = 24;
        public float chance = 0.6f;
        public int arcTicks = 5;
        // Width in cells, and the shortest arc as a fraction of the weapon's draw size
        public float width = 0.16f;
        public float minLength = 0.25f;

        [Unsaved] private List<Material> materials;

        private List<Material> Materials
        {
            get
            {
                if (materials == null)
                {
                    materials = new List<Material>();
                    foreach (var tex in ContentFinder<Texture2D>.GetAllInFolder(texFolder))
                        materials.Add(MaterialPool.MatFrom(tex, ShaderDatabase.MoteGlow, color));
                    if (materials.Count == 0) Log.ErrorOnce($"[Entad Framework] no textures in arc folder '{texFolder}'", texFolder.GetHashCode());
                }
                return materials;
            }
        }

        public override void Draw(EntadWeaponDraw d)
        {
            var mats = Materials;
            if (mats.Count == 0) return;
            int interval = Mathf.Max(arcTicks + 1, intervalTicks);
            int t = d.ticks + (d.seed & 0xFFFF);
            int cycle = t / interval;
            if (t % interval >= arcTicks) return;
            int seed = EntadGlint.Mix(d.seed ^ cycle * 0x2C1B3C6D);
            if ((seed & 0xFFFF) / 65536f >= chance) return;
            Vector3 a = d.Point(seed);
            Vector3 b = a;
            float min = minLength * d.size;
            // A few tries for a far enough second spot; a tiny weapon may not have one, and then there's no arc
            for (int k = 1; k <= 6; k++)
            {
                b = d.Point(Gen.HashCombineInt(seed, k));
                if ((b - a).MagnitudeHorizontalSquared() >= min * min) break;
            }
            Vector3 dir = b - a;
            float length = Mathf.Sqrt(dir.MagnitudeHorizontalSquared());
            if (length < min) return;
            float deg = -Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
            Vector3 mid = (a + b) * 0.5f;
            mid.y += 0.002f;
            var mat = mats[((seed >> 4) & 0x7FFFFFFF) % mats.Count];
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(mid, Quaternion.AngleAxis(deg, Vector3.up), new Vector3(length * 1.15f, 1f, width)), mat, 0);
        }
    }
}
