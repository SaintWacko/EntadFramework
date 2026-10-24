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
    // Traits that act on an entad's surroundings: Luminous (a light of its own), Warm, Cool, Sweltering and Frigid
    // (heat pushed into the room), Preserving and Spoiling (food stored on it rots slower or faster) and Fertile and
    // Barren (fertility for what grows in it) on furniture, Humble (counts for less colony wealth) on furniture and
    // gear, and Shedding (leaves filth) on anything. All are plain stats on the item, so they roll, scale and show on
    // the card like any other; each is read by one hook here.
    //
    // Buildings that light or heat are kept in one registry, refreshed when a building spawns, despawns or changes
    // traits; it ticks every 250 ticks over those buildings only. Humble and Shedding items are two more lists, kept
    // wherever the item is (worn, stored, minified): Humble is only read on vanilla's wealth recount, Shedding on the
    // same 250-tick pass. The rot and fertility hooks look the building up from the item or cell they are asked about.
    public class EntadFurnitureEffects : GameComponent
    {
        private class Source
        {
            public CompEntad comp;
            public float heat;
            public float targetTemperature;
            public CompGlower glower;
            public Map glowMap;
        }

        private class HumbleItem
        {
            public CompEntad comp;
        }

        private static readonly List<Source> sources = new List<Source>();
        private static readonly List<HumbleItem> humbleItems = new List<HumbleItem>();
        private static readonly List<CompEntad> sheddingItems = new List<CompEntad>();

        // The effect has to be around this long before the colony notices it (as RevealProperty's lights and heat)
        private const int NoticeTicks = 2500;
        // Wealth isn't seen directly: a humble item is noticed after a day in the colony
        private const int HumbleNoticeTicks = 60000;

        public EntadFurnitureEffects(Game game)
        {
            sources.Clear();
            humbleItems.Clear();
            sheddingItems.Clear();
        }

        private static Source Lookup(CompEntad comp)
        {
            for (int i = 0; i < sources.Count; i++) if (sources[i].comp == comp) return sources[i];
            return null;
        }

        public static void Refresh(CompEntad comp)
        {
            RefreshHumble(comp);
            RefreshShedding(comp);
            EntadPowerNets.Refresh(comp);
            Thing thing = comp.parent;
            var source = Lookup(comp);
            bool spawned = thing.Spawned && thing.def.building != null && comp.activeTraits.Count > 0;
            float heat = 0f, glow = 0f;
            if (spawned)
            {
                if (comp.HasStatTrait(EntadStatDefOf.Entad_RoomHeating)) heat += thing.GetStatValue(EntadStatDefOf.Entad_RoomHeating);
                if (comp.HasStatTrait(EntadStatDefOf.Entad_RoomCooling)) heat -= thing.GetStatValue(EntadStatDefOf.Entad_RoomCooling);
                if (comp.HasStatTrait(EntadStatDefOf.Entad_GlowRadius)) glow = thing.GetStatValue(EntadStatDefOf.Entad_GlowRadius);
            }
            if (source != null && source.glower != null && (glow <= 0f || source.glowMap != thing.MapHeld || source.glower.Props.glowRadius != glow))
                RemoveGlower(source);
            if (heat == 0f && glow <= 0f)
            {
                if (source != null) sources.Remove(source);
                return;
            }
            if (source == null) sources.Add(source = new Source { comp = comp });
            source.heat = heat;
            if (heat != 0f) source.targetTemperature = TargetTemperature(comp, heat > 0f);
            if (glow > 0f && source.glower == null) AddGlower(source, glow);
        }

        // The temperature the trait doing the net heating (or cooling) aims for. Only a modded item would carry both.
        private static float TargetTemperature(CompEntad comp, bool heating)
        {
            StatDef stat = heating ? EntadStatDefOf.Entad_RoomHeating : EntadStatDefOf.Entad_RoomCooling;
            var trait = comp.activeTraits.FirstOrDefault(t => t.def.AllRanges().Any(r => r.stat == stat));
            return trait?.def.TargetTemperature(heating) ?? (heating ? EntadTraitDef.DefaultWarmTarget : EntadTraitDef.DefaultCoolTarget);
        }

        private static void RefreshHumble(CompEntad comp)
        {
            int index = humbleItems.FindIndex(h => h.comp == comp);
            bool humble = comp.activeTraits.Count > 0 && comp.HasStatTrait(EntadStatDefOf.Entad_ColonyWealthFactor);
            if (humble && index < 0) humbleItems.Add(new HumbleItem { comp = comp });
            else if (!humble && index >= 0) humbleItems.RemoveAt(index);
        }

        private static void RefreshShedding(CompEntad comp)
        {
            int index = sheddingItems.IndexOf(comp);
            bool shedding = comp.activeTraits.Count > 0 && comp.HasStatTrait(EntadStatDefOf.Entad_FilthPerDay);
            if (shedding && index < 0) sheddingItems.Add(comp);
            else if (!shedding && index >= 0) sheddingItems.RemoveAt(index);
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
            if (Find.TickManager.TicksGame % 250 != 0) return;
            if (sheddingItems.Count > 0) TickShedding();
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

        // Shedding: filth around a building, or where the pawn or container holding the item is. A bound item on the
        // wrong pawn reads 0, like its other stats. Removed only once destroyed, as the humble list.
        private static void TickShedding()
        {
            for (int i = sheddingItems.Count - 1; i >= 0; i--)
            {
                var comp = sheddingItems[i];
                Thing thing = comp.parent;
                if (thing.Destroyed)
                {
                    sheddingItems.RemoveAt(i);
                    continue;
                }
                Thing at = thing.SpawnedParentOrMe;
                if (at == null) continue;
                float perDay = thing.GetStatValue(EntadStatDefOf.Entad_FilthPerDay, true, 250);
                if (perDay <= 0f || !Rand.Chance(perDay * 250f / 60000f)) continue;
                IntVec3 cell = at.def.building != null ? at.RandomAdjacentCell8Way() : at.Position;
                if (!cell.InBounds(at.Map)) continue;
                ThingDef filth = comp.activeTraits
                    .FirstOrDefault(t => t.def.AllRanges().Any(r => r.stat == EntadStatDefOf.Entad_FilthPerDay))?.def.filth ?? ThingDefOf.Filth_Dirt;
                // Seen at home on the colony's own things or loose items; not on a raider's gear
                if (FilthMaker.TryMakeFilth(cell, at.Map, filth) && comp.HasHidden && at.Map.IsPlayerHome
                    && (at.Faction == null || at.Faction == Faction.OfPlayer))
                    comp.RevealStat(EntadStatDefOf.Entad_FilthPerDay);
            }
        }

        private static void PushHeat(Source source, Thing thing, bool noticed)
        {
            Room room = thing.GetRoom();
            if (room == null || room.UsesOutdoorTemperature) return;
            float temperature = room.Temperature;
            if (source.heat > 0f ? temperature >= source.targetTemperature : temperature <= source.targetTemperature) return;
            // Per second, over the 250 ticks since the last push, as CompHeatPusher
            GenTemperature.PushHeat(thing, source.heat * 250f / 60f);
            if (noticed) source.comp.RevealStat(source.heat > 0f ? EntadStatDefOf.Entad_RoomHeating : EntadStatDefOf.Entad_RoomCooling);
        }

        // Humble: the item counts toward colony wealth at its factor. Vanilla's own sums just ran; this takes the rest
        // back out of the sum vanilla put the item in: buildings at their undamaged value while built by the player,
        // anything else (worn, carried, stored, a minified building) at its market value wherever vanilla's item count
        // reaches it. A bound item on the wrong pawn reads factor 1, like its other stats.
        internal static void ReduceWealth(Map map, ref float wealthBuildings, ref float wealthItems)
        {
            for (int i = humbleItems.Count - 1; i >= 0; i--)
            {
                var entry = humbleItems[i];
                Thing thing = entry.comp.parent;
                if (thing.Destroyed)
                {
                    humbleItems.RemoveAt(i);
                    continue;
                }
                float factor;
                if (thing.Spawned && thing.def.building != null)
                {
                    if (thing.Map != map || thing.Faction != Faction.OfPlayer) continue;
                    factor = thing.GetStatValue(EntadStatDefOf.Entad_ColonyWealthFactor);
                    if (factor >= 1f) continue;
                    wealthBuildings -= thing.GetStatValue(StatDefOf.MarketValueIgnoreHp) * (1f - factor);
                }
                else
                {
                    Thing counted = thing.ParentHolder as MinifiedThing ?? thing;
                    if (!CountedAsItem(counted, map)) continue;
                    factor = thing.GetStatValue(EntadStatDefOf.Entad_ColonyWealthFactor);
                    if (factor >= 1f) continue;
                    wealthItems -= counted.MarketValue * counted.stackCount * (1f - factor);
                }
                // The day before the colony notices counts from the first recount that took the item out of the
                // player's wealth, not from when it was generated on some trader's pawn; saved, so reloads don't reset it
                if (entry.comp.humbleSinceTick < 0) entry.comp.humbleSinceTick = Find.TickManager.TicksGame;
                else if (entry.comp.HasHidden && Find.TickManager.TicksGame - entry.comp.humbleSinceTick >= HumbleNoticeTicks)
                    entry.comp.RevealStat(EntadStatDefOf.Entad_ColonyWealthFactor);
            }
        }

        // The same test vanilla's item count makes (WealthWatcher.CalculateWealthItems): on this map, not fogged, and
        // no holder on the way up it skips (another faction's pawns, quest lodgers, passing ships)
        private static bool CountedAsItem(Thing thing, Map map)
        {
            if (thing.MapHeld != map || !thing.SpawnedOrAnyParentSpawned || thing.PositionHeld.Fogged(map)) return false;
            for (IThingHolder holder = thing.ParentHolder; holder != null; holder = holder.ParentHolder)
                if (!WealthWatcher.WealthItemsFilter(holder)) return false;
            return true;
        }

        // The storage building an item sits in: the shelf whose cell it's on, or a building holding it inside
        private static Thing StorageOf(Thing item)
        {
            if (item.Spawned) return item.Position.GetSlotGroup(item.Map)?.parent as Thing;
            return item.ParentHolder as Building;
        }

        // Preserving and Spoiling: rot rate for food on the shelf. Called for every rottable stack's rot step (every
        // 250 ticks); for anything not frozen it costs a slot group lookup, and for an entad shelf a cached stat read.
        public static float RotRate(float rate, CompRottable rottable)
        {
            if (rate <= 0f) return rate;
            float factor = StoredRotFactor(rottable, out var comp);
            if (factor == 1f) return rate;
            if (comp.HasHidden && comp.parent.Faction == Faction.OfPlayer && comp.TicksSinceSpawned >= NoticeTicks)
                comp.RevealStat(EntadStatDefOf.Entad_StoredRotFactor);
            return rate * factor;
        }

        // The time-until-rotten readout for food where it sits now. The rot step's reveal covers noticing the trait.
        internal static int TicksUntilRot(int ticks, CompRottable rottable)
        {
            if (ticks <= 0 || ticks >= 72000000) return ticks;
            float factor = StoredRotFactor(rottable, out _);
            return factor == 1f || factor <= 0f ? ticks : Mathf.Min(72000000, Mathf.RoundToInt(ticks / factor));
        }

        private static float StoredRotFactor(CompRottable rottable, out CompEntad comp)
        {
            comp = null;
            Thing storage = StorageOf(rottable.parent);
            if (storage == null || !EntadCompInjector.MayHaveComp(storage.def)) return 1f;
            comp = storage.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return 1f;
            return storage.GetStatValue(EntadStatDefOf.Entad_StoredRotFactor, true, 250);
        }

        private static CompEntad PlanterComp(IntVec3 loc, Map map, out Building planter)
        {
            planter = loc.GetEdifice(map);
            if (planter == null || !planter.def.AffectsFertility || !EntadCompInjector.MayHaveComp(planter.def)) return null;
            var comp = planter.TryGetComp<CompEntad>();
            return comp == null || comp.activeTraits.Count == 0 ? null : comp;
        }

        // Fertile: fertility of a cell whose planter has the trait. Only ever raised: sowing is gated on
        // fertility (most crops need 90%, and a plant pot only has 100%), so lowering it would leave a pot that
        // nothing can be planted in. Barren slows growth below instead.
        internal static float Fertility(float fertility, IntVec3 loc, Map map)
        {
            var comp = PlanterComp(loc, map, out Building planter);
            if (comp == null) return fertility;
            float offset = planter.GetStatValue(EntadStatDefOf.Entad_FertilityOffset, true, 250);
            if (offset <= 0f) return fertility;
            if (comp.HasHidden && planter.Faction == Faction.OfPlayer && comp.TicksSinceSpawned >= NoticeTicks)
                comp.RevealStat(EntadStatDefOf.Entad_FertilityOffset);
            return fertility + offset;
        }

        // Barren: the fertility part of a plant's growth rate, for a plant in a planter with the trait
        internal static float PlantGrowth(float factor, Plant plant)
        {
            if (!plant.Spawned) return factor;
            var comp = PlanterComp(plant.Position, plant.Map, out Building planter);
            if (comp == null) return factor;
            float growth = planter.GetStatValue(EntadStatDefOf.Entad_PlantGrowthFactor, true, 250);
            if (growth == 1f) return factor;
            if (comp.HasHidden && planter.Faction == Faction.OfPlayer && comp.TicksSinceSpawned >= NoticeTicks)
                comp.RevealStat(EntadStatDefOf.Entad_PlantGrowthFactor);
            return factor * growth;
        }
    }

    [HarmonyPatch(typeof(WealthWatcher), nameof(WealthWatcher.ForceRecount))]
    public static class Patch_WealthWatcher_ForceRecount_Humble
    {
        public static void Postfix(bool allowDuringInit, Map ___map, ref float ___wealthBuildings, ref float ___wealthItems)
        {
            // Vanilla refused the recount and left the old totals, already reduced
            if (!allowDuringInit && Current.ProgramState != ProgramState.Playing) return;
            EntadFurnitureEffects.ReduceWealth(___map, ref ___wealthBuildings, ref ___wealthItems);
        }
    }

    // The rot step's GenTemperature.RotRateAtTemperature result is passed through RotRate. The inspect pane's call
    // only picks its label (through RotRate a warm Preserving shelf would read 'refrigerated', giving away a hidden
    // trait), and TicksUntilRotAtTemp also feeds caravan estimates for food that is about to leave the shelf, so
    // the readout is corrected in the getter below instead.
    [HarmonyPatch(typeof(CompRottable), "TickInterval")]
    public static class Patch_CompRottable_RotRate_Preserving
    {
        private static readonly MethodInfo rotRateAt = AccessTools.Method(typeof(GenTemperature), nameof(GenTemperature.RotRateAtTemperature));
        private static readonly MethodInfo rotRate = AccessTools.Method(typeof(EntadFurnitureEffects), nameof(EntadFurnitureEffects.RotRate));

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            bool found = false;
            foreach (var ins in instructions)
            {
                yield return ins;
                if (ins.Calls(rotRateAt))
                {
                    found = true;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, rotRate);
                }
            }
            if (!found) Log.Warning($"[Entad Framework] {original.DeclaringType.Name}.{original.Name} no longer calls RotRateAtTemperature; Preserving won't change it");
        }
    }

    [HarmonyPatch(typeof(CompRottable), nameof(CompRottable.TicksUntilRotAtCurrentTemp), MethodType.Getter)]
    public static class Patch_CompRottable_TicksUntilRot_Preserving
    {
        public static void Postfix(CompRottable __instance, ref int __result)
        {
            __result = EntadFurnitureEffects.TicksUntilRot(__result, __instance);
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

    [HarmonyPatch(typeof(Plant), nameof(Plant.GrowthRateFactor_Fertility), MethodType.Getter)]
    public static class Patch_Plant_GrowthRateFactorFertility_Barren
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            __result = EntadFurnitureEffects.PlantGrowth(__result, __instance);
        }
    }
}
