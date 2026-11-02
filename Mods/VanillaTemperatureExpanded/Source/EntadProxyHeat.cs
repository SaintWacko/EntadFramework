using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProxyHeat;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework.VTECompat
{
    // Proxy Heat (Vanilla Temperature Expanded's ProxyHeat.dll) warms or cools the cells around a heat source, outdoors
    // unless its settings say indoors too, through a CompTemperatureSource that its startup adds to every def with a
    // heat pusher. Warm, Cool, Sweltering and Frigid come from a trait on one building, not from its def, so each such
    // building gets a CompTemperatureSource of its own here, the way EntadFurnitureEffects gives Luminous buildings a
    // CompGlower: set up and registered with the map's ProxyHeatManager but never added to the building's comps, so
    // nothing of it is saved and removing VTE leaves nothing in the save. Loaded only while VTE is active
    // (LoadFolders.xml); the main assembly never names a ProxyHeat type.
    //
    // Values follow VTE's own rule for heat pushers (Startup.AutogenerateProxyHeatComps): heat per second x5, at most
    // 50 either way, stopping at the trait's target temperature, radius from the footprint; this module's settings
    // scale the change and widen the radius.
    [StaticConstructorOnStartup]
    public static class EntadProxyHeat
    {
        private const string EnabledKey = "VTE_ProxyHeat";
        private const string StrengthKey = "VTE_ProxyHeatStrength";
        private const string RadiusKey = "VTE_ProxyHeatRadius";

        private const float OutcomePerHeat = 5f;
        private const float MaxOutcome = 50f;
        // VTE's snow melting for heat pushers: depth per tick on cells at 0 °C or warmer
        private const float SnowMeltPerTick = 0.001f;
        private const int TickInterval = 250;
        // Pawns near the building as it is installed or built don't count, so placing it outdoors doesn't identify it
        // on the spot. Also runs after a load (TicksSinceSpawned counts from then).
        private const int GraceTicks = 250;

        private class Source
        {
            public CompEntad comp;
            public CompTemperatureSource temp;
            public CompProperties_TemperatureSource props;
            public Map map;
            public ProxyHeatManager manager;
            public bool heating;
            public float target;
        }

        private static readonly Dictionary<CompEntad, Source> sources = new Dictionary<CompEntad, Source>();
        private static readonly List<Source> gone = new List<Source>();
        // Player-owned sources whose heat trait is still hidden, listed by the 250-tick pass for the 10-tick pawn check
        private static readonly List<Source> watched = new List<Source>();

        // VTE turns a source with a power, fuel or switch comp on and off with it from its tick list. Entad heat
        // needs none of those (indoors it runs unpowered), so ours leave the tick list and are switched on here.
        // Without the field (renamed in an update) they stay on VTE's list and follow its power rule instead.
        private static readonly AccessTools.FieldRef<CompTemperatureSource, bool> activeRef;

        // ProxyHeatMod and its settings class are internal, so the master switch is read by reflection
        private static readonly FieldInfo settingsField;
        private static readonly FieldInfo enableField;

        static EntadProxyHeat()
        {
            try { activeRef = AccessTools.FieldRefAccess<CompTemperatureSource, bool>("active"); }
            catch (Exception e) { Log.Warning($"[Entad Framework] Proxy Heat's CompTemperatureSource.active not found ({e.Message}): entad furniture with a power or fuel comp gives proxy heat only while powered or fuelled."); }
            settingsField = AccessTools.Field(typeof(ProxyHeatManager).Assembly.GetType("ProxyHeat.ProxyHeatMod"), "settings");
            enableField = settingsField == null ? null : AccessTools.Field(settingsField.FieldType, "enableProxyHeat");
            if (enableField == null) Log.Warning("[Entad Framework] Proxy Heat's enableProxyHeat setting not found: entad furniture reveals and melts snow as if proxy heat were on.");
            EntadCompat.Refreshed += Refresh;
            EntadCompat.Tick250 += Tick;
            EntadCompat.Tick10 += CheckPawns;
            EntadCompat.GameStarted += Clear;
            EntadCompat.SelectionOverlays += DrawSelected;
            EntadCompat.RegisterSettings(EntadSettingsTab.General, DrawSettings);
        }

        private static bool Enabled => EntadSettings.ModuleValue(EnabledKey, 1f) > 0.5f;
        private static float Strength => EntadSettings.ModuleValue(StrengthKey, 1f);
        private static float RadiusBonus => EntadSettings.ModuleValue(RadiusKey, 0f);

        private static bool ProxyHeatOn()
        {
            if (enableField == null) return true;
            object settings = settingsField.GetValue(null);
            return settings == null || (bool)enableField.GetValue(settings);
        }

        // Also raised for every building with traits when the settings window closes, which picks up this module's sliders
        private static void Refresh(CompEntad comp)
        {
            ThingWithComps thing = comp.parent;
            sources.TryGetValue(comp, out var source);
            Map map = thing.Spawned ? thing.Map : null;
            float heat = 0f, target = 0f;
            if (Enabled && map != null && thing.def.building != null && comp.activeTraits.Count > 0)
                heat = EntadFurnitureEffects.HeatOf(comp, out target);
            ProxyHeatManager manager = heat != 0f ? map.GetComponent<ProxyHeatManager>() : null;
            if (source != null && (manager == null || source.map != map))
            {
                Remove(source);
                source = null;
            }
            if (manager == null) return;

            bool heating = heat > 0f;
            float outcome = Mathf.Clamp(heat * OutcomePerHeat * Strength, -MaxOutcome, MaxOutcome);
            IntVec2 size = thing.def.Size;
            float radius = (size.x + size.z) / 2f + 0.5f + RadiusBonus;
            if (source == null)
            {
                source = new Source { comp = comp, map = map, manager = manager, props = new CompProperties_TemperatureSource() };
                SetProps(source.props, outcome, heating, target, radius);
                source.temp = new CompTemperatureSource { parent = thing };
                source.temp.Initialize(source.props);
                source.temp.PostSpawnSetup(false);
                if (activeRef != null)
                {
                    manager.compTemperaturesToTick.Remove(source.temp);
                    activeRef(source.temp) = true;
                }
                sources[comp] = source;
            }
            else
            {
                SetProps(source.props, outcome, heating, target, radius);
                source.temp.MarkDirty();
            }
            source.heating = heating;
            source.target = target;
        }

        private static void SetProps(CompProperties_TemperatureSource props, float outcome, bool heating, float target, float radius)
        {
            props.tempOutcome = outcome;
            props.maxTemperature = heating ? target : (float?)null;
            props.minTemperature = heating ? (float?)null : target;
            props.radius = radius;
        }

        private static void Remove(Source source)
        {
            sources.Remove(source.comp);
            watched.Remove(source);
            source.temp.PostDeSpawn(source.map);
            // VTE's own despawn hook may have queued this comp for a recount, which re-registers it if the building is
            // still (or again) spawned. Switched off it adds nothing back; with no outcome it changes nothing even if
            // the field is gone and it does come back.
            if (activeRef != null) activeRef(source.temp) = false;
            source.props.tempOutcome = 0f;
        }

        private static void Tick()
        {
            watched.Clear();
            if (sources.Count == 0) return;
            bool on = ProxyHeatOn();
            foreach (var source in sources.Values)
            {
                Thing thing = source.comp.parent;
                // An abandoned map's buildings are never despawned one by one
                if (thing.Destroyed || !thing.Spawned || thing.Map != source.map)
                {
                    gone.Add(source);
                    continue;
                }
                if (!on || source.temp.AffectedCells.Count == 0) continue;
                if (thing.Faction == Faction.OfPlayer && source.comp.HasHidden && !Revealed(source)) watched.Add(source);
                if (source.heating) MeltSnow(source);
            }
            for (int i = 0; i < gone.Count; i++) Remove(gone[i]);
            gone.Clear();
        }

        // The colony identifies the trait the first time one of its people stands in the changed air outdoors (indoors
        // the room effect reveals it, as without VTE). Only while the air there is short of the trait's target, since
        // VTE changes nothing past it.
        private static void CheckPawns()
        {
            for (int i = watched.Count - 1; i >= 0; i--)
            {
                var source = watched[i];
                Thing thing = source.comp.parent;
                if (!thing.Spawned || thing.Faction != Faction.OfPlayer || source.comp.TicksSinceSpawned < GraceTicks) continue;
                Map map = source.map;
                foreach (IntVec3 cell in source.temp.AffectedCells)
                {
                    if (!cell.UsesOutdoorTemperature(map) || !Feels(map, cell)) continue;
                    float temperature = cell.GetTemperature(map);
                    if (source.heating ? temperature >= source.target : temperature <= source.target) continue;
                    source.comp.RevealStat(source.heating ? EntadStatDefOf.Entad_RoomHeating : EntadStatDefOf.Entad_RoomCooling);
                    watched.RemoveAt(i);
                    break;
                }
            }
        }

        // A colonist or slave: animals and visitors don't tell the colony anything
        private static bool Feels(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
                if (things[i] is Pawn pawn && pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Humanlike && !pawn.Dead) return true;
            return false;
        }

        private static void Clear()
        {
            sources.Clear();
            watched.Clear();
        }

        private static void MeltSnow(Source source)
        {
            Map map = source.map;
            foreach (IntVec3 cell in source.temp.AffectedCells)
            {
                if (cell.GetSnowDepth(map) <= 0f) continue;
                if (source.manager.GetTemperatureOutcomeFor(cell, cell.GetTemperature(map)) >= 0f)
                    map.snowGrid.AddDepth(cell, -SnowMeltPerTick * TickInterval);
            }
        }

        // VTE's own outline of the cells it changes, once the trait doing it is identified
        private static void DrawSelected(CompEntad comp)
        {
            if (sources.TryGetValue(comp, out var source) && Revealed(source)) source.temp.PostDrawExtraSelectionOverlays();
        }

        // Whether a trait doing the heating (or cooling) is identified
        private static bool Revealed(Source source)
        {
            var comp = source.comp;
            StatDef stat = source.heating ? EntadStatDefOf.Entad_RoomHeating : EntadStatDefOf.Entad_RoomCooling;
            for (int t = 0; t < comp.activeTraits.Count; t++)
            {
                var trait = comp.activeTraits[t];
                if (!trait.IsRevealed(EntadEffectKind.Stat)) continue;
                foreach (var range in trait.def.AllRanges())
                    if (range.stat == stat) return true;
            }
            return false;
        }

        private static void DrawSettings(Listing_Standard list)
        {
            EntadMod.Section(list, "EF_VTE_Settings");
            bool enabled = Enabled;
            list.CheckboxLabeled("EF_VTE_ProxyHeat".Translate(), ref enabled, "EF_VTE_ProxyHeat_Tip".Translate());
            EntadSettings.SetModuleValue(EnabledKey, enabled ? 1f : 0f);
            if (!enabled) return;
            float strength = Strength;
            list.Label("EF_VTE_Strength".Translate(strength.ToStringPercent()));
            EntadSettings.SetModuleValue(StrengthKey, Mathf.Round(list.Slider(strength, 0.25f, 3f) * 20f) / 20f);
            EntadMod.Hint(list, "EF_VTE_Strength_Desc".Translate());
            float radius = RadiusBonus;
            list.Label("EF_VTE_Radius".Translate(radius.ToString("0.#")));
            EntadSettings.SetModuleValue(RadiusKey, Mathf.Round(list.Slider(radius, 0f, 5f) * 2f) / 2f);
            EntadMod.Hint(list, "EF_VTE_Radius_Desc".Translate());
        }
    }
}
