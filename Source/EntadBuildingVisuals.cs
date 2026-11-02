using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // When a building visual plays. Each is worked out at most every 250 game ticks per building, and only for
    // buildings in view, so a check can afford to look through a shelf's contents.
    public enum EntadBuildingVisualWhen
    {
        Always,
        // Its own power: on, or a building with no power switch of its own
        Powered,
        // The network it is on has a power source that is producing or holding power
        NetLive,
        // The network has a power source at all, flat or idle (where Leaky leaks)
        NetHasSource,
        // A solar flare is on and the network is still live through it (Stormcaught)
        FlareHeld,
        // Holds food that is rotting where it is (not frozen)
        HoldsRottable,
        // A plant is growing in it
        Growing,
        // Holds anything
        HoldsItems,
        // A condition a compat module registered under `custom` (EntadCompat.RegisterVisualCondition)
        Custom,
    }

    // Ambient effects on an installed building: mist off a cold shelf, sparks off a leaking battery. Drawn from the
    // per-frame entad pass (EntadGlint) for buildings in view; flecks are spawned on game ticks only, as on gear.
    public abstract class EntadBuildingVisual : EntadVisual
    {
        public EntadBuildingVisualWhen when = EntadBuildingVisualWhen.Always;
        [NoTranslate] public string custom;
        // Spots are taken over the footprint, kept this far inside its edges, then widened by spread cells on each side
        public float inset = 0.2f;
        public float spread;

        public abstract void Draw(EntadBuildingDraw draw);

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (when == EntadBuildingVisualWhen.Custom && custom.NullOrEmpty()) yield return $"{defName}: a building visual with when Custom needs a custom key";
            if (when != EntadBuildingVisualWhen.Custom && !custom.NullOrEmpty()) yield return $"{defName}: a building visual's custom key is only read with when Custom";
        }
    }

    // Shared state for one building's draw this frame
    public class EntadBuildingDraw
    {
        public Thing thing;
        public Map map;
        public CellRect rect;
        public int seed;
        public int ticks;
        public int newTicks;

        // A spot over the footprint for u, w in 0..1
        public Vector3 Spot(EntadBuildingVisual v, float u, float w)
        {
            float pad = v.spread - v.inset;
            float x0 = rect.minX - pad, x1 = rect.maxX + 1 + pad;
            float z0 = rect.minZ - pad, z1 = rect.maxZ + 1 + pad;
            if (x1 < x0) x0 = x1 = (rect.minX + rect.maxX + 1) * 0.5f;
            if (z1 < z0) z0 = z1 = (rect.minZ + rect.maxZ + 1) * 0.5f;
            return new Vector3(Mathf.Lerp(x0, x1, u), AltitudeLayer.MoteOverhead.AltitudeFor(), Mathf.Lerp(z0, z1, w));
        }
    }

    // Flecks rising, drifting or sinking off the building. perSecond is per cell of footprint, so a long table and a
    // lamp look equally misty, up to maxPerSecond.
    public class EntadVisual_BuildingFlecks : EntadBuildingVisual
    {
        public FleckDef fleck;
        public float perSecond = 1f;
        public float maxPerSecond = 8f;
        public FloatRange scale = new FloatRange(0.3f, 0.5f);
        public FloatRange angle = new FloatRange(-30f, 30f);
        public FloatRange speed = new FloatRange(0.1f, 0.3f);
        public FloatRange rotationRate = FloatRange.Zero;
        public Color color = Color.white;

        public override void Draw(EntadBuildingDraw d)
        {
            if (d.newTicks <= 0 || fleck == null) return;
            float chance = Mathf.Min(perSecond * d.rect.Area, maxPerSecond) / 60f;
            for (int t = 0; t < d.newTicks; t++)
            {
                if (!Rand.Chance(chance)) continue;
                Vector3 pos = d.Spot(this, Rand.Value, Rand.Value);
                if (!pos.ShouldSpawnMotesAt(d.map) || pos.ToIntVec3().Fogged(d.map)) continue;
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
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (fleck == null) yield return $"{defName}: building flecks visual needs a fleck";
        }
    }

    // Arcs jumping between spots over the building, each slot on its own clock, like the worn arcs
    public class EntadVisual_BuildingArcs : EntadBuildingVisual
    {
        [NoTranslate] public string texFolder = "Things/Mote/EntadArc";
        public Color color = new Color(0.7f, 0.85f, 1f, 0.9f);
        // Arcs that can show at once
        public int count = 1;
        public int intervalTicks = 40;
        public float chance = 0.5f;
        public int arcTicks = 5;
        public float width = 0.2f;
        public float minLength = 0.4f;
        public float maxLength = 1.5f;

        [Unsaved] private List<Material> materials;

        public override void Draw(EntadBuildingDraw d)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.MoteGlow, color, "arc");
            if (mats.Count == 0) return;
            int interval = Mathf.Max(arcTicks + 1, intervalTicks);
            for (int i = 0; i < count; i++)
            {
                int slot = Gen.HashCombineInt(d.seed, i * 7919 + 31);
                int t = d.ticks + (slot & 0xFFFF);
                if (t % interval >= arcTicks) continue;
                int seed = EntadGlint.Mix(slot ^ (t / interval) * 0x2C1B3C6D);
                if ((seed & 0xFFFF) / 65536f >= chance) continue;
                Vector3 a = SpotFor(d, seed);
                Vector3 b = a;
                for (int k = 1; k <= 6; k++)
                {
                    b = SpotFor(d, Gen.HashCombineInt(seed, k));
                    float len = (b - a).MagnitudeHorizontal();
                    if (len >= minLength && len <= maxLength) break;
                    // Too long: pull the far end back along the line rather than give up on the arc
                    if (len > maxLength) { b = a + (b - a) * (maxLength / len); break; }
                }
                if (a.ToIntVec3().Fogged(d.map)) continue;
                EntadVisuals.DrawArc(mats[((seed >> 4) & 0x7FFFFFFF) % mats.Count], a, b, width, minLength);
            }
        }

        private Vector3 SpotFor(EntadBuildingDraw d, int hash)
        {
            hash = EntadGlint.Mix(hash);
            return d.Spot(this, (hash & 0xFFFF) / 65535f, ((hash >> 16) & 0xFFFF) / 65535f);
        }
    }

    // A slowly turning sprite over the building, one per cell by default (a portal in each shelf slot), drawn over
    // what the building holds and pulsing faintly
    public class EntadVisual_BuildingSwirl : EntadBuildingVisual
    {
        [NoTranslate] public string texPath = "Things/Mote/EntadSwirl";
        public Color color = new Color(0.2f, 0.08f, 0.3f, 0.5f);
        public bool perCell = true;
        // Size in cells (per cell), degrees turned per second, and how far the alpha dips over pulseSeconds
        public float size = 0.85f;
        public float degreesPerSecond = 20f;
        public float pulse = 0.3f;
        public float pulseSeconds = 4f;

        [Unsaved] private Material material;

        public override void Draw(EntadBuildingDraw d)
        {
            if (material == null) material = MaterialPool.MatFrom(texPath, ShaderDatabase.Transparent, Color.white);
            float seconds = d.ticks / 60f;
            float angle = (seconds * degreesPerSecond + (d.seed & 0xFFF)) % 360f;
            float phase = (seconds / Mathf.Max(0.1f, pulseSeconds) + (d.seed & 0xFF) / 255f) % 1f;
            Color c = color;
            c.a *= 1f - pulse * 0.5f * (1f + Mathf.Sin(phase * 2f * Mathf.PI));
            EntadVisuals.swirlBlock.SetColor(ShaderPropertyIDs.Color, c);
            float y = AltitudeLayer.ItemImportant.AltitudeFor() + 0.01f;
            if (!perCell)
            {
                Vector3 center = d.rect.CenterVector3;
                float s = size * Mathf.Min(d.rect.Width, d.rect.Height);
                DrawOne(new Vector3(center.x, y, center.z), s, angle);
                return;
            }
            int k = 0;
            foreach (var cell in d.rect)
            {
                if (cell.Fogged(d.map)) continue;
                Vector3 pos = cell.ToVector3Shifted();
                pos.y = y;
                // Neighbouring cells turn a little out of step
                DrawOne(pos, size, angle + k++ * 47f);
            }
        }

        private void DrawOne(Vector3 pos, float s, float angle)
        {
            Graphics.DrawMesh(MeshPool.plane10, Matrix4x4.TRS(pos, Quaternion.AngleAxis(angle, Vector3.up), new Vector3(s, 1f, s)), material, 0, null, 0, EntadVisuals.swirlBlock);
        }
    }

    // Installed buildings whose traits have building visuals, revealed or not (what plays is read from the item's
    // visual set on each draw). Kept current from EntadFurnitureEffects.Refresh: spawn, despawn, trait change, load.
    public static class EntadBuildingVisuals
    {
        private class Entry
        {
            public CompEntad comp;
            public int needed;
            public List<string> customKeys;
            // Bits of the conditions that held at the last check, and the custom keys that did
            public int met;
            public HashSet<string> customMet;
            public int checkAt = -1;
        }

        private const int CheckInterval = 250;
        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly EntadBuildingDraw draw = new EntadBuildingDraw();

        public static bool Any => entries.Count > 0;
        public static bool On => EntadSettings.Visuals && EntadSettings.VisualsFurniture;

        public static void Clear() => entries.Clear();

        private static int Bit(EntadBuildingVisualWhen when) => 1 << (int)when;

        public static void Refresh(CompEntad comp)
        {
            int index = entries.FindIndex(e => e.comp == comp);
            Thing thing = comp.parent;
            bool wanted = EntadTraitDef.AnyBuildingVisuals && thing.Spawned && thing.def.building != null && comp.HasBuildingVisualTraits;
            if (!wanted)
            {
                if (index >= 0) entries.RemoveAt(index);
                return;
            }
            var entry = index >= 0 ? entries[index] : new Entry { comp = comp };
            entry.needed = 0;
            entry.customKeys = null;
            entry.checkAt = -1;
            foreach (var trait in comp.activeTraits)
            {
                if (trait.def.visuals == null) continue;
                foreach (var v in trait.def.visuals)
                {
                    if (!(v is EntadBuildingVisual b)) continue;
                    entry.needed |= Bit(b.when);
                    if (b.when == EntadBuildingVisualWhen.Custom) (entry.customKeys ??= new List<string>()).Add(b.custom);
                }
            }
            if (index < 0) entries.Add(entry);
        }

        // From EntadGlint's per-frame pass, ahead of its own list: buildings are few, and most frames only test
        // whether each is in view
        public static void Draw(Map map)
        {
            CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(2);
            int now = Find.TickManager.TicksGame;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var entry = entries[i];
                Thing thing = entry.comp.parent;
                // An abandoned map's buildings are never despawned one by one
                if (thing.Destroyed || !thing.Spawned)
                {
                    entries.RemoveAt(i);
                    continue;
                }
                if (thing.Map != map) continue;
                CellRect rect = thing.OccupiedRect();
                if (!rect.Overlaps(view)) continue;
                var set = entry.comp.Visuals;
                if (set.building.Length == 0) continue;
                if (now >= entry.checkAt || entry.checkAt - now > CheckInterval)
                {
                    entry.checkAt = now + CheckInterval;
                    Check(entry, thing);
                }
                if (entry.met == 0 && entry.customMet == null) continue;
                try { Run(entry, set, thing, map, rect, now); }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] building visuals failed on {thing}: {e}", 0x7A120000 ^ thing.thingIDNumber);
                }
            }
        }

        private static void Run(Entry entry, EntadVisualSet set, Thing thing, Map map, CellRect rect, int now)
        {
            int id = thing.thingIDNumber;
            var d = draw;
            d.thing = thing;
            d.map = map;
            d.rect = rect;
            d.seed = Gen.HashCombineInt(id, 0x5B11);
            d.ticks = now;
            d.newTicks = EntadVisuals.NewTicks(id, now);
            Rand.PushState(Gen.HashCombineInt(d.seed, now));
            try
            {
                for (int i = 0; i < set.building.Length; i++)
                {
                    var v = set.building[i];
                    bool met = v.when == EntadBuildingVisualWhen.Custom ? entry.customMet != null && entry.customMet.Contains(v.custom) : (entry.met & Bit(v.when)) != 0;
                    if (met) v.Draw(d);
                }
            }
            finally { Rand.PopState(); }
        }

        private static void Check(Entry entry, Thing thing)
        {
            int met = 0;
            for (int w = 0; w < (int)EntadBuildingVisualWhen.Custom; w++)
            {
                var when = (EntadBuildingVisualWhen)w;
                if ((entry.needed & Bit(when)) != 0 && Holds(when, thing)) met |= Bit(when);
            }
            entry.met = met;
            entry.customMet = null;
            if (entry.customKeys == null) return;
            foreach (string key in entry.customKeys)
                if (EntadCompat.VisualCondition(key, thing)) (entry.customMet ??= new HashSet<string>()).Add(key);
        }

        private static bool Holds(EntadBuildingVisualWhen when, Thing thing)
        {
            switch (when)
            {
                case EntadBuildingVisualWhen.Always:
                    return true;
                case EntadBuildingVisualWhen.Powered:
                    return (thing as ThingWithComps)?.GetComp<CompPowerTrader>()?.PowerOn ?? true;
                case EntadBuildingVisualWhen.NetLive:
                    return NetOf(thing)?.HasActivePowerSource == true;
                case EntadBuildingVisualWhen.NetHasSource:
                    return NetOf(thing)?.hasPowerSource == true;
                case EntadBuildingVisualWhen.FlareHeld:
                    return thing.Map.gameConditionManager.ElectricityDisabled(thing.Map) && NetOf(thing)?.HasActivePowerSource == true;
                case EntadBuildingVisualWhen.HoldsRottable:
                    return AnyHeld(thing, t => t.TryGetComp<CompRottable>() is CompRottable r && r.Stage == RotStage.Fresh
                        && GenTemperature.RotRateAtTemperature(t.AmbientTemperature) > 0.001f);
                case EntadBuildingVisualWhen.Growing:
                    foreach (var cell in thing.OccupiedRect())
                        if (cell.GetPlant(thing.Map) is Plant plant && plant.LifeStage == PlantLifeStage.Growing) return true;
                    return false;
                case EntadBuildingVisualWhen.HoldsItems:
                    return AnyHeld(thing, t => true);
            }
            return false;
        }

        private static PowerNet NetOf(Thing thing) => (thing as ThingWithComps)?.GetComp<CompPower>()?.PowerNet;

        // A shelf's slot group, or what a container building holds inside itself
        private static bool AnyHeld(Thing thing, System.Predicate<Thing> match)
        {
            if (thing is Building_Storage storage)
            {
                var group = storage.GetSlotGroup();
                if (group != null)
                    foreach (var held in group.HeldThings)
                        if (match(held)) return true;
                return false;
            }
            if (thing is IThingHolder holder)
            {
                var owner = holder.GetDirectlyHeldThings();
                if (owner != null)
                    for (int i = 0; i < owner.Count; i++)
                        if (match(owner[i])) return true;
            }
            return false;
        }
    }
}
