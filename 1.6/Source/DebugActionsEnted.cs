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

            CompEntad comp = targetThing.TryGetComp<CompEntad>();

            List<EntadModifierDef> allMods = DefDatabase<EntadModifierDef>.AllDefsListForReading;
            if (allMods.NullOrEmpty())
            {
                Messages.Message("No EntadModifierDefs found in database!", MessageTypeDefOf.RejectInput, false);
                return;
            }

            EntadModifierDef randomMod = allMods.RandomElement();
            if (!comp.HasModifier(randomMod))
            {
                comp.AddModifier(randomMod);
                Messages.Message($"Applied '{randomMod.LabelCap}' to {targetThing.Label}!", MessageTypeDefOf.PositiveEvent, false);
            }
            else
            {
                Messages.Message($"{targetThing.LabelCap} already has '{randomMod.LabelCap}'.", MessageTypeDefOf.RejectInput, false);
            }
        }
    }
}