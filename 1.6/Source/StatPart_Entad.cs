using Verse;
using RimWorld;

namespace EntadFramework
{
    public class StatPart_Entad : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            if (!req.HasThing || req.Thing == null) return;

            CompEntad comp = req.Thing.TryGetComp<CompEntad>();
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return;

            foreach (var mod in comp.activeModifiers)
            {
                if (!mod.statOffsets.NullOrEmpty())
                {
                    foreach (var offset in mod.statOffsets)
                    {
                        if (offset.stat == parentStat)
                        {
                            val += offset.value;
                        }
                    }
                }

                if (!mod.statFactors.NullOrEmpty())
                {
                    foreach (var factor in mod.statFactors)
                    {
                        if (factor.stat == parentStat)
                        {
                            val *= factor.value;
                        }
                    }
                }
            }
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (!req.HasThing || req.Thing == null) return null;

            CompEntad comp = req.Thing.TryGetComp<CompEntad>();
            if (comp == null || comp.activeModifiers.NullOrEmpty()) return null;

            string explanation = "";
            foreach (var mod in comp.activeModifiers)
            {
                if (!mod.statOffsets.NullOrEmpty())
                {
                    foreach (var offset in mod.statOffsets)
                    {
                        if (offset.stat == parentStat)
                        {
                            explanation += $"\n{mod.LabelCap}: +{offset.value.ToStringByStyle(parentStat.toStringStyle)}";
                        }
                    }
                }

                if (!mod.statFactors.NullOrEmpty())
                {
                    foreach (var factor in mod.statFactors)
                    {
                        if (factor.stat == parentStat)
                        {
                            explanation += $"\n{mod.LabelCap}: x{factor.value.ToStringPercent()}";
                        }
                    }
                }
            }

            return explanation.NullOrEmpty() ? null : explanation;
        }
    }
}