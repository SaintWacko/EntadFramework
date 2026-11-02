using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Radiant: apparel with Entad_GlowRadius lights the cells around whoever wears it, or around itself while it
    // lies on the ground. Bastle Framework's moving light: a vanilla CompGlower registered with the map's glow grid
    // but never added to anything's comps, so nothing is saved and no GetComp finds it. The grid snapshots the
    // light's cell (its parent's Position) on registration and tells lights apart by their parent's thingIDNumber,
    // so a worn light's parent is the wearer and following it means deregistering and registering again. Each move
    // re-floods that one light and rebuilds the lighting mesh of the few map sections it covers; that rebuild is the
    // cost, and EntadSettings.LightFollowCells lets a walking wearer move it only every few cells.
    //
    // Items are kept wherever they are (worn, stored, in a caravan), registered on trait change and load like the
    // humble list, and dropped once destroyed. A pawn wearing two lights casts the larger one.
    public static class EntadWornLight
    {
        private class Light
        {
            public CompEntad comp;
            public CompGlower glower;
            public Thing owner;
            public Map map;
            public IntVec3 cell = IntVec3.Invalid;
        }

        private static readonly List<Light> lights = new List<Light>();

        public static void Clear() => lights.Clear();

        public static bool Any => lights.Count > 0;

        // From EntadFurnitureEffects.Refresh: spawn, despawn (put on, picked up), trait change, load
        public static void Refresh(CompEntad comp)
        {
            if (!comp.parent.def.IsApparel) return;
            int index = lights.FindIndex(l => l.comp == comp);
            bool has = comp.activeTraits.Count > 0 && comp.HasStatTrait(EntadStatDefOf.Entad_GlowRadius);
            if (has && index < 0) lights.Add(new Light { comp = comp });
            else if (!has && index >= 0)
            {
                Remove(lights[index]);
                lights.RemoveAt(index);
            }
        }

        // Every tick while any light exists: a cell compare per light unless something moved
        public static void Tick()
        {
            for (int i = lights.Count - 1; i >= 0; i--)
            {
                var light = lights[i];
                Thing item = light.comp.parent;
                // Gear on a discarded world pawn is never destroyed
                if (item.Destroyed || (item.ParentHolder is Pawn_ApparelTracker worn && worn.pawn.Discarded))
                {
                    Remove(light);
                    lights.RemoveAt(i);
                    continue;
                }
                Thing owner = OwnerOf(item, out float radius);
                if (owner != null && owner is Pawn pawn && !Strongest(light, pawn, radius)) owner = null;
                Update(light, owner, radius);
            }
        }

        // What the light hangs on, and how far it reaches: the wearer, or the item on the ground. Nothing while the
        // wearer is invisible (a glowing invisible raider gives itself away) or off a map. A bound item on someone
        // outside its bloodline reads 0, like its other stats.
        private static Thing OwnerOf(Thing item, out float radius)
        {
            radius = 0f;
            Thing owner;
            if (item.Spawned) owner = item;
            else if (item.ParentHolder is Pawn_ApparelTracker tracker && tracker.pawn.Spawned && !tracker.pawn.IsPsychologicallyInvisible()) owner = tracker.pawn;
            else return null;
            radius = item.GetStatValue(EntadStatDefOf.Entad_GlowRadius, true, 250);
            return radius > 0f ? owner : null;
        }

        // Only one light per pawn, so two worn lights don't share a glow grid id: the larger, then the first listed
        private static bool Strongest(Light light, Pawn pawn, float radius)
        {
            for (int i = 0; i < lights.Count; i++)
            {
                var other = lights[i];
                if (other == light || !(other.comp.parent.ParentHolder is Pawn_ApparelTracker t) || t.pawn != pawn || other.comp.parent.Destroyed) continue;
                float r = other.comp.parent.GetStatValue(EntadStatDefOf.Entad_GlowRadius, true, 250);
                if (r > radius || (r == radius && lights.IndexOf(other) < lights.IndexOf(light))) return false;
            }
            return true;
        }

        private static void Update(Light light, Thing owner, float radius)
        {
            if (owner == null)
            {
                Remove(light);
                return;
            }
            bool sameLight = light.glower != null && light.owner == owner && light.map == owner.Map && light.glower.Props.glowRadius == radius;
            if (sameLight)
            {
                if (light.cell == owner.Position) return;
                int follow = EntadSettings.LightFollowCells;
                // Catches up as soon as the wearer stops
                if (follow > 1 && owner is Pawn p && p.pather != null && p.pather.Moving
                    && light.cell.DistanceToSquared(owner.Position) < follow * follow)
                    return;
            }
            Remove(light);
            if (!sameLight || light.glower == null)
            {
                ColorInt color = EntadTraitDef.LampGlowColor;
                var traits = light.comp.activeTraits;
                for (int i = 0; i < traits.Count; i++)
                    if (traits[i].def.AllRanges().Any(r => r.stat == EntadStatDefOf.Entad_GlowRadius)) { color = traits[i].def.glowColor; break; }
                light.glower = new CompGlower
                {
                    parent = (ThingWithComps)owner,
                    props = new CompProperties_Glower { glowRadius = radius, glowColor = color },
                };
            }
            light.owner = owner;
            light.map = owner.Map;
            light.cell = owner.Position;
            light.map.glowGrid.RegisterGlower(light.glower);
        }

        private static void Remove(Light light)
        {
            // A map that was just removed has already disposed its glow grid
            var map = light.map;
            if (map != null && light.glower != null && map.glowGrid != null && Find.Maps.Contains(map)) map.glowGrid.DeRegisterGlower(light.glower);
            light.map = null;
            light.cell = IntVec3.Invalid;
        }
    }
}
