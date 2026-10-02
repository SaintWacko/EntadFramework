using System.Collections.Generic;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public class EntadModifierDef : Def
    {
        public string category;

        // RimWorld's standard StatModifier class parses <li><stat>...</stat><value>...</value></li>
        public List<StatModifier> statOffsets;
        public List<StatModifier> statFactors;

        public string discoveryMessage;
    }
}