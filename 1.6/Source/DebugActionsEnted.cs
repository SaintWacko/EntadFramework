using System.Collections.Generic;
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
    }
}