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
        // The item's own texture as drawn this frame, for the tint
        public Texture texture;
        public float size;
        public int seed;
        public int ticks;
        // Game ticks since this weapon last spawned flecks (0 while paused), capped so a weapon coming back into view
        // doesn't burst out a pile of them
        public int newTicks;
        public float altitude;
        // Worn apparel is drawn on a pawn mesh whose top edge is raised by this much (vanilla's back lift, so a
        // pawn's head sorts over the layer below it at every height). A flat quad just over the item's altitude sank
        // under all of it but the bottom edge, so whatever covers the item uses a lifted quad, and anything drawn
        // at a single spot is raised by the lift at that spot.
        public float lift;

        private static Mesh liftedPlane, liftedPlaneFlip;

        // A 1x1 quad, raised along its top edge when the item's own mesh is
        public Mesh Plane
        {
            get
            {
                if (lift <= 0f) return flipped ? MeshPool.plane10Flip : MeshPool.plane10;
                if (liftedPlane == null)
                {
                    liftedPlane = MeshMakerPlanes.NewPlaneMesh(1f, false, true);
                    liftedPlaneFlip = MeshMakerPlanes.NewPlaneMesh(1f, true, true);
                }
                return flipped ? liftedPlaneFlip : liftedPlane;
            }
        }

        // A spot on the weapon's opaque pixels, in world space
        public Vector3 Point(int index)
        {
            Vector2 p = shape.points[(index & 0x7FFFFFFF) % shape.points.Length];
            float x = flipped ? -p.x : p.x;
            Vector3 world = matrix.MultiplyPoint3x4(new Vector3(x, 0f, p.y));
            // Vanilla's lifted quad (MeshMakerPlanes.NewPlaneMesh) is two triangles split on the bottom-left to
            // top-right diagonal: the top edge at the full lift, bottom left at 0, bottom right at 0.4 of it
            world.y = altitude + (lift <= 0f ? 0f : p.y >= x ? lift * (p.y + 0.5f) : lift * (0.4f * x + 0.6f * p.y + 0.5f));
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
        // Strength of a wash of the colour over the item itself, as a fraction of the glow's. The halo is additive, so
        // on a pale shirt it only whitens and the colour shows round the edges; the wash dyes the item (its own
        // texture times the colour, blended over it) so the colour reads on any cloth.
        public float tint;

        private static readonly Dictionary<Texture, Material> tintMaterials = new Dictionary<Texture, Material>();

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (tint < 0f) yield return $"{defName}: weapon glow tint can't be negative";
        }

        public override void Draw(EntadWeaponDraw d)
        {
            float phase = (d.seed & 0xFF) / 255f;
            // Times worked out in double and wrapped: a float of the raw tick count is too coarse a few years into a game,
            // and the pulse and flicker would step instead of moving. Unity's Perlin noise should repeat every 256 units
            // (its permutation table), so that wrap has no seam; if it doesn't, the flicker jumps once every half minute.
            float cycle = (float)((d.ticks / 60.0 * pulseSpeed + phase) % 1.0);
            float wave = pulseSpeed > 0f ? (Mathf.Sin(cycle * Mathf.PI * 2f) + 1f) * 0.5f : 1f;
            float noise = flicker > 0f ? Mathf.PerlinNoise((float)((d.ticks * (double)flickerSpeed) % 256.0), phase * 37f) : 0f;
            Color c = color;
            c.a *= Mathf.Lerp(pulseMin, 1f, wave) * (1f - flicker * noise);
            var block = EntadVisuals.glowBlock;
            Mesh mesh = d.Plane;
            if (tint > 0f && d.texture != null)
            {
                if (!tintMaterials.TryGetValue(d.texture, out var tintMat))
                {
                    tintMat = new Material(ShaderDatabase.Transparent) { mainTexture = d.texture, name = "EntadTint_" + d.texture.name };
                    tintMaterials[d.texture] = tintMat;
                }
                Color t = c;
                t.a = Mathf.Clamp01(c.a * tint);
                block.SetColor(ShaderPropertyIDs.Color, t);
                Matrix4x4 tm = d.matrix;
                tm.m13 = d.altitude - 0.0002f;
                Graphics.DrawMesh(mesh, tm, tintMat, 0, null, 0, block);
            }
            block.SetColor(ShaderPropertyIDs.Color, c);
            Matrix4x4 m = d.matrix * Matrix4x4.Scale(new Vector3(EntadWeaponShape.HaloScale, 1f, EntadWeaponShape.HaloScale));
            m.m13 = d.altitude;
            Graphics.DrawMesh(mesh, m, d.shape.HaloMaterial, 0, null, 0, block);
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

        public override void Draw(EntadWeaponDraw d)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.TransparentPostLight, color, "flame");
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

        public override void Draw(EntadWeaponDraw d)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.MoteGlow, color, "arc");
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
            EntadVisuals.DrawArc(mats[((seed >> 4) & 0x7FFFFFFF) % mats.Count], a, b, width, min);
        }
    }

    // Glints twinkling on spots of the item, drawn at the item's own height rather than as flecks: a fleck sits over
    // everything, so a glint on a shirt's collar showed on its wearer's face. Each slot twinkles on its own clock and
    // moves to a new spot every time, from the game tick, so they hold still while paused.
    public class EntadVisual_WeaponGlints : EntadWeaponVisual
    {
        [NoTranslate] public string texPath = "Things/Mote/EntadGlint";
        public Color color = new Color(0.92f, 0.96f, 1f, 0.9f);
        public int count = 2;
        // Each slot shows one glint per period, lasting glintTicks
        public IntRange periodTicks = new IntRange(90, 200);
        public int glintTicks = 24;
        // Size in cells at the brightest
        public FloatRange size = new FloatRange(0.18f, 0.3f);

        [Unsaved] private Material material;

        public override void Draw(EntadWeaponDraw d)
        {
            material ??= MaterialPool.MatFrom(texPath, ShaderDatabase.MoteGlow, Color.white);
            var block = EntadVisuals.glowBlock;
            for (int i = 0; i < count; i++)
            {
                int seed = Gen.HashCombineInt(d.seed, i * 6151 + 29);
                int period = Mathf.Max(glintTicks + 1, periodTicks.min + (seed & 0x7FFFFFFF) % Mathf.Max(1, periodTicks.max - periodTicks.min + 1));
                int t = d.ticks + ((seed >> 8) & 0xFFFF);
                int phase = t % period;
                if (phase >= glintTicks) continue;
                float u = phase / (float)glintTicks;
                float strength = Mathf.Sin(u * Mathf.PI);
                int spot = EntadGlint.Mix(seed ^ (t / period) * 0x2C1B3C6D);
                Vector3 pos = d.Point(spot);
                // A flat quad on the slope of a lifted pawn mesh: lowered to the middle of the gap to the next apparel
                // layer, so neither edge goes through it
                if (d.lift > 0f) pos.y -= 0.00012f;
                float s = Mathf.Lerp(size.min, size.max, ((spot >> 12) & 0xFF) / 255f) * strength;
                Color c = color;
                c.a *= strength;
                block.SetColor(ShaderPropertyIDs.Color, c);
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.AngleAxis(u * 45f + (spot & 0x3F), Vector3.up), new Vector3(s, 1f, s)), material, 0, null, 0, block);
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (texPath.NullOrEmpty()) yield return $"{defName}: weapon glints visual needs a texPath";
            if (glintTicks < 1) yield return $"{defName}: weapon glints glintTicks must be at least 1";
            if (periodTicks.min <= glintTicks) yield return $"{defName}: weapon glints periodTicks must be longer than glintTicks";
        }
    }

    // A bright band sweeping across the item every few seconds, for a polished surface. The band is masked to the
    // item's own shape: a set of frames is made once per texture from its alpha (EntadWeaponShape.mask) and the
    // two frames either side of the band's position are cross-faded, so it moves smoothly with few frames.
    public class EntadVisual_WeaponSheen : EntadWeaponVisual
    {
        public Color color = new Color(1f, 1f, 1f, 0.55f);
        // One sweep per period, lasting sweepTicks
        public IntRange periodTicks = new IntRange(240, 420);
        public int sweepTicks = 45;
        // Width of the band as a fraction of the item's size, and its slant from vertical in degrees
        public float width = 0.16f;
        public float angle = 30f;
        public int frames = 16;

        private readonly Dictionary<EntadWeaponShape, Material[]> frameCache = new Dictionary<EntadWeaponShape, Material[]>();

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (sweepTicks < 1) yield return $"{defName}: weapon sheen sweepTicks must be at least 1";
            if (periodTicks.min <= sweepTicks) yield return $"{defName}: weapon sheen periodTicks must be longer than sweepTicks";
            if (frames < 2) yield return $"{defName}: weapon sheen needs at least 2 frames";
            if (width <= 0f) yield return $"{defName}: weapon sheen width must be above 0";
        }

        public override void Draw(EntadWeaponDraw d)
        {
            int seed = Gen.HashCombineInt(d.seed, 0x5EE7);
            int period = Mathf.Max(1, periodTicks.min + (seed & 0x7FFFFFFF) % Mathf.Max(1, periodTicks.max - periodTicks.min + 1));
            int phase = (d.ticks + ((seed >> 8) & 0xFFFF)) % period;
            if (phase >= sweepTicks) return;
            var mats = FramesFor(d.shape);
            if (mats == null || mats.Length < 2) return;
            float at = phase / (float)sweepTicks * (mats.Length - 1);
            int a = Mathf.Min((int)at, mats.Length - 2);
            float f = at - a;
            Matrix4x4 m = d.matrix;
            m.m13 = d.altitude - 0.0001f;
            Mesh mesh = d.Plane;
            var block = EntadVisuals.glowBlock;
            Color c = color;
            c.a = color.a * (1f - f);
            block.SetColor(ShaderPropertyIDs.Color, c);
            Graphics.DrawMesh(mesh, m, mats[a], 0, null, 0, block);
            c.a = color.a * f;
            block.SetColor(ShaderPropertyIDs.Color, c);
            Graphics.DrawMesh(mesh, m, mats[a + 1], 0, null, 0, block);
        }

        private Material[] FramesFor(EntadWeaponShape shape)
        {
            if (frameCache.TryGetValue(shape, out var mats)) return mats;
            mats = null;
            float[] mask = shape.mask;
            if (mask != null)
            {
                int n = EntadWeaponShape.Size;
                float rad = angle * Mathf.Deg2Rad;
                // Across the band: x and z of the item's square, turned by the slant
                float ax = Mathf.Cos(rad), az = -Mathf.Sin(rad);
                float reach = 0.5f * (Mathf.Abs(ax) + Mathf.Abs(az)) + width;
                mats = new Material[frames];
                var colors = new Color32[n * n];
                for (int k = 0; k < frames; k++)
                {
                    float centre = Mathf.Lerp(-reach, reach, k / (float)(frames - 1));
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            float u = (x + 0.5f) / n - 0.5f, v = (y + 0.5f) / n - 0.5f;
                            float dist = Mathf.Abs(u * ax + v * az - centre) / width;
                            // A soft band with a brighter core
                            float band = dist >= 1f ? 0f : (1f - dist) * (1f - dist) * 0.7f + (dist < 0.3f ? 0.3f * (1f - dist / 0.3f) : 0f);
                            colors[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(band * mask[y * n + x]) * 255f));
                        }
                    var tex = new Texture2D(n, n, TextureFormat.ARGB32, false) { name = "EntadSheen_" + k, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    tex.SetPixels32(colors);
                    tex.Apply(false, true);
                    mats[k] = new Material(ShaderDatabase.MoteGlow) { mainTexture = tex, name = tex.name };
                }
            }
            frameCache[shape] = mats;
            return mats;
        }
    }

    // Soft patches of colour crawling up the item, masked to its shape: flames licking over a burning coat, or static
    // or frost with other colours. Like the sheen, a looping set of frames is made once per texture from its alpha
    // (EntadWeaponShape.mask), so it fits any apparel or weapon with no art; the two frames either side of the moment
    // are cross-faded. The pattern is 3D value noise: scrolled up a whole number of wraps per loop, and moved through
    // its third axis one wrap per loop, so the patches change shape as they rise and the loop has no seam. Scrolling
    // alone showed the same lick passing over and over.
    public class EntadVisual_WeaponFlow : EntadWeaponVisual
    {
        public Color color = new Color(1f, 0.45f, 0.08f, 0.6f);
        // The brightest part of each patch blends towards this colour (fire's yellow core); alpha unused
        public Color coreColor = new Color(1f, 0.85f, 0.4f, 1f);
        // Noise above threshold shows, and from coreThreshold up it turns to the core colour
        public float threshold = 0.45f;
        public float coreThreshold = 0.75f;
        // Noise cells across and up the item's square texture (most items fill only the middle of it): more across
        // than up gives taller patches, like flames
        public int cellsAcross = 7;
        public int cellsUp = 4;
        // Cells the shapes change through per loop; more = faster change
        public int evolveCells = 3;
        // Ticks for one loop, how many times the pattern rises the item's height in it, and frames in the loop
        public int loopTicks = 300;
        public int scrolls = 2;
        public int frames = 24;
        // Brightness pulse on top, as in the glow
        public float pulseSpeed = 0f;
        public float pulseMin = 1f;
        // Added light (fire, sparks) or blended colour (frost, smoke, anything that should show on white cloth)
        public bool additive = true;

        private readonly Dictionary<EntadWeaponShape, Material[]> frameCache = new Dictionary<EntadWeaponShape, Material[]>();

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (frames < 2 || frames > 64) yield return $"{defName}: weapon flow frames must be from 2 to 64";
            if (loopTicks < 1) yield return $"{defName}: weapon flow loopTicks must be at least 1";
            if (cellsAcross < 1 || cellsUp < 1 || evolveCells < 1) yield return $"{defName}: weapon flow cellsAcross, cellsUp and evolveCells must be at least 1";
            if (scrolls < 0) yield return $"{defName}: weapon flow scrolls must not be negative";
            if (threshold < 0f || threshold >= 1f) yield return $"{defName}: weapon flow threshold must be from 0 to below 1";
        }

        public override void Draw(EntadWeaponDraw d)
        {
            var mats = FramesFor(d.shape);
            if (mats == null || mats.Length < 2 || loopTicks < 1) return;
            int seed = Gen.HashCombineInt(d.seed, 0xF10);
            float at = (d.ticks + ((seed >> 4) & 0xFFFF)) % loopTicks / (float)loopTicks * mats.Length;
            int a = (int)at % mats.Length;
            int b = (a + 1) % mats.Length;
            float f = at - (int)at;
            float strength = color.a;
            if (pulseSpeed > 0f)
            {
                float cycle = (float)((d.ticks / 60.0 * pulseSpeed + (seed & 0xFF) / 255f) % 1.0);
                strength *= Mathf.Lerp(pulseMin, 1f, (Mathf.Sin(cycle * Mathf.PI * 2f) + 1f) * 0.5f);
            }
            Matrix4x4 m = d.matrix;
            // Over the tint, under the sheen and the halo
            m.m13 = d.altitude - 0.00015f;
            Mesh mesh = d.Plane;
            var block = EntadVisuals.glowBlock;
            block.SetColor(ShaderPropertyIDs.Color, new Color(1f, 1f, 1f, strength * (1f - f)));
            Graphics.DrawMesh(mesh, m, mats[a], 0, null, 0, block);
            // Added light sums to the full strength; two blended layers cover less than their sum, so the second is
            // raised to keep the blend at full strength between frames
            float second = additive ? strength * f : strength * f / Mathf.Max(0.0001f, 1f - strength * (1f - f));
            block.SetColor(ShaderPropertyIDs.Color, new Color(1f, 1f, 1f, Mathf.Clamp01(second)));
            Graphics.DrawMesh(mesh, m, mats[b], 0, null, 0, block);
        }

        private Material[] FramesFor(EntadWeaponShape shape)
        {
            if (frameCache.TryGetValue(shape, out var mats)) return mats;
            mats = null;
            float[] mask = shape.mask;
            if (mask != null && frames >= 2)
            {
                int n = EntadWeaponShape.Size;
                mats = new Material[frames];
                var colors = new Color32[n * n];
                Shader shader = additive ? ShaderDatabase.MoteGlow : ShaderDatabase.Transparent;
                for (int k = 0; k < frames; k++)
                {
                    float t = k / (float)frames;
                    for (int y = 0; y < n; y++)
                        for (int x = 0; x < n; x++)
                        {
                            float alpha = mask[y * n + x];
                            if (alpha <= 0f) { colors[y * n + x] = default; continue; }
                            float u = (x + 0.5f) / n, v = (y + 0.5f) / n;
                            float rise = v - t * scrolls;
                            float value = 0.65f * Noise(u * cellsAcross, rise * cellsUp, t * evolveCells, cellsAcross, cellsUp, evolveCells, 17)
                                + 0.35f * Noise(u * cellsAcross * 2, rise * cellsUp * 2, t * evolveCells * 2, cellsAcross * 2, cellsUp * 2, evolveCells * 2, 91);
                            float show = Mathf.Clamp01((value - threshold) / (1f - threshold));
                            show = show * show * (3f - 2f * show);
                            float core = coreThreshold < 1f ? Mathf.Clamp01((value - coreThreshold) / (1f - coreThreshold)) : 0f;
                            Color c = Color.Lerp(color, coreColor, core);
                            c.a = show * alpha;
                            colors[y * n + x] = c;
                        }
                    var tex = new Texture2D(n, n, TextureFormat.ARGB32, false) { name = "EntadFlow_" + k, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
                    tex.SetPixels32(colors);
                    tex.Apply(false, true);
                    mats[k] = new Material(shader) { mainTexture = tex, name = tex.name };
                }
            }
            frameCache[shape] = mats;
            return mats;
        }

        // Smooth value noise on a grid that wraps every wrapX by wrapY by wrapZ cells
        private static float Noise(float x, float y, float z, int wrapX, int wrapY, int wrapZ, int salt)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
            float fx = Smooth(x - x0), fy = Smooth(y - y0), fz = Smooth(z - z0);
            float Layer(int zc) => Mathf.Lerp(
                Mathf.Lerp(Cell(x0, y0, zc, wrapX, wrapY, wrapZ, salt), Cell(x0 + 1, y0, zc, wrapX, wrapY, wrapZ, salt), fx),
                Mathf.Lerp(Cell(x0, y0 + 1, zc, wrapX, wrapY, wrapZ, salt), Cell(x0 + 1, y0 + 1, zc, wrapX, wrapY, wrapZ, salt), fx), fy);
            return Mathf.Lerp(Layer(z0), Layer(z0 + 1), fz);
        }

        private static float Smooth(float f) => f * f * (3f - 2f * f);

        private static float Cell(int x, int y, int z, int wrapX, int wrapY, int wrapZ, int salt)
        {
            x = (x % wrapX + wrapX) % wrapX;
            y = (y % wrapY + wrapY) % wrapY;
            z = (z % wrapZ + wrapZ) % wrapZ;
            return (EntadGlint.Mix(Gen.HashCombineInt(Gen.HashCombineInt(x * 7349 + salt, y * 3517), z * 9241)) & 0xFFFF) / 65535f;
        }
    }
}
