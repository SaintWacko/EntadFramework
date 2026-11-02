using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // One entry of a trait's <visuals> list. By default it plays before its trait is identified (setting
    // VisualsUnidentified): frost mist off a coat says something cold is in it, not what sets it off or how strong it
    // is. Nothing is revealed by it; identifying the item stays the way to learn the rest.
    public abstract class EntadVisual
    {
        // Null follows the setting; true plays while the trait is hidden whatever the setting, false only once it is
        // fully revealed
        public bool? beforeIdentified;

        public virtual IEnumerable<string> ConfigErrors(string defName) { yield break; }
    }

    // The item's visuals that are playing, by kind; rebuilt when its traits or what is revealed of them change
    internal class EntadVisualSet
    {
        public EntadWeaponVisual[] weapon = new EntadWeaponVisual[0];
        public EntadVisual_ProjectileTrail[] trails = new EntadVisual_ProjectileTrail[0];
        public EntadWornVisual[] worn = new EntadWornVisual[0];
        public EntadEventVisual[] events = new EntadEventVisual[0];
        public EntadBuildingVisual[] building = new EntadBuildingVisual[0];
        // EntadEventVisual.Bit of every event the set plays on
        public int eventMask;
        public bool Any => weapon.Length > 0 || trails.Length > 0 || worn.Length > 0 || events.Length > 0 || building.Length > 0;

        // Bumped when a setting changes which entries play, so every item's cached set is rebuilt on its next draw
        // without walking the items
        internal static int version;
        internal int builtVersion;

        internal static void Invalidate() => version++;

        public static EntadVisualSet For(CompEntad comp)
        {
            List<EntadWeaponVisual> weapon = null;
            List<EntadVisual_ProjectileTrail> trails = null;
            List<EntadWornVisual> worn = null;
            List<EntadEventVisual> events = null;
            List<EntadBuildingVisual> building = null;
            var traits = comp.activeTraits;
            for (int i = 0; i < traits.Count; i++)
            {
                var visuals = traits[i].def.visuals;
                if (visuals == null) continue;
                bool hidden = traits[i].AnyHidden;
                foreach (var v in visuals)
                {
                    if (hidden && !(v.beforeIdentified ?? EntadSettings.VisualsUnidentified)) continue;
                    if (v is EntadWeaponVisual w) (weapon ?? (weapon = new List<EntadWeaponVisual>())).Add(w);
                    else if (v is EntadVisual_ProjectileTrail p) (trails ?? (trails = new List<EntadVisual_ProjectileTrail>())).Add(p);
                    else if (v is EntadWornVisual o) (worn ?? (worn = new List<EntadWornVisual>())).Add(o);
                    else if (v is EntadEventVisual e) (events ?? (events = new List<EntadEventVisual>())).Add(e);
                    else if (v is EntadBuildingVisual b) (building ?? (building = new List<EntadBuildingVisual>())).Add(b);
                }
            }
            if (weapon == null && trails == null && worn == null && events == null && building == null) return new EntadVisualSet { builtVersion = version };
            var set = new EntadVisualSet { builtVersion = version };
            if (weapon != null) set.weapon = weapon.ToArray();
            if (trails != null) set.trails = trails.ToArray();
            if (worn != null) set.worn = worn.ToArray();
            if (building != null) set.building = building.ToArray();
            if (events != null)
            {
                set.events = events.ToArray();
                foreach (var e in events) set.eventMask |= EntadEventVisual.Bit(e.on);
            }
            return set;
        }
    }

    [StaticConstructorOnStartup]
    public static partial class EntadVisuals
    {
        private static readonly EntadWeaponDraw draw = new EntadWeaponDraw();
        // For the weapon glow. Here rather than on EntadVisual_WeaponGlow: that class is built by the def loader and
        // has no StaticConstructorOnStartup, so RimWorld warns about a Unity object field on it at startup.
        internal static readonly MaterialPropertyBlock glowBlock = new MaterialPropertyBlock();
        internal static readonly MaterialPropertyBlock swirlBlock = new MaterialPropertyBlock();
        // Last game tick each weapon or worn item spawned flecks, by thing id. Cleared when it grows large: that costs
        // an item one skipped tick of flecks, not a leak.
        private static readonly Dictionary<int, int> lastFleckTick = new Dictionary<int, int>();
        private const int MaxCatchUpTicks = 6;

        public static bool GearOn => EntadSettings.Visuals && EntadSettings.VisualsGear;

        public static void Clear()
        {
            lastFleckTick.Clear();
            trails.Clear();
            zaps.Clear();
            lastEventTick.Clear();
            blastHits.Clear();
            eventContext.Reset(null, null);
            EntadAfterimages.Clear();
            EntadRibbons.Clear();
            EntadBuildingVisuals.Clear();
        }

        // Game ticks since the item last spawned flecks, capped, and 1 the first time it's seen
        internal static int NewTicks(int id, int now)
        {
            int newTicks;
            if (lastFleckTick.TryGetValue(id, out int last)) newTicks = Mathf.Clamp(now - last, 0, MaxCatchUpTicks);
            else
            {
                newTicks = 1;
                if (lastFleckTick.Count > 1024) lastFleckTick.Clear();
            }
            if (newTicks > 0 || last == 0) lastFleckTick[id] = now;
            return newTicks;
        }

        // Every texture in a folder as a material, for flicker frames and arc variants. Called on first draw (main
        // thread), never while defs load.
        internal static List<Material> LoadFrames(string folder, Shader shader, Color color, string what)
        {
            var materials = new List<Material>();
            foreach (var tex in ContentFinder<Texture2D>.GetAllInFolder(folder))
                materials.Add(MaterialPool.MatFrom(tex, shader, color));
            if (materials.Count == 0) Log.ErrorOnce($"[Entad Framework] no textures in {what} folder '{folder}'", folder.GetHashCode());
            return materials;
        }

        // An arc sprite stretched from a to b; none when the spots are closer than minLength
        internal static void DrawArc(Material mat, Vector3 a, Vector3 b, float width, float minLength, bool drawNow = false)
        {
            Vector3 dir = b - a;
            float length = Mathf.Sqrt(dir.MagnitudeHorizontalSquared());
            if (length < minLength) return;
            float deg = -Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
            Vector3 mid = (a + b) * 0.5f;
            mid.y += 0.002f;
            GenDraw.DrawMeshNowOrLater(MeshPool.plane10, Matrix4x4.TRS(mid, Quaternion.AngleAxis(deg, Vector3.up), new Vector3(length * 1.15f, 1f, width)), mat, drawNow);
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
            if (!(thing.def.IsWeapon || thing.def.IsApparel) || !view.Contains(thing.Position) || thing.Position.Fogged(map)) return;
            var set = comp.Visuals;
            if (set.weapon.Length == 0 && set.worn.Length == 0) return;
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
            // The random-rotation wrapper has no graphic data of its own, so its DrawOffset is always zero; vanilla's
            // Print gets the offset through the sub-graphic
            Graphic placed = (graphic as Graphic_RandomRotated)?.SubGraphic ?? graphic;
            Vector3 pos = thing.DrawPos + placed.DrawOffset(thing.Rotation);
            float altitude = AltitudeLayer.Item.AltitudeFor() + 0.01f;
            Matrix4x4 matrix = Matrix4x4.TRS(pos, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(size.x, 1f, size.y));
            if (set.weapon.Length > 0) Run(set, thing, map, matrix, false, graphic.MatSingleFor(thing), size, altitude);
            if (set.worn.Length > 0) DrawWornOnGround(set, thing, map, matrix, size);
        }

        internal static void Run(EntadVisualSet set, Thing weapon, Map map, Matrix4x4 matrix, bool flipped, Material mat, Vector2 size, float altitude)
        {
            var shape = EntadWeaponShape.For(mat?.mainTexture);
            if (shape == null) return;
            int now = Find.TickManager.TicksGame;
            int id = weapon.thingIDNumber;
            int newTicks = NewTicks(id, now);

            draw.weapon = weapon;
            draw.map = map;
            draw.matrix = matrix;
            draw.flipped = flipped;
            draw.shape = shape;
            draw.texture = mat.mainTexture;
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
