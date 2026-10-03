using System.Collections.Generic;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public static class EntadEquippedMood
    {
        // Total mood from entad modifiers on everything the pawn wears or wields.
        // Fixed or range-picked thoughts contribute their own mood effect; the thought itself is not applied.
        public static float TotalFor(Pawn pawn, out int sources)
        {
            float total = 0f;
            sources = 0;
            var apparel = pawn.apparel?.WornApparel;
            if (apparel != null)
                for (int i = 0; i < apparel.Count; i++) total += ItemMood(apparel[i], ref sources);
            var equipment = pawn.equipment?.AllEquipmentListForReading;
            if (equipment != null)
                for (int i = 0; i < equipment.Count; i++) total += ItemMood(equipment[i], ref sources);
            return total;
        }

        private static float ItemMood(Thing item, ref int sources)
        {
            var comp = item.TryGetComp<CompEntad>();
            if (comp == null) return 0f;
            float total = 0f;
            var mods = comp.activeModifiers;
            for (int i = 0; i < mods.Count; i++)
            {
                ThoughtDef t = mods[i].thought;
                if (t == null) continue;
                total += EntadModifierDef.MoodEffectOf(t);
                sources++;
            }
            return total;
        }
    }

    // Active whenever the pawn holds at least one entad item with a mood effect; works like the
    // vanilla bonded weapon trait thoughts (e.g. mad wailing), so it never stacks or expires.
    public class ThoughtWorker_EntadEquipped : ThoughtWorker
    {
        public override ThoughtState CurrentStateInternal(Pawn p)
        {
            if (p.needs?.mood == null) return ThoughtState.Inactive;
            EntadEquippedMood.TotalFor(p, out int sources);
            return sources > 0 ? ThoughtState.ActiveAtStage(0) : ThoughtState.Inactive;
        }
    }

    public class Thought_EntadEquipped : Thought_Situational
    {
        public override float MoodOffset()
        {
            return EntadEquippedMood.TotalFor(pawn, out _);
        }
    }
}
