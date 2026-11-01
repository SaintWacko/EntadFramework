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
            // Before the empty check: the entad that fired a zapping shot may be gone while its zaps still show
            if (EntadVisuals.AnyZaps && Find.CurrentMap != null && WorldRendererUtility.DrawingMap)
            {
                try { EntadVisuals.DrawZaps(Find.CurrentMap); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] drawing shot zaps failed: {e}", 0x7A11CAFE);
                }
            }
            if (EntadRibbons.Any && Find.CurrentMap != null && WorldRendererUtility.DrawingMap)
            {
                try { EntadRibbons.Draw(Find.CurrentMap); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] drawing ribbons failed: {e}", 0x7A11CAFF);
                }
            }
            // Afterimages come from hediffs and dodges, not from the entad list, so they don't wait on it either
            if (EntadAfterimages.Any && Find.CurrentMap != null && WorldRendererUtility.DrawingMap)
            {
                try { EntadAfterimages.Draw(Find.CurrentMap); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] drawing afterimages failed: {e}", 0x7A11AF7E);
                }
            }
            if (items.Count == 0) return;
            bool glint = EntadSettings.Glint;
            // Weapons lying on the map show their trait visuals from this pass too: it already walks every entad, and
            // a printed item has no per-frame draw of its own to hook
            bool ground = EntadVisuals.GearOn && EntadTraitDef.AnyWeaponVisuals;
            if (!glint && !ground) return;
            Map map = Find.CurrentMap;
            if (map == null || !WorldRendererUtility.DrawingMap) return;
            // Double: a float clock this far into a session would round to steps near a frame long
            double now = Time.realtimeSinceStartupAsDouble;
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            float size = Mathf.Clamp(MinPixels / Find.CameraDriver.CellSizePixels, MinCells, MaxCells);
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (Dead(i, out var comp)) { items.RemoveAt(i); continue; }
                if (ground && comp.parent.Spawned && comp.parent.Map == map)
                {
                    try { EntadVisuals.DrawGround(comp, map, view); }
                    catch (System.Exception e)
                    {
                        Log.ErrorOnce($"[Entad Framework] trait visuals failed on {comp.parent}: {e}", 0x7A110000 ^ comp.parent.thingIDNumber);
                    }
                }
                if (!glint) continue;
                int seed = Gen.HashCombineInt(comp.parent.thingIDNumber, 0x5E1C);
                double period = MinPeriod + PeriodSpread * ((seed & 0xFF) / 255f);
                double t = now + period * (((seed >> 8) & 0xFFFF) / 65535f);
                double cycles = System.Math.Floor(t / period);
                int cycle = (int)(long)cycles;
                float u = (float)((t - cycles * period) / GlintSeconds);
                if (u >= 1f) continue;
                if (EntadSettings.GlintHideIdentified && !comp.HasHidden) continue;
                Thing anchor = Anchor(comp.parent);
                // Vanilla hides a pawn by its Position cell, while its draw position leads towards the next cell, so
                // the spot check in Draw alone would glint on a pawn still hidden as it walks out of fog
                if (anchor == null || anchor.Map != map || !view.Contains(anchor.Position) || anchor is Pawn && anchor.Position.Fogged(map)) continue;
                try { Draw(anchor, map, Mix(seed ^ cycle * 0x2C1B3C6D), u, size); }
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

        // Murmur3's finaliser. Gen.HashCombineInt only adds the value in, so consecutive cycles changed the low bits
        // by about one and the spot stayed put
        internal static int Mix(int h)
        {
            uint x = (uint)h;
            x ^= x >> 16;
            x *= 0x85EBCA6B;
            x ^= x >> 13;
            x *= 0xC2B2AE35;
            x ^= x >> 16;
            return (int)x;
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
