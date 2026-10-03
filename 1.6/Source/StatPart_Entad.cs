using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public class StatPart_Entad : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            var comp = req.HasThing ? req.Thing.TryGetComp<CompEntad>() : null;
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return;

            bool unidentified = false;
            foreach (var m in comp.activeModifiers)
            {
                var def = m.def;
                // Stat effects apply whether or not they've been revealed; only their explanations are hidden
                for (int i = 0; def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat) val += m.OffsetFor(i);
                for (int i = 0; def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat) val *= m.FactorFor(i);
                // Identified modifiers add their normal value; unidentified ones share one fixed bonus (below)
                if (parentStat == StatDefOf.MarketValue && !m.AnyHidden) val += m.MarketValueOffset();
                else if (parentStat == StatDefOf.MarketValue) unidentified = true;
            }
            if (unidentified) val += EntadModifierDef.UnidentifiedMarketValue;
        }

        public override string ExplanationPart(StatRequest req)
        {
            var comp = req.HasThing ? req.Thing.TryGetComp<CompEntad>() : null;
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return null;

            string explanation = "";
            bool unidentified = false;
            foreach (var m in comp.activeModifiers)
            {
                var def = m.def;
                bool known = m.IsRevealed(EntadEffectKind.Stat);
                if (parentStat == StatDefOf.MarketValue)
                {
                    if (m.AnyHidden) unidentified = true;
                    else explanation += $"\n{def.LabelCap} ({def.rarity}): +{m.MarketValueOffset().ToStringMoney()}";
                    continue;
                }
                for (int i = 0; known && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: {(m.OffsetFor(i) >= 0 ? "+" : "")}{m.OffsetFor(i).ToStringByStyle(parentStat.toStringStyle)}";
                for (int i = 0; known && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: x{m.FactorFor(i).ToStringPercent()}";
            }
            if (unidentified) explanation += $"\nUnidentified entad properties: +{EntadModifierDef.UnidentifiedMarketValue.ToStringMoney()}";
            return explanation.NullOrEmpty() ? null : explanation;
        }
    }

    // Dynamically attaches StatPart_Entad to every stat referenced by any EntadModifierDef (and MarketValue)
    [StaticConstructorOnStartup]
    public static class EntadStatPartInjector
    {
        static EntadStatPartInjector()
        {
            new Harmony("saintwacko.entadframework").PatchAll();

            var stats = new HashSet<StatDef> { StatDefOf.MarketValue };
            foreach (var def in DefDatabase<EntadModifierDef>.AllDefsListForReading)
                foreach (var r in def.AllRanges())
                    if (r.stat != null) stats.Add(r.stat);

            foreach (var stat in stats)
            {
                if (stat.parts == null) stat.parts = new List<StatPart>();
                if (stat.parts.Any(p => p is StatPart_Entad)) continue;
                var part = new StatPart_Entad { parentStat = stat };
                stat.parts.Add(part);
            }
        }
    }
}
