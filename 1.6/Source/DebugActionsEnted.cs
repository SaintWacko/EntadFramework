using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public static class DebugActionsEntad
    {
        [DebugAction(
            category = "Entad Framework",
            name = "Apply Random Entad",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap
        )]
        private static void ApplyRandomEntad()
        {
            IntVec3 cell = UI.MouseCell();
            Map map = Find.CurrentMap;

            if (map == null || !cell.InBounds(map)) return;

            // Find first thing at cursor location with CompEntad attached
            List<Thing> thingList = cell.GetThingList(map);
            Thing targetThing = thingList.Find(t => t.TryGetComp<CompEntad>() != null);

            if (targetThing == null)
            {
                Messages.Message("No valid Entad-compatible item found at clicked cell.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var applied = EntadApi.ApplyRandomModifiers(targetThing);
            if (applied.Count == 0)
            {
                Messages.Message("No applicable EntadModifierDefs for this item!", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message($"Applied '{applied[0].LabelCap}' to {targetThing.Label}!", MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction(
            category = "Entad Framework",
            name = "Generate Random Entad Item",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap
        )]
        private static void GenerateRandomEntadItem()
        {
            IntVec3 cell = UI.MouseCell();
            Map map = Find.CurrentMap;
            if (map == null || !cell.InBounds(map)) return;

            Thing thing = EntadApi.GenerateEntadItem();
            if (thing == null)
            {
                Messages.Message("Could not generate an entad item.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near);
            Messages.Message($"Generated {thing.LabelCap}.", thing, MessageTypeDefOf.PositiveEvent, false);
        }

        private static CompEntad EntadAtMouse(out Thing thing)
        {
            thing = null;
            Map map = Find.CurrentMap;
            IntVec3 cell = UI.MouseCell();
            if (map == null || !cell.InBounds(map)) return null;
            thing = cell.GetThingList(map).Find(t => t.TryGetComp<CompEntad>() != null);
            if (thing == null) Messages.Message("No valid Entad-compatible item found at clicked cell.", MessageTypeDefOf.RejectInput, false);
            return thing?.TryGetComp<CompEntad>();
        }

        [DebugAction(category = "Entad Framework", name = "Add specific Entad modifier", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AddSpecificEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;

            var options = new List<DebugMenuOption>();
            foreach (var def in DefDatabase<EntadModifierDef>.AllDefsListForReading.OrderBy(d => d.defName))
            {
                var d = def;
                bool ok = d.CanApplyTo(thing);
                options.Add(new DebugMenuOption(d.defName + (ok ? "" : " (not applicable)"), DebugMenuOptionMode.Action, () =>
                {
                    comp.AddModifier(d);
                    Messages.Message($"Applied '{d.LabelCap}' to {thing.Label}.", MessageTypeDefOf.PositiveEvent, false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        [DebugAction(category = "Entad Framework", name = "Remove specific Entad modifier", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveSpecificEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            if (comp.activeModifiers.Count == 0)
            {
                Messages.Message("That item has no entad modifiers.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var options = new List<DebugMenuOption>();
            foreach (var applied in comp.activeModifiers.ToList())
            {
                var a = applied;
                options.Add(new DebugMenuOption(a.def.defName, DebugMenuOptionMode.Action, () =>
                {
                    comp.RemoveModifier(a);
                    Messages.Message($"Removed '{a.def.LabelCap}' from {thing.Label}.", MessageTypeDefOf.NeutralEvent, false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        [DebugAction(category = "Entad Framework", name = "Remove all Entad modifiers", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            int n = comp.activeModifiers.Count;
            comp.ClearModifiers();
            Messages.Message($"Removed {n} modifier(s) from {thing.Label}.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(category = "Entad Framework", name = "Reveal all Entad modifiers", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RevealAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            foreach (var m in comp.activeModifiers.ToList()) m.Reveal(EntadEffectKind.All);
        }

        [DebugAction(category = "Entad Framework", name = "Hide all Entad modifiers", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void HideAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            foreach (var m in comp.activeModifiers.ToList()) m.Hide();
        }
    }
}
