using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Drawn on the pawn wearing the item, as a node of the pawn's render tree under its body, so it follows the body
    // through walking, facing, lying down and animations, the way Bastle Framework's flaming creatures do. Apparel
    // only: vanilla rebuilds a pawn's render tree when its apparel changes, not when its weapon does.
    public abstract class EntadWornVisual : EntadVisual
    {
        // Only while the wearer is drafted, or otherwise shows its weapon (raiders attacking, guards on duty, a hunt),
        // since an NPC is never drafted
        public bool onlyDrafted;
        // Where on the body it plays, as fractions of the body mesh from its centre (x across, z up). Sprites don't
        // fill their mesh: the defaults are a humanlike's torso and legs.
        public FloatRange x = new FloatRange(-0.17f, 0.17f);
        public FloatRange z = new FloatRange(-0.25f, 0.12f);
        // x is multiplied by this while the body shows its narrow side: a humanlike facing east or west, an animal
        // facing north or south
        public float narrowWidth = 0.6f;

        public abstract void Draw(EntadWornDraw draw);
    }

    // Shared state for one item's draw on its wearer this frame, filled once and handed to each of its visuals
    public class EntadWornDraw
    {
        public Pawn pawn;
        public Thing item;
        public Map map;
        // The node's matrix: the body's position, facing and lying rotation, at the node's layer
        public Matrix4x4 matrix;
        // Body mesh in cells
        public float width, height;
        public bool narrow, west;
        public bool drawNow;
        public int seed;
        public int ticks;
        // Game ticks since this item last spawned flecks (0 while paused), capped like a weapon's
        public int newTicks;

        // A spot on the body for u, v in 0..1
        public Vector3 Spot(EntadWornVisual v, float u, float w)
        {
            float fx = Mathf.Lerp(v.x.min, v.x.max, u) * (narrow ? v.narrowWidth : 1f) * (west ? -1f : 1f);
            float fz = Mathf.Lerp(v.z.min, v.z.max, w);
            return matrix.MultiplyPoint3x4(new Vector3(fx * width, 0f, fz * height));
        }
    }

    // Flecks rising, drifting or sinking off the wearer: smoke, frost mist, glints. Spawned on game ticks only, and
    // only while the wearer is drawn, so nothing spawns off screen or piles up while paused.
    public class EntadVisual_WornFlecks : EntadWornVisual
    {
        public FleckDef fleck;
        public float perSecond = 2f;
        public FloatRange scale = new FloatRange(0.2f, 0.35f);
        // Direction (0 = up the screen) and speed in cells per second
        public FloatRange angle = new FloatRange(-30f, 30f);
        public FloatRange speed = new FloatRange(0.1f, 0.3f);
        public FloatRange rotationRate = FloatRange.Zero;
        public Color color = Color.white;

        public override void Draw(EntadWornDraw d)
        {
            if (d.newTicks <= 0 || fleck == null) return;
            float chance = perSecond / 60f;
            for (int t = 0; t < d.newTicks; t++)
            {
                if (!Rand.Chance(chance)) continue;
                Vector3 pos = d.Spot(this, Rand.Value, Rand.Value);
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
            if (fleck == null) yield return $"{defName}: worn flecks visual needs a fleck";
        }
    }

    // Small flickering fire sprites over the wearer, each on its own spot and clock; vanilla's fire frames by default
    public class EntadVisual_WornFlames : EntadWornVisual
    {
        [NoTranslate] public string texFolder = "Things/Special/Fire";
        public int count = 3;
        // Sprite size as a fraction of the body mesh height
        public FloatRange size = new FloatRange(0.2f, 0.3f);
        public Color color = new Color(1f, 1f, 1f, 0.85f);
        public int ticksPerFrame = 12;

        [Unsaved] private List<Material> materials;

        public override void Draw(EntadWornDraw d)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.TransparentPostLight, color, "flame");
            if (mats.Count == 0) return;
            for (int i = 0; i < count; i++)
            {
                int seed = Gen.HashCombineInt(d.seed, i * 7919 + 17);
                int frame = (d.ticks + (seed & 0xFFFF)) / Mathf.Max(1, ticksPerFrame);
                // A flame moves to a new spot now and then, so a pawn standing still doesn't burn in the same places
                int spot = EntadGlint.Mix(seed ^ (frame / 12) * 0x2C1B3C6D);
                Vector3 pos = d.Spot(this, (spot & 0xFFFF) / 65535f, ((spot >> 16) & 0xFFFF) / 65535f);
                float s = Mathf.Lerp(size.min, size.max, Rand.ValueSeeded(seed + 2)) * d.height;
                // Fire sprites sit on their base, so the flame rises from the spot
                pos.z += s * 0.35f;
                pos.y += 0.002f * (i + 1);
                var mat = mats[((frame ^ seed) & 0x7FFFFFFF) % mats.Count];
                GenDraw.DrawMeshNowOrLater(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(s, 1f, s)), mat, d.drawNow);
            }
        }
    }

    // Static arcs jumping between two spots on the wearer for a few ticks at a time, like the weapon arcs
    public class EntadVisual_WornArcs : EntadWornVisual
    {
        [NoTranslate] public string texFolder = "Things/Mote/EntadArc";
        public Color color = new Color(0.7f, 0.85f, 1f, 0.9f);
        public int intervalTicks = 30;
        public float chance = 0.6f;
        public int arcTicks = 5;
        // Width in cells, and the shortest arc in cells
        public float width = 0.2f;
        public float minLength = 0.3f;

        [Unsaved] private List<Material> materials;

        public override void Draw(EntadWornDraw d)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.MoteGlow, color, "arc");
            if (mats.Count == 0) return;
            int interval = Mathf.Max(arcTicks + 1, intervalTicks);
            int t = d.ticks + (d.seed & 0xFFFF);
            if (t % interval >= arcTicks) return;
            int seed = EntadGlint.Mix(d.seed ^ (t / interval) * 0x2C1B3C6D);
            if ((seed & 0xFFFF) / 65536f >= chance) return;
            Vector3 a = SpotFor(d, seed);
            Vector3 b = a;
            for (int k = 1; k <= 6; k++)
            {
                b = SpotFor(d, Gen.HashCombineInt(seed, k));
                if ((b - a).MagnitudeHorizontalSquared() >= minLength * minLength) break;
            }
            EntadVisuals.DrawArc(mats[((seed >> 4) & 0x7FFFFFFF) % mats.Count], a, b, width, minLength, d.drawNow);
        }

        private Vector3 SpotFor(EntadWornDraw d, int hash)
        {
            hash = EntadGlint.Mix(hash);
            return d.Spot(this, (hash & 0xFFFF) / 65535f, ((hash >> 16) & 0xFFFF) / 65535f);
        }
    }

    // One per worn item that has a trait with worn visuals, revealed or not: what is revealed is read on every draw,
    // so a reveal needs no tree rebuild. The item is set after construction; the base constructor only asks for the
    // mesh, which needs none of it.
    public class PawnRenderNode_EntadWorn : PawnRenderNode
    {
        public Apparel item;
        public CompEntad comp;
        public float width = 1f, height = 1f;

        private static PawnRenderNodeProperties props;

        public static PawnRenderNodeProperties Props
        {
            get
            {
                if (props == null)
                {
                    props = new PawnRenderNodeProperties
                    {
                        debugLabel = "Entad worn visuals",
                        nodeClass = typeof(PawnRenderNode_EntadWorn),
                        workerClass = typeof(PawnRenderNodeWorker_EntadWorn),
                        parentTagDef = PawnRenderNodeTagDefOf.Body,
                        pawnType = PawnRenderNodeProperties.RenderNodePawnType.Any,
                        rotDrawMode = RotDrawMode.Fresh,
                        useGraphic = false,
                        // Over the body, apparel and head (the body node is layer 0, the head 50, headgear up to about
                        // 80): flames and arcs show in front of the clothes
                        baseLayer = 90f,
                    };
                    props.EnsureInitialized();
                }
                return props;
            }
        }

        public PawnRenderNode_EntadWorn(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree) { }

        // Only used to know the body's size; nothing is drawn with it
        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            if (pawn.RaceProps.Humanlike) width = height = HumanlikeMeshPoolUtility.HumanlikeBodyWidthForPawn(pawn);
            else
            {
                var data = pawn.ageTracker?.CurKindLifeStage?.bodyGraphicData;
                if (data != null) { width = data.drawSize.x; height = data.drawSize.y; }
            }
            return MeshPool.GetMeshSetForSize(width, height);
        }
    }

    public class PawnRenderNodeWorker_EntadWorn : PawnRenderNodeWorker
    {
        // Draw requests are cached until the pawn's draw parameters change, so nothing here can depend on what is
        // revealed or on the settings. A request with no material is skipped by the tree's draw loop, but PostDraw
        // still runs for it every frame with the node's matrix, and that's where the visuals draw.
        public override void AppendDrawRequests(PawnRenderNode node, PawnDrawParms parms, List<PawnGraphicDrawRequest> requests)
        {
            if (parms.Portrait || parms.Statue) return;
            requests.Add(new PawnGraphicDrawRequest(node));
        }

        public override void PostDraw(PawnRenderNode node, PawnDrawParms parms, Mesh mesh, Matrix4x4 matrix)
        {
            base.PostDraw(node, parms, mesh, matrix);
            if (node is PawnRenderNode_EntadWorn worn) EntadVisuals.DrawWorn(worn, parms, matrix);
        }
    }

    // PawnRenderTree makes one of every DynamicPawnRenderNodeSetup subclass and asks it for nodes whenever a pawn's
    // tree is built (spawning, apparel changes, SetAllGraphicsDirty), so this needs no Harmony patch
    public class DynamicPawnRenderNodeSetup_EntadWorn : DynamicPawnRenderNodeSetup
    {
        public override bool HumanlikeOnly => false;

        public override IEnumerable<(PawnRenderNode node, PawnRenderNode parent)> GetDynamicNodes(Pawn pawn, PawnRenderTree tree)
        {
            if (!EntadTraitDef.AnyWornVisuals) yield break;
            var worn = pawn.apparel?.WornApparel;
            if (worn == null) yield break;
            for (int i = 0; i < worn.Count; i++)
            {
                var comp = worn[i].GetComp<CompEntad>();
                if (comp == null) continue;
                comp.wornNodeBuilt = comp.HasWornVisualTraits;
                if (!comp.wornNodeBuilt) continue;
                yield return (new PawnRenderNode_EntadWorn(pawn, PawnRenderNode_EntadWorn.Props, tree) { item = worn[i], comp = comp }, null);
            }
        }
    }

    public static partial class EntadVisuals
    {
        private static readonly EntadWornDraw wornDraw = new EntadWornDraw();

        internal static void DrawWorn(PawnRenderNode_EntadWorn node, PawnDrawParms parms, Matrix4x4 matrix)
        {
            if (!GearOn) return;
            Pawn pawn = parms.pawn;
            var item = node.item;
            // The node outlives the item until the tree is next rebuilt: a destroyed or taken-off item is skipped
            if (item == null || item.Wearer != pawn) return;
            var set = node.comp.Visuals;
            if (set.worn.Length == 0) return;
            if (pawn.Dead || !pawn.Spawned || pawn.Map != Find.CurrentMap || parms.flags.FlagSet(PawnRenderFlags.Invisible) || pawn.IsHiddenFromPlayer()) return;

            int now = Find.TickManager.TicksGame;
            int id = item.thingIDNumber;
            var d = wornDraw;
            d.pawn = pawn;
            d.item = item;
            d.map = pawn.Map;
            d.matrix = matrix;
            d.width = node.width;
            d.height = node.height;
            d.narrow = pawn.RaceProps.Humanlike ? parms.facing.IsHorizontal : !parms.facing.IsHorizontal;
            d.west = parms.facing == Rot4.West;
            d.drawNow = parms.DrawNow;
            d.seed = Gen.HashCombineInt(id, 0x3E57);
            d.ticks = now;
            d.newTicks = NewTicks(id, now);
            int shown = -1;
            Rand.PushState(Gen.HashCombineInt(d.seed, now));
            try
            {
                for (int i = 0; i < set.worn.Length; i++)
                {
                    var v = set.worn[i];
                    if (v.onlyDrafted)
                    {
                        if (shown < 0) shown = PawnRenderUtility.CarryWeaponOpenly(pawn) ? 1 : 0;
                        if (shown == 0) continue;
                    }
                    try { v.Draw(d); }
                    catch (System.Exception e)
                    {
                        Log.ErrorOnce($"[Entad Framework] worn visual {v.GetType().Name} failed on {item}: {e}", Gen.HashCombineInt(id, i + 0x51));
                    }
                }
            }
            finally { Rand.PopState(); }
        }
    }
}
