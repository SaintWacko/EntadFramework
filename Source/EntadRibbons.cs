using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Glowing strands of energy that writhe around the line between two spots, twisting around each other as they go
    // and meeting at the far end, the head racing ahead and the tail following it in (Maddening pouring into its victim, Vampiric drawing life back). A
    // fleck can't bend, so these are drawn every frame as short stretched segments of one soft strip.
    public class EntadVisual_EventRibbons : EntadEventVisual
    {
        public EntadVisualSpot from = EntadVisualSpot.Holder;
        public EntadVisualSpot to = EntadVisualSpot.Other;
        public Color color = Color.white;
        public IntRange strands = new IntRange(3, 3);
        // How far a strand swings off the line, as a fraction of the distance, capped in tiles. Each strand is two
        // sine waves of its own (random frequency and phase) under an envelope pinned at both ends, so strands cross
        // and wind around each other; both waves slide along over time (spin, radians per second), which makes them
        // twist as they fly.
        public float bulge = 0.14f;
        public float maxBulge = 0.8f;
        public FloatRange turns = new FloatRange(0.8f, 1.8f);
        public FloatRange spin = new FloatRange(5f, 10f);
        // Strength of the second, faster wave against the first
        public float chaos = 0.45f;
        public float width = 0.32f;
        public float coreWidth = 0.09f;
        // How far the core is lightened towards white
        public float coreWhiten = 0.55f;
        // Tiles per second for the head; the travel time never drops under minTravel seconds
        public float speed = 9f;
        public float minTravel = 0.18f;
        // Length of the lit stretch behind the head, as a fraction of the path
        public float tail = 0.3f;
        // Seconds between one strand setting off and the next
        public float stagger = 0.05f;

        public override void Play(EntadVisualContext ctx)
        {
            ctx.Spots(from, spotsA);
            if (spotsA.Count == 0) return;
            ctx.Spots(to, spotsB);
            foreach (var (a, ta) in spotsA)
                foreach (var (b, tb) in spotsB)
                {
                    float dist = Mathf.Sqrt((b - a).MagnitudeHorizontalSquared());
                    if (dist < 0.3f) continue;
                    int n = strands.RandomInRange;
                    var s = new EntadRibbons.Strand[n];
                    float amp = Mathf.Min(dist * bulge, maxBulge);
                    float spread = Mathf.PI * 2f / n;
                    for (int k = 0; k < n; k++)
                    {
                        // Phases spread round the circle so the strands start apart and cross later, not in step
                        float side = Rand.Bool ? 1f : -1f;
                        s[k] = new EntadRibbons.Strand
                        {
                            amp = amp * Rand.Range(0.7f, 1.15f),
                            freq = turns.RandomInRange * Mathf.PI * 2f,
                            phase = k * spread + Rand.Range(-0.6f, 0.6f),
                            spin = spin.RandomInRange * side,
                            freq2 = turns.RandomInRange * 2.3f * Mathf.PI * 2f,
                            phase2 = Rand.Range(0f, Mathf.PI * 2f),
                            spin2 = -spin.RandomInRange * side * 1.4f,
                            delay = k * stagger + Rand.Range(0f, stagger * 0.5f),
                        };
                    }
                    EntadRibbons.Add(new EntadRibbons.Ribbon
                    {
                        def = this,
                        map = ctx.map,
                        fromThing = ta.HasThing ? ta.Thing : null,
                        toThing = tb.HasThing ? tb.Thing : null,
                        fromPos = a,
                        toPos = b,
                        strands = s,
                        travel = Mathf.Max(minTravel, dist / speed),
                    });
                }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (speed <= 0f) yield return $"{defName}: ribbon speed must be above 0";
            if (tail <= 0f) yield return $"{defName}: ribbon tail must be above 0";
            if (strands.min < 1) yield return $"{defName}: ribbons need at least one strand";
            if ((from == EntadVisualSpot.Affected || from == EntadVisualSpot.Source || to == EntadVisualSpot.Affected || to == EntadVisualSpot.Source)
                && on != EntadVisualEvent.Trigger)
                yield return $"{defName}: ribbon spots Affected and Source only exist on Trigger events";
        }
    }

    [StaticConstructorOnStartup]
    public static class EntadRibbons
    {
        internal struct Strand
        {
            public float amp;
            public float freq;
            public float phase;
            public float spin;
            public float freq2;
            public float phase2;
            public float spin2;
            public float delay;
        }

        internal class Ribbon
        {
            public EntadVisual_EventRibbons def;
            public Map map;
            // Ends follow their pawns while they stay on the map, else keep the last spot
            public Thing fromThing;
            public Thing toThing;
            public Vector3 fromPos;
            public Vector3 toPos;
            public Strand[] strands;
            public float travel;
            public float age;
        }

        // Few at a time: one per trigger that fires. Capped for the world map, where nothing draws (and so nothing
        // ages) them while triggers on the colony map keep adding more.
        private static readonly List<Ribbon> ribbons = new List<Ribbon>();
        private const int MaxRibbons = 32;
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static Material mat;

        public static bool Any => ribbons.Count > 0;

        internal static void Add(Ribbon r)
        {
            if (ribbons.Count >= MaxRibbons) ribbons.RemoveAt(0);
            ribbons.Add(r);
        }

        public static void Clear() => ribbons.Clear();

        // Every frame from the glint's MapInterfaceUpdate postfix. Ages in real time while the game runs, so a ribbon
        // reads the same at any game speed and holds still while paused.
        public static void Draw(Map map)
        {
            if (mat == null) mat = MaterialPool.MatFrom("Things/Mote/EntadRibbon", ShaderDatabase.MoteGlow);
            float dt = Find.TickManager.Paused ? 0f : Mathf.Min(Time.deltaTime, 0.1f);
            float y = AltitudeLayer.MoteOverhead.AltitudeFor();
            for (int i = ribbons.Count - 1; i >= 0; i--)
            {
                var r = ribbons[i];
                // Play only adds ribbons on the map in view, and one lasts under a second, so one for another map is
                // left over from a map switch and would otherwise replay on return or hold a removed map
                if (r.map != map) { ribbons.RemoveAt(i); continue; }
                r.age += dt;
                Follow(r.fromThing, map, ref r.fromPos);
                Follow(r.toThing, map, ref r.toPos);
                bool alive = false;
                for (int k = 0; k < r.strands.Length; k++)
                    alive |= DrawStrand(r, r.strands[k], y);
                if (!alive) ribbons.RemoveAt(i);
            }
        }

        private static void Follow(Thing thing, Map map, ref Vector3 pos)
        {
            if (thing != null && thing.Spawned && thing.Map == map) pos = thing.DrawPos;
        }

        // False once the strand's tail has reached the far end
        private static bool DrawStrand(Ribbon r, Strand s, float y)
        {
            var d = r.def;
            float a = r.age - s.delay;
            if (a <= 0f) return true;
            float head = Mathf.Clamp01(a / r.travel);
            float tail = Mathf.Clamp01((a - d.tail * r.travel) / r.travel);
            if (tail >= 1f) return false;
            float span = head - tail;
            if (span <= 0.001f) return true;

            Vector3 from = r.fromPos, to = r.toPos;
            Vector3 line = to - from;
            line.y = 0f;
            float dist = line.magnitude;
            if (dist < 0.01f) return false;
            Vector3 perp = new Vector3(-line.z, 0f, line.x) / dist;
            int steps = Mathf.Clamp(Mathf.CeilToInt(span * dist * 5f), 3, 40);

            Color core = Color.Lerp(d.color, Color.white, d.coreWhiten);
            Vector3 prev = Point(from, line, perp, tail, s, d, r.age);
            for (int i = 1; i <= steps; i++)
            {
                float t = Mathf.Lerp(tail, head, i / (float)steps);
                Vector3 p = Point(from, line, perp, t, s, d, r.age);
                // Brightest just behind the head, fading out along the tail
                float u = (i - 0.5f) / steps;
                float fade = Mathf.Clamp01(u * 2.2f) * Mathf.Clamp01((1f - u) * 10f);
                Segment(prev, p, y, d.width, d.color, fade * 0.75f);
                Segment(prev, p, y + 0.001f, d.coreWidth, core, fade);
                prev = p;
            }
            return true;
        }

        private static Vector3 Point(Vector3 from, Vector3 line, Vector3 perp, float t, Strand s, EntadVisual_EventRibbons d, float age)
        {
            // Pinned at both ends, so every strand leaves and lands on its pawn
            float envelope = Mathf.Sin(t * Mathf.PI);
            float off = s.amp * envelope * (Mathf.Sin(s.freq * t + s.phase + s.spin * age)
                + d.chaos * Mathf.Sin(s.freq2 * t + s.phase2 + s.spin2 * age));
            return from + line * t + perp * off;
        }

        private static void Segment(Vector3 a, Vector3 b, float y, float width, Color color, float alpha)
        {
            Vector3 dir = b - a;
            float length = Mathf.Sqrt(dir.MagnitudeHorizontalSquared());
            if (length < 0.001f || alpha <= 0.01f) return;
            float deg = -Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
            Vector3 mid = (a + b) * 0.5f;
            mid.y = y;
            color.a *= alpha;
            block.SetColor(ShaderPropertyIDs.Color, color);
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(mid, Quaternion.AngleAxis(deg, Vector3.up), new Vector3(length * 1.04f, 1f, width)), mat, 0, null, 0, block);
        }
    }
}
