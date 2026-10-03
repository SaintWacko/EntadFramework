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
            if (comp == null || comp.activeTraits.NullOrEmpty()) return;

            bool unidentified = false;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                // Stat effects apply whether or not they've been revealed; only their explanations are hidden
                for (int i = 0; def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat) val += m.OffsetFor(i);
                for (int i = 0; def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat) val *= m.FactorFor(i);
                // Identified traits add their normal value; unidentified ones share one fixed bonus (below)
                if (parentStat == StatDefOf.MarketValue && !m.AnyHidden) val += m.MarketValueOffset();
                else if (parentStat == StatDefOf.MarketValue) unidentified = true;
            }
            if (unidentified) val += EntadTraitDef.UnidentifiedMarketValue;
        }

        public override string ExplanationPart(StatRequest req)
        {
            var comp = req.HasThing ? req.Thing.TryGetComp<CompEntad>() : null;
            if (comp == null || comp.activeTraits.NullOrEmpty()) return null;

            string explanation = "";
            bool unidentified = false;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                bool known = m.IsRevealed(EntadEffectKind.Stat);
                if (parentStat == StatDefOf.MarketValue)
                {
                    if (m.AnyHidden) unidentified = true;
                    else explanation += $"\n{def.LabelCap} ({m.Rarity}): +{m.MarketValueOffset().ToStringMoney()}";
                    continue;
                }
                for (int i = 0; known && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: {(m.OffsetFor(i) >= 0 ? "+" : "")}{m.OffsetFor(i).ToStringByStyle(parentStat.toStringStyle)}";
                for (int i = 0; known && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: x{m.FactorFor(i).ToStringPercent()}";
            }
            if (unidentified) explanation += $"\nUnidentified entad properties: +{EntadTraitDef.UnidentifiedMarketValue.ToStringMoney()}";
            return explanation.NullOrEmpty() ? null : explanation;
        }
    }

    // Dynamically attaches StatPart_Entad to every stat referenced by any EntadTraitDef (and MarketValue)
    [StaticConstructorOnStartup]
    public static class EntadStatPartInjector
    {
        static EntadStatPartInjector()
        {
            new Harmony("saintwacko.entadframework").PatchAll();

            var stats = new HashSet<StatDef> { StatDefOf.MarketValue };
            foreach (var def in DefDatabase<EntadTraitDef>.AllDefsListForReading)
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
