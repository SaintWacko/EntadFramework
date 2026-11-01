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

    [StaticConstructorOnStartup]
    public static partial class EntadVisuals
    {
        private static readonly EntadWeaponDraw draw = new EntadWeaponDraw();
        // Last game tick each weapon spawned flecks, by thing id. Cleared when it grows large: that costs a weapon one
        // skipped tick of flecks, not a leak.
        private static readonly Dictionary<int, int> lastFleckTick = new Dictionary<int, int>();
        private const int MaxCatchUpTicks = 6;

        public static bool GearOn => EntadSettings.Visuals && EntadSettings.VisualsGear;

        public static void Clear()
        {
            lastFleckTick.Clear();
            trails.Clear();
            zaps.Clear();
        }

        // In a pawn's hands: the matrix is rebuilt the way DrawEquipmentAiming builds it. drawLoc already carries the
        // recoil offset, since vanilla adds it to its own parameter and a postfix reads parameters after the body ran;
        // the recoil angle lives in a local there, so it is added here. Combat Extended transpiles DrawEquipmentAiming
        // (its own recoil and draw offsets), which the effects don't follow.
        public static void DrawHeld(Thing eq, Vector3 drawLoc, float aimAngle)
        {
            if (!(eq.ParentHolder is Pawn_EquipmentTracker tracker) || !(eq is ThingWithComps weapon)) return;
            Pawn pawn = tracker.pawn;
            // Portraits and statues draw an unspawned stand-in
            if (!pawn.Spawned || pawn.Map != Find.CurrentMap || pawn.IsHiddenFromPlayer()) return;
            var set = weapon.GetComp<CompEntad>()?.Visuals;
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
            var equippable = weapon.GetComp<CompEquippable>();
            if (equippable != null)
            {
                EquipmentUtility.Recoil(eq.def, EquipmentUtility.GetRecoilVerb(equippable.AllVerbs), out _, out var angleOffset, aimAngle);
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
            Vector3 pos = thing.DrawPos + graphic.DrawOffset(thing.Rotation);
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
            // Draw code must not move the game's shared random stream, or how far it moves would depend on frame rate
            // and what's on screen (vanilla's Recoil does the same in this method)
            Rand.PushState(Gen.HashCombineInt(draw.seed, now));
            try
            {
                for (int i = 0; i < set.weapon.Length; i++)
                {
                    try { set.weapon[i].Draw(draw); }
                    catch (System.Exception e)
                    {
                        Log.ErrorOnce($"[Entad Framework] weapon visual {set.weapon[i].GetType().Name} failed on {weapon}: {e}", Gen.HashCombineInt(id, i));
                    }
                }
            }
            finally { Rand.PopState(); }
        }
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
}
