using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // A short gold glint every few seconds on entads in view, so one sitting in a dungeon room or a pile of loot is
    // hard to miss: in the books, mages can tell an entad on sight. Drawn straight from the map's per-frame update,
    // on real time, so it keeps twinkling while the game is paused (flecks would freeze) and spawns nothing.
    //
    // Every entad with traits is listed once, wherever it is (worn, carried, on a world pawn), because gear doesn't
    // hear about its wearer spawning. The list holds weak references: gear on a discarded world pawn is never
    // destroyed, so a strong reference would keep the pawn alive. Each item glints for a fraction of its own period,
    // and the period check comes first, so most items cost a few float operations per frame and nothing else.
    [HarmonyPatch(typeof(MapInterface), nameof(MapInterface.MapInterfaceUpdate))]
    [StaticConstructorOnStartup]
    public static class EntadGlint
    {
        private const float GlintSeconds = 0.6f;
        private const float MinPeriod = 3f;
        private const float PeriodSpread = 2.5f;
        // Size on screen it never shrinks below as the camera zooms out, and its size in cells up close
        private const float MinPixels = 26f;
        private const float MinCells = 0.7f;
        private const float MaxCells = 3f;
        private static readonly Color GlintColor = new Color(1f, 0.9f, 0.62f);

        private static readonly Material material = MaterialPool.MatFrom("Things/Mote/EntadGlint", ShaderDatabase.MoteGlow, GlintColor);
        private static readonly MaterialPropertyBlock block = new MaterialPropertyBlock();
        private static readonly List<System.WeakReference<CompEntad>> items = new List<System.WeakReference<CompEntad>>();

        // The draw loop prunes as it goes, but it doesn't run with the glint off or on the world view, while world
        // pawn gear keeps registering, so registering prunes too once the list has doubled
        private static int pruneAt = 256;

        public static void Clear() { items.Clear(); pruneAt = 256; }

        public static void Register(CompEntad comp)
        {
            if (comp.glintRegistered || comp.activeTraits.Count == 0) return;
            comp.glintRegistered = true;
            items.Add(new System.WeakReference<CompEntad>(comp));
            if (items.Count < pruneAt) return;
            for (int i = items.Count - 1; i >= 0; i--)
                if (Dead(i, out _)) items.RemoveAt(i);
            pruneAt = Mathf.Max(256, items.Count * 2);
        }

        private static bool Dead(int i, out CompEntad comp)
        {
            if (items[i].TryGetTarget(out comp) && !comp.parent.Destroyed && comp.activeTraits.Count > 0) return false;
            if (comp != null) comp.glintRegistered = false;
            return true;
        }

        public static void Postfix()
        {
            if (!EntadSettings.Glint || items.Count == 0) return;
            Map map = Find.CurrentMap;
            if (map == null || !WorldRendererUtility.DrawingMap) return;
            // Double: a float clock this far into a session would round to steps near a frame long
            double now = Time.realtimeSinceStartupAsDouble;
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            float size = Mathf.Clamp(MinPixels / Find.CameraDriver.CellSizePixels, MinCells, MaxCells);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (Dead(i, out var comp)) { items.RemoveAt(i); continue; }
                int seed = Gen.HashCombineInt(comp.parent.thingIDNumber, 0x5E1C);
                double period = MinPeriod + PeriodSpread * ((seed & 0xFF) / 255f);
                double t = now + period * (((seed >> 8) & 0xFFFF) / 65535f);
                double cycles = System.Math.Floor(t / period);
                int cycle = (int)(long)cycles;
                float u = (float)((t - cycles * period) / GlintSeconds);
                if (u >= 1f) continue;
                if (EntadSettings.GlintHideIdentified && !comp.HasHidden) continue;
                Thing anchor = Anchor(comp.parent);
                if (anchor == null || anchor.Map != map || !view.Contains(anchor.Position) || anchor.Position.Fogged(map)) continue;
                try { Draw(anchor, map, Gen.HashCombineInt(seed, cycle), u, size); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] entad glint failed on {comp.parent}: {e}", 0x5E1C0000 ^ comp.parent.thingIDNumber);
                }
            }
        }

        // Where the item shows on the map. Loose and minified items, installed furniture and gear on a corpse are
        // loot to be found; gear in use (worn, wielded, carried in hand) is its own setting. Inventory and containers are out of sight,
        // and a glint there would tell the player what a raider or a closed crate holds.
        private static Thing Anchor(Thing item)
        {
            if (item.Spawned) return item;
            switch (item.ParentHolder)
            {
                case MinifiedThing minified:
                    return minified.Spawned ? minified : null;
                case Pawn_CarryTracker carry:
                    return GearAnchor(carry.pawn);
                case Pawn_EquipmentTracker equipment:
                    return GearAnchor(equipment.pawn);
                case Pawn_ApparelTracker apparel:
                    return GearAnchor(apparel.pawn);
            }
            return null;
        }

        private static Thing GearAnchor(Pawn pawn)
        {
            if (pawn.Spawned) return EntadSettings.GlintOnGear && !pawn.IsHiddenFromPlayer() ? pawn : null;
            return pawn.ParentHolder is Corpse corpse && corpse.Spawned ? corpse : null;
        }

        private static void Draw(Thing anchor, Map map, int cycleSeed, float u, float size)
        {
            // A new spot on the thing each time it glints, inside its footprint
            Vector2 extent = anchor.def.building != null ? new Vector2(anchor.RotatedSize.x, anchor.RotatedSize.z) * 0.5f - new Vector2(0.25f, 0.25f) : new Vector2(0.3f, 0.3f);
            extent = Vector2.Max(extent, new Vector2(0.15f, 0.15f));
            float rx = ((cycleSeed & 0xFFFF) / 65535f) * 2f - 1f;
            float rz = (((cycleSeed >> 16) & 0x7FFF) / 32767f) * 2f - 1f;
            Vector3 pos = anchor.DrawPos;
            pos.x += rx * extent.x;
            pos.z += rz * extent.y;
            // The spot's own cell: a large building on the edge of the fog would otherwise glint from its fogged part
            if (pos.ToIntVec3().Fogged(map)) return;
            pos.y = AltitudeLayer.MetaOverlays.AltitudeFor();
            float strength = Mathf.Sin(u * Mathf.PI);
            float scale = size * strength;
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.AngleAxis(u * 45f, Vector3.up), new Vector3(scale, 1f, scale));
            Color color = GlintColor;
            color.a = strength;
            block.SetColor(ShaderPropertyIDs.Color, color);
            Graphics.DrawMesh(MeshPool.plane10, matrix, material, 0, null, 0, block);
        }
    }
}
