using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Faded copies of a pawn left behind as it sprints, or knocked off it as it dodges. Each is the pawn's portrait
    // (PortraitsCache, the picture the colonist bar and the character card use) drawn as a translucent quad where the
    // pawn was. The portrait is cached per pawn and facing and re-rendered only when the pawn's looks change; vanilla
    // drops it a second after the last request, so nothing outlives the effect. Ages by game tick, so a pause holds
    // the images where they are.
    [StaticConstructorOnStartup]
    public static class EntadAfterimages
    {
        private class Ghost
        {
            public Pawn pawn;
            public Map map;
            public Vector3 pos;
            public Vector3 driftPerTick;
            public Rot4 rot;
            public int start;
            public int life;
            public Color color;
        }

        private static readonly List<Ghost> ghosts = new List<Ghost>();
        // Portraits come from a pool vanilla reuses, so a material made for one stays valid for whoever is in it next
        private static readonly Dictionary<RenderTexture, Material> materials = new Dictionary<RenderTexture, Material>();
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private const int PortraitPixels = 160;
        // More than this on screen and the oldest go first: a sprinting crowd shouldn't render a portrait each
        private const int MaxGhosts = 48;

        public static bool On => EntadSettings.Visuals && EntadSettings.VisualsEvents;
        public static bool Any => ghosts.Count > 0;

        public static void Clear()
        {
            ghosts.Clear();
            materials.Clear();
        }

        public static void Add(Pawn pawn, Vector3 pos, Vector3 driftPerTick, int lifeTicks, Color color)
        {
            if (!pawn.Spawned || pawn.Map != Find.CurrentMap) return;
            if (ghosts.Count >= MaxGhosts) ghosts.RemoveAt(0);
            ghosts.Add(new Ghost
            {
                pawn = pawn,
                map = pawn.Map,
                pos = pos,
                driftPerTick = driftPerTick,
                rot = pawn.Rotation,
                start = Find.TickManager.TicksGame,
                life = lifeTicks,
                color = color,
            });
        }

        // Every frame, from the map's per-frame update (EntadGlint)
        public static void Draw(Map map)
        {
            int now = Find.TickManager.TicksGame;
            float y = AltitudeLayer.Pawn.AltitudeFor() - 0.01f;
            for (int i = ghosts.Count - 1; i >= 0; i--)
            {
                var g = ghosts[i];
                int age = now - g.start;
                if (age >= g.life || g.pawn.Destroyed) { ghosts.RemoveAt(i); continue; }
                if (g.map != map || !g.pawn.Spawned || g.pawn.IsHiddenFromPlayer()) continue;
                Vector3 pos = g.pos + g.driftPerTick * age;
                if (pos.ToIntVec3().Fogged(map)) continue;
                float cells = Cells(g.pawn);
                var rt = PortraitsCache.Get(g.pawn, new Vector2(PortraitPixels, PortraitPixels), g.rot, default, 2f / cells,
                    supersample: false, compensateForUIScale: false, healthStateOverride: PawnHealthState.Mobile);
                if (rt == null) continue;
                if (!materials.TryGetValue(rt, out var mat))
                {
                    mat = new Material(ShaderDatabase.Transparent) { mainTexture = rt };
                    materials[rt] = mat;
                }
                Color c = g.color;
                c.a *= 1f - age / (float)g.life;
                block.SetColor(ShaderPropertyIDs.Color, c);
                pos.y = y;
                Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.identity, new Vector3(cells, 1f, cells)), mat, 0, null, 0, block);
            }
        }

        // The square the portrait covers, in cells: two for a person (vanilla's portrait camera at zoom 1), more for a
        // big animal so it isn't cut off
        private static float Cells(Pawn pawn)
        {
            if (pawn.RaceProps.Humanlike) return 2f;
            Vector2 size = pawn.ageTracker?.CurKindLifeStage?.bodyGraphicData?.drawSize ?? Vector2.one;
            return Mathf.Max(2f, Mathf.Max(size.x, size.y) * 1.1f);
        }
    }

    // On a hediff: afterimages behind the pawn while it moves (Sprint)
    public class HediffCompProperties_EntadAfterimages : HediffCompProperties
    {
        public int intervalTicks = 5;
        public int lifeTicks = 22;
        public Color color = new Color(1f, 0.85f, 0.55f, 0.45f);

        public HediffCompProperties_EntadAfterimages() { compClass = typeof(HediffComp_EntadAfterimages); }
    }

    public class HediffComp_EntadAfterimages : HediffComp
    {
        public HediffCompProperties_EntadAfterimages Props => (HediffCompProperties_EntadAfterimages)props;

        public override void CompPostTick(ref float severityAdjustment)
        {
            Pawn pawn = Pawn;
            if (!pawn.IsHashIntervalTick(Props.intervalTicks) || !EntadAfterimages.On || !pawn.Spawned || pawn.Map != Find.CurrentMap) return;
            if (pawn.pather == null || !pawn.pather.Moving || pawn.IsHiddenFromPlayer()) return;
            IntVec3 cell = pawn.Position;
            if (!cell.ShouldSpawnMotesAt(pawn.Map, false) || cell.Fogged(pawn.Map)) return;
            EntadAfterimages.Add(pawn, pawn.DrawPos, Vector3.zero, Props.lifeTicks, Props.color);
        }
    }
}
