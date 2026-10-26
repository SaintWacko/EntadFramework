using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // The <powerNet> block on a trait: effects that cover the whole power network its building is on rather than the
    // building itself. Conserving, Leaky and Capacitive are plain stats instead (Entad_NetPowerFactor,
    // Entad_NetPowerLeak, Entad_NetBatteryLossFactor), so they roll and scale like any other; what is left here are
    // the on/off effects. Read in EntadPowerNets.
    public class EntadPowerNetEffect
    {
        // Short circuits never strike the network: not the incident, not rain on its buildings, not an arc
        public bool grounded;
        // The network keeps running through solar flares (anything else that disables electricity, too)
        public bool flareProof;
        // Every this many days while the network has power, the building short-circuits the network, as vanilla's
        // incident does at a conduit: stored power explodes at it, or it starts a fire. 0..0 means never.
        public FloatRange arcIntervalDays = FloatRange.Zero;

        public bool Arcs => arcIntervalDays.max > 0f;

        public IEnumerable<string> CardLines()
        {
            if (grounded) yield return "EF_Card_NetGrounded".Translate();
            if (flareProof) yield return "EF_Card_NetFlareProof".Translate();
            if (Arcs) yield return "EF_Card_NetArcs".Translate(arcIntervalDays.min.ToString("0.#"), arcIntervalDays.max.ToString("0.#"));
        }

        public IEnumerable<string> ConfigErrors(string defName)
        {
            if (!grounded && !flareProof && !Arcs) yield return $"{defName}: powerNet does nothing; set grounded, flareProof or arcIntervalDays";
            if (arcIntervalDays.min > arcIntervalDays.max || arcIntervalDays.min < 0f) yield return $"{defName}: invalid powerNet arcIntervalDays range";
            if (Arcs && arcIntervalDays.min < 1f) yield return $"{defName}: powerNet arcIntervalDays below 1 day would arc every few hours";
            if (grounded && Arcs) yield return $"{defName}: a grounded network stops its own arcs; put grounded and arcs on different traits";
        }
    }

    // Network traits on power buildings (batteries, generators, switches, powered furniture: anything with a power
    // comp that IsFurniture accepts, so never a conduit). Each carrier is listed here while it is spawned; the hooks
    // find the carriers on a network by comparing PowerNet references, which stays right however often vanilla
    // rebuilds its nets. There are rarely more than a few carriers, and with none on any map every hook stops at a
    // count check.
    public class EntadPowerNets : GameComponent
    {
        private class Carrier
        {
            public CompEntad comp;
            public CompPower power;
            public float powerFactor = 1f;
            public float leakWatts;
            public float lossFactor = 1f;
            public bool grounded;
            public bool flareProof;
            public AppliedEntadTrait arcTrait;
        }

        private static readonly List<Carrier> carriers = new List<Carrier>();
        private static int gainCount, capacitiveCount, groundedCount, flareProofCount;

        // As EntadFurnitureEffects: the colony notices an effect once it has been running this long
        private const int NoticeTicks = 2500;
        // Grounded acts by nothing happening; with nothing else to give it away, a day on a live network does
        private const int GroundedNoticeTicks = 60000;

        public EntadPowerNets(Game game)
        {
            carriers.Clear();
            Recount();
        }

        private static readonly Dictionary<ThingDef, bool> carrierDefs = new Dictionary<ThingDef, bool>();

        // A building that joins a power network: a power user, generator, battery or switch
        public static bool IsCarrier(ThingDef td)
        {
            if (td.building == null) return false;
            if (!carrierDefs.TryGetValue(td, out bool result))
            {
                result = false;
                for (int i = 0; td.comps != null && i < td.comps.Count; i++)
                    if (td.comps[i].compClass != null && typeof(CompPower).IsAssignableFrom(td.comps[i].compClass)) { result = true; break; }
                carrierDefs[td] = result;
            }
            return result;
        }

        private static AppliedEntadTrait ArcTrait(CompEntad comp)
        {
            for (int i = 0; i < comp.activeTraits.Count; i++)
                if (comp.activeTraits[i].def.powerNet?.Arcs == true) return comp.activeTraits[i];
            return null;
        }

        public static void Refresh(CompEntad comp)
        {
            int index = carriers.FindIndex(c => c.comp == comp);
            var arcTrait = ArcTrait(comp);
            // Kept while minified or carried, so a reinstalled battery picks up where its countdown left off
            if (arcTrait == null) comp.arcTicksLeft = -1;
            else if (comp.arcTicksLeft < 0) comp.arcTicksLeft = RollArcTicks(arcTrait);
            Carrier carrier = null;
            Thing thing = comp.parent;
            if (thing.Spawned && comp.activeTraits.Count > 0 && IsCarrier(thing.def)) carrier = Build(comp, arcTrait);
            if (carrier == null) { if (index >= 0) carriers.RemoveAt(index); }
            else if (index >= 0) carriers[index] = carrier;
            else carriers.Add(carrier);
            Recount();
        }

        private static Carrier Build(CompEntad comp, AppliedEntadTrait arcTrait)
        {
            var thing = (ThingWithComps)comp.parent;
            var carrier = new Carrier { comp = comp, power = thing.GetComp<CompPower>(), arcTrait = arcTrait };
            if (carrier.power == null) return null;
            if (comp.HasStatTrait(EntadStatDefOf.Entad_NetPowerFactor)) carrier.powerFactor = thing.GetStatValue(EntadStatDefOf.Entad_NetPowerFactor);
            if (comp.HasStatTrait(EntadStatDefOf.Entad_NetPowerLeak)) carrier.leakWatts = thing.GetStatValue(EntadStatDefOf.Entad_NetPowerLeak);
            if (comp.HasStatTrait(EntadStatDefOf.Entad_NetBatteryLossFactor)) carrier.lossFactor = thing.GetStatValue(EntadStatDefOf.Entad_NetBatteryLossFactor);
            for (int i = 0; i < comp.activeTraits.Count; i++)
            {
                var effect = comp.activeTraits[i].def.powerNet;
                if (effect == null) continue;
                carrier.grounded |= effect.grounded;
                carrier.flareProof |= effect.flareProof;
            }
            bool any = carrier.powerFactor < 1f || carrier.leakWatts > 0f || carrier.lossFactor < 1f
                || carrier.grounded || carrier.flareProof || carrier.arcTrait != null;
            return any ? carrier : null;
        }

        private static void Recount()
        {
            gainCount = capacitiveCount = groundedCount = flareProofCount = 0;
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (c.powerFactor < 1f || c.leakWatts > 0f) gainCount++;
                if (c.lossFactor < 1f) capacitiveCount++;
                if (c.grounded) groundedCount++;
                if (c.flareProof) flareProofCount++;
            }
        }

        private static int RollArcTicks(AppliedEntadTrait trait) =>
            Mathf.Max(GenDate.TicksPerHour, Mathf.RoundToInt(trait.def.powerNet.arcIntervalDays.RandomInRange * GenDate.TicksPerDay));

        private static bool IsPlayers(Carrier c) => c.comp.parent.Faction == Faction.OfPlayer;

        // Conserving: what the network's running consumers draw, times the strongest factor on it (two Conserving
        // carriers don't stack, or a few rolls would run a base on nothing). Leaky: every leak on the network, while
        // it has something to leak from. Called every tick for every network, and by the power readout.
        internal static float GainRate(PowerNet net, float rate)
        {
            if (gainCount == 0 || DebugSettings.unlimitedPower) return rate;
            float factor = 1f, leak = 0f;
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (c.power.PowerNet != net) continue;
                if (c.powerFactor < factor) factor = c.powerFactor;
                leak += c.leakWatts;
            }
            if (factor < 1f)
            {
                float draw = 0f;
                var comps = net.powerComps;
                for (int i = 0; i < comps.Count; i++)
                    if (comps[i].PowerOn && comps[i].EnergyOutputPerTick < 0f) draw += comps[i].EnergyOutputPerTick;
                rate -= draw * (1f - factor);
            }
            if (leak > 0f && net.hasPowerSource) rate -= leak * CompPower.WattsToWattDaysPerTick;
            return rate;
        }

        // Conserving on one consumer's draw, for vanilla's 'can this one switch on' checks (PowerNetTick, CanPowerNow).
        // GainRate already counts running consumers at the lower draw; without this a consumer that only fits at the
        // lower draw would never come on. Producers are left alone. PowerNetTick's flare shutdown also reads this, to
        // pick consumers (output < 0), which holds only while Entad_NetPowerFactor's minValue stays above 0.
        internal static float ConservedOutput(CompPowerTrader trader)
        {
            float output = trader.EnergyOutputPerTick;
            if (gainCount == 0 || output >= 0f) return output;
            PowerNet net = trader.PowerNet;
            if (net == null) return output;
            float factor = 1f;
            for (int i = 0; i < carriers.Count; i++)
                if (carriers[i].powerFactor < factor && carriers[i].power.PowerNet == net) factor = carriers[i].powerFactor;
            return output * factor;
        }

        // Capacitive: the share of the battery's charging loss left, 1 when no carrier on the network has the trait
        internal static float LossFactor(PowerNet net)
        {
            if (capacitiveCount == 0 || net == null) return 1f;
            float factor = 1f;
            for (int i = 0; i < carriers.Count; i++)
                if (carriers[i].lossFactor < factor && carriers[i].power.PowerNet == net) factor = carriers[i].lossFactor;
            return factor;
        }

        // The factors as the player knows them, for inspect text: only carriers whose trait has been revealed count,
        // so a selected battery or lamp never gives a hidden trait away. Built only when something is selected.
        internal static float ShownFactor(PowerNet net, StatDef stat)
        {
            if (net == null || (stat == EntadStatDefOf.Entad_NetPowerFactor ? gainCount : capacitiveCount) == 0) return 1f;
            float factor = 1f;
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                float f = stat == EntadStatDefOf.Entad_NetPowerFactor ? c.powerFactor : c.lossFactor;
                if (f < factor && c.power.PowerNet == net && StatRevealed(c.comp, stat)) factor = f;
            }
            return factor;
        }

        private static bool StatRevealed(CompEntad comp, StatDef stat)
        {
            for (int t = 0; t < comp.activeTraits.Count; t++)
            {
                var trait = comp.activeTraits[t];
                if (!trait.IsRevealed(EntadEffectKind.Stat)) continue;
                foreach (var r in trait.def.AllRanges()) if (r.stat == stat) return true;
            }
            return false;
        }

        internal static bool IsGrounded(PowerNet net)
        {
            if (groundedCount == 0 || net == null) return false;
            for (int i = 0; i < carriers.Count; i++)
                if (carriers[i].grounded && carriers[i].power.PowerNet == net) return true;
            return false;
        }

        private static void RevealGrounded(PowerNet net)
        {
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (c.grounded && c.power.PowerNet == net && IsPlayers(c)) RevealNetwork(c.comp, e => e.grounded);
            }
        }

        private static void RevealNetwork(CompEntad comp, System.Func<EntadPowerNetEffect, bool> which)
        {
            if (!comp.HasHidden) return;
            comp.RevealWhere(EntadEffectKind.Network, m => m.def.powerNet != null && which(m.def.powerNet));
        }

        // Stormcaught: whether electricity is off for this network. The flare still counts for everything else.
        internal static bool ElectricityDisabled(GameConditionManager manager, Map map, PowerNet net)
        {
            if (!manager.ElectricityDisabled(map)) return false;
            if (flareProofCount == 0 || net == null) return true;
            for (int i = 0; i < carriers.Count; i++)
            {
                var c = carriers[i];
                if (!c.flareProof || c.power.PowerNet != net) continue;
                if (IsPlayers(c)) RevealNetwork(c.comp, e => e.flareProof);
                return false;
            }
            return true;
        }

        // The comms console's own flare check, so a console on a Stormcaught network can still be used
        internal static bool ElectricityDisabled(GameConditionManager manager, Map map, Building building) =>
            ElectricityDisabled(manager, map, building?.PowerComp?.PowerNet);

        // The same from a comp's own check (Ideology's lightballs and loudspeakers, Anomaly's flare-sensitive facilities)
        internal static bool ElectricityDisabled(GameConditionManager manager, Map map, ThingComp comp) =>
            ElectricityDisabled(manager, map, comp?.parent?.GetComp<CompPower>()?.PowerNet);

        public override void GameComponentTick()
        {
            if (carriers.Count == 0 || Find.TickManager.TicksGame % 250 != 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = carriers.Count - 1; i >= 0; i--)
            {
                var c = carriers[i];
                Thing thing = c.comp.parent;
                // An abandoned map's buildings are never despawned one by one
                if (thing.Destroyed || !thing.Spawned)
                {
                    carriers.RemoveAt(i);
                    Recount();
                    continue;
                }
                var net = c.power.PowerNet;
                if (net == null || !IsPlayers(c)) continue;
                bool live = net.HasActivePowerSource;
                if (c.arcTrait != null && live)
                {
                    c.comp.arcTicksLeft -= 250;
                    if (c.comp.arcTicksLeft <= 0)
                    {
                        c.comp.arcTicksLeft = RollArcTicks(c.arcTrait);
                        Arc(c, net);
                        // An arc is all this carrier does on this pass: no reveal check on the same tick
                        continue;
                    }
                }
                if (c.comp.HasHidden && c.comp.TicksSinceSpawned >= NoticeTicks) Notice(c, net, live, now);
            }
        }

        // Effects the colony can only see as numbers: the network's power readout (Conserving, Leaky) and its
        // batteries' charge (Capacitive). Grounded is noticed after a day of nothing going wrong.
        private static void Notice(Carrier c, PowerNet net, bool live, int now)
        {
            if (c.powerFactor < 1f && AnyConsumerOn(net)) c.comp.RevealStat(EntadStatDefOf.Entad_NetPowerFactor);
            if (c.leakWatts > 0f && live) c.comp.RevealStat(EntadStatDefOf.Entad_NetPowerLeak);
            if (c.lossFactor < 1f && live && net.batteryComps.Count > 0) c.comp.RevealStat(EntadStatDefOf.Entad_NetBatteryLossFactor);
            if (c.grounded && live)
            {
                // Counted from the first time it was seen on a live player network, not reset when the network goes
                // dark or the building moves; saved, so a reload doesn't restart it
                if (c.comp.groundedSinceTick < 0) c.comp.groundedSinceTick = now;
                else if (now - c.comp.groundedSinceTick >= GroundedNoticeTicks) RevealNetwork(c.comp, e => e.grounded);
            }
        }

        private static bool AnyConsumerOn(PowerNet net)
        {
            for (int i = 0; i < net.powerComps.Count; i++)
                if (net.powerComps[i].PowerOn && net.powerComps[i].EnergyOutputPerTick < 0f) return true;
            return false;
        }

        // Stormcaught and Sparking: vanilla's short circuit, at the carrier instead of a conduit. A grounded
        // network takes the arc harmlessly, and both items give themselves away.
        private static void Arc(Carrier c, PowerNet net)
        {
            var building = c.comp.parent as Building;
            if (building == null) return;
            RevealNetwork(c.comp, e => e.Arcs);
            if (IsGrounded(net))
            {
                RevealGrounded(net);
                Messages.Message("EF_ArcGrounded".Translate(building.LabelShort), building, MessageTypeDefOf.NeutralEvent);
                return;
            }
            ShortCircuitUtility.DoShortCircuit(building);
        }

        // Grounded: vanilla's own test for a rain short circuit, so the trait is only noticed when it stopped one
        // Only true where vanilla would short: anything else goes on to vanilla (and any other mod's prefix)
        internal static bool BlocksRainShortCircuit(Thing thing)
        {
            if (groundedCount == 0) return false;
            var net = (thing as ThingWithComps)?.GetComp<CompPower>()?.PowerNet;
            if (!IsGrounded(net)) return false;
            var trader = thing.TryGetComp<CompPowerTrader>();
            var battery = thing.TryGetComp<CompPowerBattery>();
            if (!((trader != null && trader.PowerOn && trader.Props.shortCircuitInRain) || (battery != null && battery.StoredEnergy > 100f))) return false;
            RevealGrounded(net);
            return true;
        }

        internal static IEnumerable<Building> SkipGrounded(IEnumerable<Building> conduits)
        {
            foreach (var conduit in conduits)
                if (!IsGrounded(conduit.PowerComp?.PowerNet)) yield return conduit;
        }

        internal static bool AnyGrounded => groundedCount > 0;
        internal static bool AnyCapacitive => capacitiveCount > 0;
    }

    [HarmonyPatch(typeof(PowerNet), nameof(PowerNet.CurrentEnergyGainRate))]
    public static class Patch_PowerNet_CurrentEnergyGainRate_Conserving
    {
        public static void Postfix(PowerNet __instance, ref float __result)
        {
            __result = EntadPowerNets.GainRate(__instance, __result);
        }
    }

    // Capacitive: batteries on the network keep the 5 W vanilla takes from each of them every tick
    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.CompTick))]
    public static class Patch_CompPowerBattery_CompTick_Capacitive
    {
        internal static readonly AccessTools.FieldRef<CompPowerBattery, float> storedEnergy =
            AccessTools.FieldRefAccess<CompPowerBattery, float>("storedEnergy");

        public static void Prefix(CompPowerBattery __instance, out float __state)
        {
            __state = EntadPowerNets.AnyCapacitive ? storedEnergy(__instance) : -1f;
        }

        public static void Postfix(CompPowerBattery __instance, float __state)
        {
            if (__state < 0f || storedEnergy(__instance) >= __state) return;
            if (EntadPowerNets.LossFactor(__instance.PowerNet) < 1f) storedEnergy(__instance) = __state;
        }
    }

    // Capacitive: charging keeps part of what the battery's efficiency throws away. The battery still reports
    // what it can take at its own efficiency, so near full the extra is cut off at its capacity.
    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.AddEnergy))]
    public static class Patch_CompPowerBattery_AddEnergy_Capacitive
    {
        public static void Prefix(CompPowerBattery __instance, out float __state)
        {
            __state = EntadPowerNets.AnyCapacitive ? Patch_CompPowerBattery_CompTick_Capacitive.storedEnergy(__instance) : -1f;
        }

        public static void Postfix(CompPowerBattery __instance, float __state)
        {
            if (__state < 0f) return;
            ref float stored = ref Patch_CompPowerBattery_CompTick_Capacitive.storedEnergy(__instance);
            float added = stored - __state;
            float efficiency = __instance.Props.efficiency;
            if (added <= 0f || efficiency <= 0f || efficiency >= 1f) return;
            float factor = EntadPowerNets.LossFactor(__instance.PowerNet);
            if (factor >= 1f) return;
            float better = 1f - (1f - efficiency) * factor;
            stored = Mathf.Min(__instance.Props.storedEnergyMax, stored + added * (better / efficiency - 1f));
        }
    }

    // Capacitive on a battery's own inspect text: vanilla prints its def efficiency and a 5 W self-discharge line.
    // The two segments are rebuilt with vanilla's own keys and format, then swapped, so another mod's additions stay.
    [HarmonyPatch(typeof(CompPowerBattery), nameof(CompPowerBattery.CompInspectStringExtra))]
    public static class Patch_CompPowerBattery_Inspect_Capacitive
    {
        public static void Postfix(CompPowerBattery __instance, ref string __result)
        {
            if (__result == null || !EntadPowerNets.AnyCapacitive) return;
            float factor = EntadPowerNets.ShownFactor(__instance.PowerNet, EntadStatDefOf.Entad_NetBatteryLossFactor);
            if (factor >= 1f) return;
            float efficiency = __instance.Props.efficiency;
            string label = "PowerBatteryEfficiency".Translate() + ": ";
            float better = 1f - (1f - efficiency) * factor;
            __result = __result.Replace(label + (efficiency * 100f).ToString("F0") + "%", label + (better * 100f).ToString("F0") + "%");
            __result = __result.Replace("\n" + "SelfDischarging".Translate() + ": " + 5f.ToString("F0") + " W", "");
        }
    }

    // Conserving on each consumer's 'Power needed' line, which vanilla prints from the def's draw
    [HarmonyPatch(typeof(CompPowerTrader), nameof(CompPowerTrader.CompInspectStringExtra))]
    public static class Patch_CompPowerTrader_Inspect_Conserving
    {
        public static void Postfix(CompPowerTrader __instance, ref string __result)
        {
            if (__result == null || __instance.PowerOutput >= 0f) return;
            float factor = EntadPowerNets.ShownFactor(__instance.PowerNet, EntadStatDefOf.Entad_NetPowerFactor);
            if (factor >= 1f) return;
            string label = "PowerNeeded".Translate() + ": ";
            string vanilla = label + (0f - __instance.PowerOutput).ToString("#####0") + " W";
            __result = __result.Replace(vanilla, label + (0f - __instance.PowerOutput * factor).ToString("#####0") + " W");
            var props = __instance.Props;
            if (props.idlePowerDraw > 0f || props.alwaysDisplayAsUsingPower)
                __result = __result.Replace("PowerActiveNeeded".Translate(props.PowerConsumption.ToString("#####0")),
                    "PowerActiveNeeded".Translate((props.PowerConsumption * factor).ToString("#####0")));
        }
    }

    // Grounded: the short-circuit incident never picks a conduit on a grounded network, and can't fire at all when
    // every live network is grounded
    [HarmonyPatch(typeof(ShortCircuitUtility), nameof(ShortCircuitUtility.GetShortCircuitablePowerConduits))]
    public static class Patch_ShortCircuitUtility_GetConduits_Grounded
    {
        public static void Postfix(ref IEnumerable<Building> __result)
        {
            if (EntadPowerNets.AnyGrounded) __result = EntadPowerNets.SkipGrounded(__result);
        }
    }

    [HarmonyPatch(typeof(ShortCircuitUtility), nameof(ShortCircuitUtility.TryShortCircuitInRain))]
    public static class Patch_ShortCircuitUtility_TryShortCircuitInRain_Grounded
    {
        public static bool Prefix(Thing thing, ref bool __result)
        {
            if (!EntadPowerNets.BlocksRainShortCircuit(thing)) return true;
            __result = false;
            return false;
        }
    }

    // Stormcaught: the network's own flare check goes through EntadPowerNets.ElectricityDisabled with the network
    [HarmonyPatch(typeof(PowerNet), nameof(PowerNet.PowerNetTick))]
    public static class Patch_PowerNet_PowerNetTick_FlareProof
    {
        private static readonly MethodInfo disabled = AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.ElectricityDisabled));
        private static readonly MethodInfo netDisabled = AccessTools.Method(typeof(EntadPowerNets), nameof(EntadPowerNets.ElectricityDisabled),
            new[] { typeof(GameConditionManager), typeof(Map), typeof(PowerNet) });

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            ReplaceFlareCheck(instructions, original, disabled, netDisabled);

        // Swaps manager.ElectricityDisabled(map) for helper(manager, map, this): the manager and map are already on
        // the stack, so only 'this' is pushed before the call
        internal static IEnumerable<CodeInstruction> ReplaceFlareCheck(IEnumerable<CodeInstruction> instructions, MethodBase original, MethodInfo from, MethodInfo to)
        {
            bool found = false;
            foreach (var ins in instructions)
            {
                if (ins.Calls(from))
                {
                    found = true;
                    var self = new CodeInstruction(OpCodes.Ldarg_0);
                    ins.MoveLabelsTo(self);
                    ins.MoveBlocksTo(self);
                    yield return self;
                    yield return new CodeInstruction(OpCodes.Call, to);
                    continue;
                }
                yield return ins;
            }
            if (!found) Log.Warning($"[Entad Framework] {original.DeclaringType.Name}.{original.Name} no longer checks ElectricityDisabled; Stormcaught won't change it");
        }
    }

    // Conserving: vanilla's checks for whether a consumer can be switched on read its full draw
    [HarmonyPatch]
    public static class Patch_PowerNet_SwitchOn_Conserving
    {
        private static readonly MethodInfo output = AccessTools.PropertyGetter(typeof(CompPowerTrader), nameof(CompPowerTrader.EnergyOutputPerTick));
        private static readonly MethodInfo conserved = AccessTools.Method(typeof(EntadPowerNets), nameof(EntadPowerNets.ConservedOutput));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PowerNet), nameof(PowerNet.PowerNetTick));
            yield return AccessTools.Method(typeof(PowerNet), nameof(PowerNet.CanPowerNow));
        }

        // Same stack shape: the trader in, its draw out
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            bool found = false;
            foreach (var ins in instructions)
            {
                if (ins.Calls(output))
                {
                    found = true;
                    ins.opcode = OpCodes.Call;
                    ins.operand = conserved;
                }
                yield return ins;
            }
            if (!found) Log.Warning($"[Entad Framework] PowerNet.{original.Name} no longer reads EnergyOutputPerTick; a consumer on a Conserving network may not switch on at its lower draw");
        }
    }

    [HarmonyPatch]
    public static class Patch_Comp_FlareProof
    {
        private static readonly MethodInfo disabled = AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.ElectricityDisabled));
        private static readonly MethodInfo compDisabled = AccessTools.Method(typeof(EntadPowerNets), nameof(EntadPowerNets.ElectricityDisabled),
            new[] { typeof(GameConditionManager), typeof(Map), typeof(ThingComp) });

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(CompAutoPowered), nameof(CompAutoPowered.AppearsPowered));
            yield return AccessTools.PropertyGetter(typeof(CompFacilityInactiveWhenElectricityDisabled), nameof(CompFacilityInactiveWhenElectricityDisabled.CanBeActive));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            Patch_PowerNet_PowerNetTick_FlareProof.ReplaceFlareCheck(instructions, original, disabled, compDisabled);
    }

    [HarmonyPatch]
    public static class Patch_Building_CommsConsole_FlareProof
    {
        private static readonly MethodInfo disabled = AccessTools.Method(typeof(GameConditionManager), nameof(GameConditionManager.ElectricityDisabled));
        private static readonly MethodInfo buildingDisabled = AccessTools.Method(typeof(EntadPowerNets), nameof(EntadPowerNets.ElectricityDisabled),
            new[] { typeof(GameConditionManager), typeof(Map), typeof(Building) });

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.PropertyGetter(typeof(Building_CommsConsole), nameof(Building_CommsConsole.CanUseCommsNow));
            // Private, so found by name: a null target would stop Harmony applying every patch in the assembly
            var failureReason = AccessTools.Method(typeof(Building_CommsConsole), "GetFailureReason");
            if (failureReason != null) yield return failureReason;
            else Log.Warning("[Entad Framework] Building_CommsConsole.GetFailureReason not found; a console on a Stormcaught network may still say it can't be used in a solar flare");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            Patch_PowerNet_PowerNetTick_FlareProof.ReplaceFlareCheck(instructions, original, disabled, buildingDisabled);
    }
}
