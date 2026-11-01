using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // One entry of a trait's <visuals> list. An entry plays only while its trait is fully revealed, so a hidden trait
    // never gives itself away by its look; a visible trait (EntadTraitDef.visible) is always revealed, which is how a
    // burning blade shows on a raider's weapon.
    public abstract class EntadVisual
    {
        public virtual IEnumerable<string> ConfigErrors(string defName) { yield break; }
    }

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

    // A trail of flecks behind every shot the weapon fires
    public class EntadVisual_ProjectileTrail : EntadVisual
    {
        public FleckDef fleck;
        // Flecks per game tick of flight; fractions are a chance
        public float perTick = 1f;
        public FloatRange scale = new FloatRange(0.15f, 0.25f);
        public FloatRange speed = new FloatRange(0f, 0.2f);
        public FloatRange rotationRate = FloatRange.Zero;
        public float jitter = 0.05f;
        public Color color = Color.white;

        public void Spawn(Vector3 pos, Map map)
        {
            float n = perTick;
            while (n > 0f)
            {
                if (n < 1f && !Rand.Chance(n)) break;
                n -= 1f;
                Vector3 p = pos + new Vector3(Rand.Range(-jitter, jitter), 0f, Rand.Range(-jitter, jitter));
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
            if (perTick <= 0f) yield return $"{defName}: projectile trail perTick must be above 0";
        }
    }

    // The item's visuals that are playing, by kind; rebuilt when its traits or what is revealed of them change
    internal class EntadVisualSet
    {
        public static readonly EntadVisualSet Empty = new EntadVisualSet();
        public EntadWeaponVisual[] weapon = new EntadWeaponVisual[0];
        public EntadVisual_ProjectileTrail[] trails = new EntadVisual_ProjectileTrail[0];
        public bool Any => weapon.Length > 0 || trails.Length > 0;

        public static EntadVisualSet For(CompEntad comp)
        {
            List<EntadWeaponVisual> weapon = null;
            List<EntadVisual_ProjectileTrail> trails = null;
            var traits = comp.activeTraits;
            for (int i = 0; i < traits.Count; i++)
            {
                var visuals = traits[i].def.visuals;
                if (visuals == null || traits[i].AnyHidden) continue;
                foreach (var v in visuals)
                {
                    if (v is EntadWeaponVisual w) (weapon ?? (weapon = new List<EntadWeaponVisual>())).Add(w);
                    else if (v is EntadVisual_ProjectileTrail p) (trails ?? (trails = new List<EntadVisual_ProjectileTrail>())).Add(p);
                }
            }
            if (weapon == null && trails == null) return Empty;
            var set = new EntadVisualSet();
            if (weapon != null) set.weapon = weapon.ToArray();
            if (trails != null) set.trails = trails.ToArray();
            return set;
        }
    }

    // What a weapon texture looks like, read back from the GPU once per texture: spots on its opaque pixels for flames,
    // flecks and arcs to sit on, and a blurred copy of its alpha for the glow. Textures loaded by the game aren't
    // readable from the CPU, so the texture is blitted into a small render texture and read from there.
    public class EntadWeaponShape
    {
        private const int Size = 48;
        private const int Pad = 8;
        // The halo texture is padded so the blur has room past the weapon's edges; drawn this much larger to line up
        public const float HaloScale = (Size + 2f * Pad) / Size;
        private const int MaxPoints = 64;

        public Vector2[] points;
        public Material HaloMaterial { get; private set; }

        private static readonly Dictionary<Texture, EntadWeaponShape> cache = new Dictionary<Texture, EntadWeaponShape>();

        public static EntadWeaponShape For(Texture tex)
        {
            if (tex == null) return null;
            if (cache.TryGetValue(tex, out var shape)) return shape;
            try { shape = Read(tex); }
            catch (System.Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] couldn't read the shape of weapon texture {tex.name}: {e}", tex.GetInstanceID());
                shape = null;
            }
            cache[tex] = shape;
            return shape;
        }

        private static EntadWeaponShape Read(Texture tex)
        {
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            Color32[] pixels;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(Size, Size, TextureFormat.ARGB32, false);
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply();
                pixels = read.GetPixels32();
                Object.Destroy(read);
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
            }

            var spots = new List<Vector2>();
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    if (pixels[y * Size + x].a > 128) spots.Add(new Vector2((x + 0.5f) / Size - 0.5f, (y + 0.5f) / Size - 0.5f));
            if (spots.Count == 0) spots.Add(Vector2.zero);
            // An even spread over the weapon, not the first rows of it
            var chosen = new Vector2[Mathf.Min(MaxPoints, spots.Count)];
            for (int i = 0; i < chosen.Length; i++) chosen[i] = spots[(int)((long)i * spots.Count / chosen.Length)];
            for (int i = chosen.Length - 1; i > 0; i--)
            {
                int j = (i * 7919 + 13) % (i + 1);
                (chosen[i], chosen[j]) = (chosen[j], chosen[i]);
            }

            return new EntadWeaponShape { points = chosen, HaloMaterial = MakeHalo(pixels, tex.name) };
        }

        private static Material MakeHalo(Color32[] pixels, string name)
        {
            const int Full = Size + 2 * Pad;
            var alpha = new float[Full * Full];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    alpha[(y + Pad) * Full + x + Pad] = pixels[y * Size + x].a / 255f;
            // Two box blurs each way spread the silhouette a few pixels out with a soft edge
            var tmp = new float[alpha.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                Blur(alpha, tmp, Full, 1, 0);
                Blur(tmp, alpha, Full, 0, 1);
            }
            var colors = new Color32[alpha.Length];
            for (int i = 0; i < alpha.Length; i++) colors[i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha[i] * 1.8f) * 255f));
            var halo = new Texture2D(Full, Full, TextureFormat.ARGB32, false) { name = "EntadHalo_" + name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            halo.SetPixels32(colors);
            halo.Apply(false, true);
            return new Material(ShaderDatabase.MoteGlow) { mainTexture = halo, name = halo.name };
        }

        private static void Blur(float[] from, float[] to, int full, int dx, int dy)
        {
            const int R = 3;
            for (int y = 0; y < full; y++)
                for (int x = 0; x < full; x++)
                {
                    float sum = 0f;
                    for (int k = -R; k <= R; k++)
                    {
                        int sx = x + dx * k, sy = y + dy * k;
                        if (sx >= 0 && sx < full && sy >= 0 && sy < full) sum += from[sy * full + sx];
                    }
                    to[y * full + x] = sum / (2 * R + 1);
                }
        }
    }

    [StaticConstructorOnStartup]
    public static class EntadVisuals
    {
        private static readonly EntadWeaponDraw draw = new EntadWeaponDraw();
        // Last game tick each weapon spawned flecks, by thing id. Cleared when it grows large: that costs a weapon one
        // skipped tick of flecks, not a leak.
        private static readonly Dictionary<int, int> lastFleckTick = new Dictionary<int, int>();
        private const int MaxCatchUpTicks = 6;

        private class Trail
        {
            public Projectile projectile;
            public EntadVisual_ProjectileTrail[] visuals;
        }
        private static readonly List<Trail> trails = new List<Trail>();

        public static bool GearOn => EntadSettings.Visuals && EntadSettings.VisualsGear;

        public static void Clear()
        {
            lastFleckTick.Clear();
            trails.Clear();
        }

        // In a pawn's hands: drawLoc and the angle are what vanilla drew the weapon at, and the matrix is rebuilt the way
        // DrawEquipmentAiming builds it (recoil included). Combat Extended moves some guns by a draw offset of its
        // own after this, which the effects don't follow.
        public static void DrawHeld(Thing eq, Vector3 drawLoc, float aimAngle)
        {
            if (!(eq.ParentHolder is Pawn_EquipmentTracker tracker)) return;
            Pawn pawn = tracker.pawn;
            // Portraits and statues draw an unspawned stand-in
            if (!pawn.Spawned || pawn.Map != Find.CurrentMap || pawn.IsHiddenFromPlayer()) return;
            var set = ((ThingWithComps)eq).GetComp<CompEntad>()?.Visuals;
            if (set == null || set.weapon.Length == 0) return;

            float angle = aimAngle - 90f;
            bool flipped = false;
            if (aimAngle > 200f && aimAngle < 340f)
            {
                flipped = true;
                angle -= 180f;
                angle -= eq.def.equippedAngleOffset;
            }
            else angle += eq.def.equippedAngleOffset;
            angle %= 360f;
            var equippable = ((ThingWithComps)eq).GetComp<CompEquippable>();
            if (equippable != null)
            {
                EquipmentUtility.Recoil(eq.def, EquipmentUtility.GetRecoilVerb(equippable.AllVerbs), out var offset, out var angleOffset, aimAngle);
                drawLoc += offset;
                angle += angleOffset;
            }
            Material mat = eq.Graphic is Graphic_StackCount stack ? stack.SubGraphicForStackCount(1, eq.def).MatSingleFor(eq) : eq.Graphic.MatSingleFor(eq);
            Vector2 size = eq.Graphic.drawSize;
            Run(set, eq, pawn.Map, Matrix4x4.TRS(drawLoc, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(size.x, 1f, size.y)), flipped, mat, size, drawLoc.y + 0.0005f);
        }

        // Lying on the map, drawn from the entad list's per-frame pass (EntadGlint). Weapons are printed into the map
        // mesh, so the placement is worked out as Graphic_RandomRotated.Print does it: a shelf holding several items
        // lays a weapon flat or upright, otherwise each gets a fixed tilt from its id.
        public static void DrawGround(CompEntad comp, Map map, CellRect view)
        {
            Thing thing = comp.parent;
            if (!thing.def.IsWeapon || !view.Contains(thing.Position) || thing.Position.Fogged(map)) return;
            var set = comp.Visuals;
            if (set.weapon.Length == 0) return;
            Graphic graphic = thing.Graphic;
            float angle = 0f;
            float maxAngle = thing.def.graphicData?.onGroundRandomRotateAngle ?? 0f;
            if (graphic is Graphic_RandomRotated)
            {
                if (thing.Position.GetEdifice(map) != null && thing.Position.GetItemCount(map) >= 2) angle = thing.def.rotateInShelves ? -90f : 0f;
                else if (maxAngle > 0.01f) angle = -maxAngle + (thing.thingIDNumber * 542) % (maxAngle * 2f);
            }
            Vector2 size = graphic.drawSize;
            if (thing.MultipleItemsPerCellDrawn()) size *= 0.8f;
            Vector3 pos = thing.DrawPos;
            float altitude = AltitudeLayer.Item.AltitudeFor() + 0.01f;
            Run(set, thing, map, Matrix4x4.TRS(pos, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(size.x, 1f, size.y)), false, graphic.MatSingleFor(thing), size, altitude);
        }

        private static void Run(EntadVisualSet set, Thing weapon, Map map, Matrix4x4 matrix, bool flipped, Material mat, Vector2 size, float altitude)
        {
            var shape = EntadWeaponShape.For(mat?.mainTexture);
            if (shape == null) return;
            int now = Find.TickManager.TicksGame;
            int id = weapon.thingIDNumber;
            int newTicks;
            if (lastFleckTick.TryGetValue(id, out int last)) newTicks = Mathf.Clamp(now - last, 0, MaxCatchUpTicks);
            else
            {
                newTicks = 1;
                if (lastFleckTick.Count > 1024) lastFleckTick.Clear();
            }
            if (newTicks > 0 || last == 0) lastFleckTick[id] = now;

            draw.weapon = weapon;
            draw.map = map;
            draw.matrix = matrix;
            draw.flipped = flipped;
            draw.shape = shape;
            draw.size = Mathf.Max(size.x, size.y);
            draw.seed = Gen.HashCombineInt(id, 0x7A11);
            draw.ticks = now;
            draw.newTicks = newTicks;
            draw.altitude = altitude;
            for (int i = 0; i < set.weapon.Length; i++)
            {
                try { set.weapon[i].Draw(draw); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] weapon visual {set.weapon[i].GetType().Name} failed on {weapon}: {e}", Gen.HashCombineInt(id, i));
                }
            }
        }

        public static void Launched(Projectile projectile, Thing equipment)
        {
            if (!EntadSettings.Visuals || !EntadSettings.VisualsProjectiles || !(equipment is ThingWithComps eq)) return;
            var set = eq.GetComp<CompEntad>()?.Visuals;
            if (set == null || set.trails.Length == 0) return;
            trails.Add(new Trail { projectile = projectile, visuals = set.trails });
        }

        // Every game tick while a shot with a trail is in the air
        public static void TickTrails()
        {
            Map current = Find.CurrentMap;
            CellRect view = current != null ? Find.CameraDriver.CurrentViewRect.ExpandedBy(2) : CellRect.Empty;
            for (int i = trails.Count - 1; i >= 0; i--)
            {
                var trail = trails[i];
                var projectile = trail.projectile;
                if (projectile.Destroyed || !projectile.Spawned) { trails.RemoveAt(i); continue; }
                if (projectile.Map != current) continue;
                Vector3 pos = projectile.DrawPos;
                IntVec3 cell = pos.ToIntVec3();
                if (!view.Contains(cell) || !pos.ShouldSpawnMotesAt(current)) continue;
                pos.y = AltitudeLayer.MoteLow.AltitudeFor();
                for (int k = 0; k < trail.visuals.Length; k++) trail.visuals[k].Spawn(pos, current);
            }
        }

        public static bool AnyTrails => trails.Count > 0;
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
    public static class Patch_DrawEquipmentAiming_Visuals
    {
        public static void Postfix(Thing eq, Vector3 drawLoc, float aimAngle)
        {
            if (!EntadVisuals.GearOn || !EntadTraitDef.AnyWeaponVisuals) return;
            EntadVisuals.DrawHeld(eq, drawLoc, aimAngle);
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
