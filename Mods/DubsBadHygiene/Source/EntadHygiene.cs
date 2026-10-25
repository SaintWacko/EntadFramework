using System.Collections.Generic;
using DubsBadHygiene;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework.HygieneCompat
{
    [DefOf]
    public static class EntadHygieneStatDefOf
    {
        public static StatDef Entad_WaterCapacityFactor;
        public static StatDef Entad_SewageTreatmentFactor;
        public static StatDef Entad_WaterFoulingChance;
        public static StatDef Entad_WaterLossPerDay;

        static EntadHygieneStatDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(EntadHygieneStatDefOf));
    }

    // Entad traits on Dubs Bad Hygiene buildings. Loaded only while DBH is active (LoadFolders.xml), so everything
    // naming a DBH type lives here and the main assembly never does.
    //
    // Effects that cover the plumbing network (Pure, Fouling) are read from the building's plumbing pipe comp, so they
    // follow the building rather than the pipes, which never become entads. Every carrier is listed while spawned, and
    // each hook stops at a count or a dictionary lookup when nothing on any map has the trait: the capacity getters and
    // HasFilter are called every tick for every well, pump and network.
    [StaticConstructorOnStartup]
    public static class EntadHygiene
    {
        public const string PureKey = "DBH_Pure";
        public const string PitKey = "DBH_BottomlessPit";

        // The colony notices an effect once it has been running this long, as EntadFurnitureEffects
        private const int NoticeTicks = 2500;
        private const int TickInterval = 250;

        private class Carrier
        {
            public CompEntad comp;
            public CompPipe pipe;
            public bool pure;
            public float foulingPerDay;
            public CompSepticTank septic;
            public float treatmentFactor = 1f;
            public CompWaterStorage tank;
            public float lossPerDay;
            // Wellspring is applied by the capacity getters (capacityFactors); kept here only to reveal it
            public ThingComp capacityComp;
        }

        private static readonly List<Carrier> carriers = new List<Carrier>();
        private static readonly List<Carrier> pureCarriers = new List<Carrier>();
        private static readonly Dictionary<ThingComp, float> capacityFactors = new Dictionary<ThingComp, float>();
        private static readonly Dictionary<Thing, CompEntad> pits = new Dictionary<Thing, CompEntad>();

        // Set while a Bottomless Pit flushes, so the PushSewage prefix takes the sewage without sending it anywhere
        private static bool pitFlushing;

        static EntadHygiene()
        {
            // First: if a DBH update removed a target this throws, and nothing below registers, so the traits never
            // roll instead of rolling and doing nothing
            new Harmony("SaintWacko.EntadFramework.DubsBadHygiene").PatchAll(typeof(EntadHygiene).Assembly);
            EntadCompat.RegisterEffect(PureKey, IsWaterSupply);
            EntadCompat.RegisterEffect(PitKey, IsFlushToilet);
            EntadCompat.RegisterStat(EntadHygieneStatDefOf.Entad_WaterCapacityFactor, td => HasComp<CompWaterInlet>(td) || HasComp<CompWaterPumpingStation>(td));
            EntadCompat.RegisterStat(EntadHygieneStatDefOf.Entad_SewageTreatmentFactor, td => HasComp<CompSepticTank>(td));
            EntadCompat.RegisterStat(EntadHygieneStatDefOf.Entad_WaterFoulingChance, IsWaterSupply);
            EntadCompat.RegisterStat(EntadHygieneStatDefOf.Entad_WaterLossPerDay, td => HasComp<CompWaterStorage>(td));
            EntadCompat.Refreshed += Refresh;
            EntadCompat.Tick250 += Tick;
            EntadCompat.GameStarted += Clear;
        }

        private static readonly Dictionary<(ThingDef, System.Type), bool> compCache = new Dictionary<(ThingDef, System.Type), bool>();

        private static bool HasComp<T>(ThingDef td) where T : ThingComp
        {
            if (td.building == null || td.comps == null) return false;
            var key = (td, typeof(T));
            if (!compCache.TryGetValue(key, out bool result))
            {
                result = false;
                for (int i = 0; i < td.comps.Count; i++)
                    if (td.comps[i].compClass != null && typeof(T).IsAssignableFrom(td.comps[i].compClass)) { result = true; break; }
                compCache[key] = result;
            }
            return result;
        }

        // Where the network's water comes from or is kept: wells, pumping stations, water towers
        private static bool IsWaterSupply(ThingDef td) =>
            HasComp<CompWaterInlet>(td) || HasComp<CompWaterPumpingStation>(td) || HasComp<CompWaterStorage>(td);

        // Flush toilets (the advanced one derives from it) and latrines. Spacer toilets make no sewage to begin with.
        private static bool IsFlushToilet(ThingDef td) =>
            td.thingClass != null && (typeof(Building_toilet).IsAssignableFrom(td.thingClass) || typeof(Building_Latrine).IsAssignableFrom(td.thingClass));

        private static void Clear()
        {
            carriers.Clear();
            pureCarriers.Clear();
            capacityFactors.Clear();
            pits.Clear();
        }

        private static void Refresh(CompEntad comp)
        {
            Remove(comp);
            var thing = comp.parent;
            if (!thing.Spawned || thing.def.building == null || comp.activeTraits.Count == 0) return;
            var carrier = new Carrier { comp = comp, pipe = PlumbingPipe(thing) };
            bool any = false;
            if (comp.HasCustomEffect(PureKey)) { carrier.pure = true; any = true; }
            if (comp.HasCustomEffect(PitKey) && IsFlushToilet(thing.def)) { pits[thing] = comp; any = true; }
            if (comp.HasStatTrait(EntadHygieneStatDefOf.Entad_WaterCapacityFactor))
            {
                float factor = thing.GetStatValue(EntadHygieneStatDefOf.Entad_WaterCapacityFactor);
                carrier.capacityComp = (ThingComp)thing.GetComp<CompWaterInlet>() ?? thing.GetComp<CompWaterPumpingStation>();
                if (carrier.capacityComp != null && factor != 1f) { capacityFactors[carrier.capacityComp] = factor; any = true; }
                else carrier.capacityComp = null;
            }
            if (comp.HasStatTrait(EntadHygieneStatDefOf.Entad_SewageTreatmentFactor))
            {
                carrier.septic = thing.GetComp<CompSepticTank>();
                carrier.treatmentFactor = thing.GetStatValue(EntadHygieneStatDefOf.Entad_SewageTreatmentFactor);
                any |= carrier.septic != null && carrier.treatmentFactor != 1f;
            }
            if (comp.HasStatTrait(EntadHygieneStatDefOf.Entad_WaterFoulingChance))
            {
                carrier.foulingPerDay = thing.GetStatValue(EntadHygieneStatDefOf.Entad_WaterFoulingChance);
                any |= carrier.foulingPerDay > 0f;
            }
            if (comp.HasStatTrait(EntadHygieneStatDefOf.Entad_WaterLossPerDay))
            {
                carrier.tank = thing.GetComp<CompWaterStorage>();
                carrier.lossPerDay = thing.GetStatValue(EntadHygieneStatDefOf.Entad_WaterLossPerDay);
                any |= carrier.tank != null && carrier.lossPerDay > 0f;
            }
            if (!any) return;
            carriers.Add(carrier);
            if (carrier.pure && carrier.pipe != null) pureCarriers.Add(carrier);
        }

        private static void Remove(CompEntad comp)
        {
            for (int i = carriers.Count - 1; i >= 0; i--)
            {
                if (carriers[i].comp != comp) continue;
                if (carriers[i].capacityComp != null) capacityFactors.Remove(carriers[i].capacityComp);
                pureCarriers.Remove(carriers[i]);
                carriers.RemoveAt(i);
            }
            pits.Remove(comp.parent);
        }

        // The plumbing (water and sewage) pipe; DBH's air and heating pipes are other nets
        private static CompPipe PlumbingPipe(Thing thing)
        {
            if (!(thing is ThingWithComps twc)) return null;
            var comps = twc.AllComps;
            for (int i = 0; i < comps.Count; i++)
                if (comps[i] is CompPipe pipe && pipe.mode == PipeType.Sewage) return pipe;
            return null;
        }

        // pipeNetRef rather than pipeNet: DBH's getter rebuilds every pipe grid on the map when the reference is unset
        private static PlumbingNet NetOf(Carrier c) => c.pipe?.pipeNetRef;

        internal static bool IsPure(PlumbingNet net)
        {
            for (int i = 0; i < pureCarriers.Count; i++)
                if (pureCarriers[i].pipe.pipeNetRef == net) return true;
            return false;
        }

        internal static float CapacityFactor(ThingComp comp) =>
            capacityFactors.Count > 0 && capacityFactors.TryGetValue(comp, out float factor) ? factor : 1f;

        internal static bool AnyPits => pits.Count > 0;

        internal static bool BeginFlush(Thing toilet)
        {
            if (!pits.TryGetValue(toilet, out var comp)) return false;
            if (toilet.Faction == Faction.OfPlayer) comp.RevealCustomEffect(PitKey);
            pitFlushing = true;
            return true;
        }

        internal static void EndFlush() => pitFlushing = false;

        internal static bool PitFlushing => pitFlushing;

        private static bool RealFilter(PlumbingNet net)
        {
            for (int i = 0; i < net.Filters.Count; i++)
                if (net.Filters[i].WorkingNow) return true;
            return false;
        }

        private static void Tick()
        {
            if (carriers.Count == 0) return;
            for (int i = carriers.Count - 1; i >= 0; i--)
            {
                var c = carriers[i];
                Thing thing = c.comp.parent;
                // An abandoned map's buildings are never despawned one by one
                if (thing.Destroyed || !thing.Spawned)
                {
                    Remove(c.comp);
                    continue;
                }
                bool players = thing.Faction == Faction.OfPlayer;
                bool noticed = players && c.comp.HasHidden && c.comp.TicksSinceSpawned >= NoticeTicks;
                var net = NetOf(c);

                // Pure acts the moment its network has water to treat; with a real filter there, nobody could tell
                if (c.pure && noticed && net != null && net.WaterTowers.Count > 0 && !RealFilter(net))
                    c.comp.RevealCustomEffect(PureKey);

                if (c.capacityComp != null && noticed && net != null && net.WaterTowers.Count > 0)
                    c.comp.RevealStat(EntadHygieneStatDefOf.Entad_WaterCapacityFactor);

                // DBH treats TreatedTicksPerDay every tick while working; the rest of the factor goes here
                if (c.septic != null && c.septic.WorkingNow && c.septic.sewageBuffer > 0f)
                {
                    float extra = c.septic.TreatedTicksPerDay * (c.treatmentFactor - 1f) * TickInterval;
                    c.septic.sewageBuffer = UnityEngine.Mathf.Max(0f, c.septic.sewageBuffer - extra);
                    if (noticed) c.comp.RevealStat(EntadHygieneStatDefOf.Entad_SewageTreatmentFactor);
                }

                if (c.tank != null && c.tank.WaterStorage > 0f)
                {
                    float loss = c.tank.Props.WaterStorageCap * c.lossPerDay * TickInterval / GenDate.TicksPerDay;
                    c.tank.WaterStorage = UnityEngine.Mathf.Max(0f, c.tank.WaterStorage - loss);
                    if (noticed) c.comp.RevealStat(EntadHygieneStatDefOf.Entad_WaterLossPerDay);
                }

                if (c.foulingPerDay > 0f && net != null && Rand.Chance(c.foulingPerDay * TickInterval / GenDate.TicksPerDay))
                    Foul(c, net, players);
            }
        }

        // As a polluted well does (CompWaterInlet.CompTick): one clean tower turns contaminated, unless the network
        // filters its water (a Pure building on it counts, through the HasFilter patch)
        private static void Foul(Carrier c, PlumbingNet net, bool players)
        {
            if (net.HasFilter) return;
            CompWaterStorage target = null;
            int seen = 0;
            for (int i = 0; i < net.WaterTowers.Count; i++)
            {
                var tower = net.WaterTowers[i];
                if (tower.WaterQuality == ContaminationLevel.Contaminated) continue;
                if (Rand.Range(0, ++seen) == 0) target = tower;
            }
            if (target == null) return;
            target.WaterQuality = ContaminationLevel.Contaminated;
            if (players) c.comp.RevealStat(EntadHygieneStatDefOf.Entad_WaterFoulingChance);
        }
    }

    // Pure: the network counts as filtered everywhere DBH asks (towers turning treated, polluted wells, the
    // contamination incident)
    [HarmonyPatch(typeof(PlumbingNet), nameof(PlumbingNet.HasFilter), MethodType.Getter)]
    public static class Patch_PlumbingNet_HasFilter
    {
        public static void Postfix(PlumbingNet __instance, ref bool __result)
        {
            if (!__result && EntadHygiene.IsPure(__instance)) __result = true;
        }
    }

    // Wellspring on a well. The cryo well overrides the getter without calling the base, so it is patched as well.
    [HarmonyPatch(typeof(CompWaterInlet), nameof(CompWaterInlet.GetGroundWaterCapacity), MethodType.Getter)]
    public static class Patch_CompWaterInlet_GetGroundWaterCapacity
    {
        public static void Postfix(CompWaterInlet __instance, ref float __result) => __result *= EntadHygiene.CapacityFactor(__instance);
    }

    [HarmonyPatch(typeof(CompWaterInletCryo), nameof(CompWaterInletCryo.GetGroundWaterCapacity), MethodType.Getter)]
    public static class Patch_CompWaterInletCryo_GetGroundWaterCapacity
    {
        public static void Postfix(CompWaterInletCryo __instance, ref float __result) => __result *= EntadHygiene.CapacityFactor(__instance);
    }

    // Wellspring on a pumping station; the wind pump overrides the getter without calling the base
    [HarmonyPatch(typeof(CompWaterPumpingStation), nameof(CompWaterPumpingStation.Capacity), MethodType.Getter)]
    public static class Patch_CompWaterPumpingStation_Capacity
    {
        public static void Postfix(CompWaterPumpingStation __instance, ref float __result) => __result *= EntadHygiene.CapacityFactor(__instance);
    }

    [HarmonyPatch(typeof(CompWindPump), nameof(CompWindPump.Capacity), MethodType.Getter)]
    public static class Patch_CompWindPump_Capacity
    {
        public static void Postfix(CompWindPump __instance, ref float __result) => __result *= EntadHygiene.CapacityFactor(__instance);
    }

    // Bottomless Pit: the flush's PushSewage call takes the sewage and reports success without sending it to a tank,
    // so the toilet never backs up. The advanced toilet inherits Building_toilet's flush.
    [HarmonyPatch(typeof(Building_toilet), nameof(Building_toilet.TryUseFlush))]
    public static class Patch_Building_toilet_TryUseFlush
    {
        public static void Prefix(Building_toilet __instance)
        {
            if (EntadHygiene.AnyPits) EntadHygiene.BeginFlush(__instance);
        }

        public static void Finalizer() => EntadHygiene.EndFlush();
    }

    [HarmonyPatch(typeof(Building_Latrine), nameof(Building_Latrine.TryUseFlush))]
    public static class Patch_Building_Latrine_TryUseFlush
    {
        public static void Prefix(Building_Latrine __instance)
        {
            if (EntadHygiene.AnyPits) EntadHygiene.BeginFlush(__instance);
        }

        public static void Finalizer() => EntadHygiene.EndFlush();
    }

    [HarmonyPatch(typeof(PlumbingNet), nameof(PlumbingNet.PushSewage))]
    public static class Patch_PlumbingNet_PushSewage
    {
        public static bool Prefix(ref bool __result)
        {
            if (!EntadHygiene.PitFlushing) return true;
            __result = true;
            return false;
        }
    }
}
