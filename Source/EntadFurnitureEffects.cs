using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Furniture traits that act on the building's surroundings: Luminous (a light of its own), Warm and Cool (heat
    // pushed into the room), Humble (counts for less colony wealth), Preserving (food stored on it rots slower) and
    // Fertile (more fertility for what grows in it). All are plain stats on the building, so they roll, scale and
    // show on the card like any other; each is read by one hook here.
    //
    // Buildings that light, heat or hide wealth are kept in one registry, refreshed when a building spawns, despawns
    // or changes traits. It ticks every 250 ticks over those buildings only (heat, and noticing a light).
    // The rot and fertility hooks look the building up from the item or cell they are asked about.
    public class EntadFurnitureEffects : GameComponent
    {
        private class Source
        {
            public CompEntad comp;
            public float heat;
            public bool humble;
            public CompGlower glower;
            public Map glowMap;
        }

        private static readonly List<Source> sources = new List<Source>();

        // Warm stops pushing heat at this room temperature and Cool at the other, so neither cooks or freezes a room
        // on its own: both stay inside vanilla's comfortable range
        private const float WarmUntil = 22f;
        private const float CoolUntil = 18f;

        // The effect has to be around this long before the colony notices it (as RevealProperty's lights and heat)
        private const int NoticeTicks = 2500;
        // Wealth isn't seen directly: a humble item is noticed after a day in the colony
        private const int HumbleNoticeTicks = 60000;

        public EntadFurnitureEffects(Game game)
        {
            sources.Clear();
        }

        private static Source Lookup(CompEntad comp)
        {
            for (int i = 0; i < sources.Count; i++) if (sources[i].comp == comp) return sources[i];
            return null;
        }

        public static void Refresh(CompEntad comp)
        {
            Thing thing = comp.parent;
            var source = Lookup(comp);
            bool spawned = thing.Spawned && thing.def.building != null && comp.activeTraits.Count > 0;
            float heat = 0f, glow = 0f;
            bool humble = false;
            if (spawned)
            {
                if (comp.HasStatTrait(EntadStatDefOf.Entad_RoomHeating)) heat += thing.GetStatValue(EntadStatDefOf.Entad_RoomHeating);
                if (comp.HasStatTrait(EntadStatDefOf.Entad_RoomCooling)) heat -= thing.GetStatValue(EntadStatDefOf.Entad_RoomCooling);
                if (comp.HasStatTrait(EntadStatDefOf.Entad_GlowRadius)) glow = thing.GetStatValue(EntadStatDefOf.Entad_GlowRadius);
                humble = comp.HasStatTrait(EntadStatDefOf.Entad_ColonyWealthFactor);
            }
            if (source != null && source.glower != null && (glow <= 0f || source.glowMap != thing.MapHeld || source.glower.Props.glowRadius != glow))
                RemoveGlower(source);
            if (heat == 0f && glow <= 0f && !humble)
            {
                if (source != null) sources.Remove(source);
                return;
            }
            if (source == null) sources.Add(source = new Source { comp = comp });
            source.heat = heat;
            source.humble = humble;
            if (glow > 0f && source.glower == null) AddGlower(source, glow);
        }

        // A vanilla CompGlower registered with the map's glow grid but never added to the building's comps, so it
        // isn't saved and nothing that looks for a lamp finds it. The grid reads only its parent's position and id
        // and its radius and colour. A building never moves while spawned, so it is registered once.
        private static void AddGlower(Source source, float radius)
        {
            Thing thing = source.comp.parent;
            // Only one trait can carry the stat (traits sharing a stat conflict)
            ColorInt color = source.comp.activeTraits
                .FirstOrDefault(t => t.def.AllRanges().Any(r => r.stat == EntadStatDefOf.Entad_GlowRadius))?.def.glowColor ?? EntadTraitDef.LampGlowColor;
            source.glower = new CompGlower
            {
                parent = (ThingWithComps)thing,
                props = new CompProperties_Glower { glowRadius = radius, glowColor = color },
            };
            source.glowMap = thing.Map;
            source.glowMap.glowGrid.RegisterGlower(source.glower);
        }

        private static void RemoveGlower(Source source)
        {
            // A map that was just removed has already disposed its glow grid
            var map = source.glowMap;
            if (map != null && map.glowGrid != null && Current.Game.Maps.Contains(map)) map.glowGrid.DeRegisterGlower(source.glower);
            source.glower = null;
            source.glowMap = null;
        }

        public override void GameComponentTick()
        {
            if (sources.Count == 0 || Find.TickManager.TicksGame % 250 != 0) return;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                var source = sources[i];
                Thing thing = source.comp.parent;
                // An abandoned map's buildings are never despawned one by one
                if (thing.Destroyed || !thing.Spawned)
                {
                    if (source.glower != null) RemoveGlower(source);
                    sources.RemoveAt(i);
                    continue;
                }
                bool noticed = thing.Faction == Faction.OfPlayer && source.comp.HasHidden && source.comp.TicksSinceSpawned >= NoticeTicks;
                if (source.glower != null && noticed) source.comp.RevealStat(EntadStatDefOf.Entad_GlowRadius);
                if (source.heat != 0f) PushHeat(source, thing, noticed);
            }
        }

        private static void PushHeat(Source source, Thing thing, bool noticed)
        {
            Room room = thing.GetRoom();
            if (room == null || room.UsesOutdoorTemperature) return;
            float temperature = room.Temperature;
            if (source.heat > 0f ? temperature >= WarmUntil : temperature <= CoolUntil) return;
            // Per second, over the 250 ticks since the last push, as CompHeatPusher
            GenTemperature.PushHeat(thing, source.heat * 250f / 60f);
            if (noticed) source.comp.RevealStat(source.heat > 0f ? EntadStatDefOf.Entad_RoomHeating : EntadStatDefOf.Entad_RoomCooling);
        }

        // Humble: the building counts toward colony wealth at its factor. Vanilla's own sum of building wealth just
        // ran; this takes the rest back out for the player's humble buildings on that map. Minified ones in storage
        // count as items, in full.
        internal static void ReduceWealth(Map map, ref float wealthBuildings)
        {
            for (int i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                if (!source.humble) continue;
                Thing thing = source.comp.parent;
                if (thing.Map != map || thing.Faction != Faction.OfPlayer) continue;
                float factor = thing.GetStatValue(EntadStatDefOf.Entad_ColonyWealthFactor);
                wealthBuildings -= thing.GetStatValue(StatDefOf.MarketValueIgnoreHp) * (1f - factor);
                if (source.comp.HasHidden && source.comp.TicksSinceSpawned >= HumbleNoticeTicks)
                    source.comp.RevealStat(EntadStatDefOf.Entad_ColonyWealthFactor);
            }
        }

        // The storage building an item sits in: the shelf whose cell it's on, or a building holding it inside
        private static Thing StorageOf(Thing item)
        {
            if (item.Spawned) return item.Position.GetSlotGroup(item.Map)?.parent as Thing;
            return item.ParentHolder as Building;
        }

        // Preserving: rot rate for food on a preserving shelf. Called for every rottable stack's rot step (every 250
        // ticks) and for rot time readouts; for anything not frozen it costs a slot group lookup, and for an entad
        // shelf a cached stat read.
        public static float RotRate(float rate, CompRottable rottable)
        {
            if (rate <= 0f) return rate;
            Thing storage = StorageOf(rottable.parent);
            if (storage == null || !EntadCompInjector.MayHaveComp(storage.def)) return rate;
            var comp = storage.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return rate;
            float factor = storage.GetStatValue(EntadStatDefOf.Entad_StoredRotFactor, true, 250);
            if (factor >= 1f) return rate;
            if (comp.HasHidden && storage.Faction == Faction.OfPlayer && comp.TicksSinceSpawned >= NoticeTicks)
                comp.RevealStat(EntadStatDefOf.Entad_StoredRotFactor);
            return rate * factor;
        }

        // Fertile: fertility of a cell whose planter has the trait
        internal static float Fertility(float fertility, IntVec3 loc, Map map)
        {
            Building edifice = loc.GetEdifice(map);
            if (edifice == null || !edifice.def.AffectsFertility || !EntadCompInjector.MayHaveComp(edifice.def)) return fertility;
            var comp = edifice.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return fertility;
            float offset = edifice.GetStatValue(EntadStatDefOf.Entad_FertilityOffset, true, 250);
            if (offset == 0f) return fertility;
            if (comp.HasHidden && edifice.Faction == Faction.OfPlayer && comp.TicksSinceSpawned >= NoticeTicks)
                comp.RevealStat(EntadStatDefOf.Entad_FertilityOffset);
            return Mathf.Max(0f, fertility + offset);
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
    public static class Patch_WealthWatcher_ForceRecount_Humble
    {
        public static void Postfix(Map ___map, ref float ___wealthBuildings)
        {
            EntadFurnitureEffects.ReduceWealth(___map, ref ___wealthBuildings);
        }
    }

    // Both the rot step and the time-until-rotten readout (inspect pane, caravan food) go through
    // GenTemperature.RotRateAtTemperature; each call's result is passed through RotRate
    [HarmonyPatch]
    public static class Patch_CompRottable_RotRate_Preserving
    {
        private static readonly MethodInfo rotRateAt = AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.RotRateAtTemperature));
        private static readonly MethodInfo rotRate = AccessTools.Method(typeof(EntadFurnitureEffects), nameof(EntadFurnitureEffects.RotRate));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.DeclaredMethod(typeof(CompRottable), "TickInterval");
            yield return AccessTools.DeclaredMethod(typeof(CompRottable), nameof(CompRottable.TicksUntilRotAtTemp));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.Calls(rotRateAt))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, rotRate);
                }
            }
        }
    }

    [HarmonyPatch(typeof(FertilityGrid), nameof(FertilityGrid.FertilityAt))]
    public static class Patch_FertilityGrid_FertilityAt_Fertile
    {
        public static void Postfix(IntVec3 loc, Map ___map, ref float __result)
        {
            __result = EntadFurnitureEffects.Fertility(__result, loc, ___map);
        }
    }
}
