using System.Collections.Generic;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public class CompEntad : ThingComp
    {
        // Stores active modifiers rolled for this specific item instance
        public List<EntadModifierDef> activeModifiers = new List<EntadModifierDef>();

        public CompProperties_Entad Props => (CompProperties_Entad)props;

        // Called when the game saves/loads (handles save state persistence)
        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Collections.Look(ref activeModifiers, "activeModifiers", LookMode.Def);

            // Re-initialize list if loading a clean item
            if (Scribe.mode == LoadSaveMode.PostLoadInit && activeModifiers == null)
            {
                activeModifiers = new List<EntadModifierDef>();
            }
        }

        // Appends custom modifier details to the item's bottom-left inspect box in-game
        public override string CompInspectStringExtra()
        {
            if (activeModifiers.NullOrEmpty()) return null;

            string inspectText = "Entad Modifiers:";
            foreach (var mod in activeModifiers)
            {
                inspectText += $"\n • {mod.LabelCap}: {mod.description}";
            }
            return inspectText;
        }
    }

    // Standard wrapper required by RimWorld to attach a ThingComp via XML
    public class CompProperties_Entad : CompProperties
    {
        public CompProperties_Entad()
        {
            compClass = typeof(CompEntad);
        }
    }
}