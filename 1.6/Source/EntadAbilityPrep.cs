using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Runs once at startup, after defs load:
    // Gives every trait ability its own copy of the AbilityDef when it needs one: psycasts lose their psyfocus
    // and heat cost (they become plain abilities limited by cooldown/charges), and traits with abilityCharges
    //     or abilityCooldownTicks get that cooldown/charge setup, and abilityCastTicks sets the casting time. Copies are per trait so the same ability can be
    //     limited differently by different traits.
    [StaticConstructorOnStartup]
    public static class EntadAbilityPrep
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        static EntadAbilityPrep()
        {
            foreach (var m in DefDatabase<EntadTraitDef>.AllDefsListForReading) Prepare(m);
        }

        private static bool IsPsycast(AbilityDef a) => a.abilityClass != null && typeof(Psycast).IsAssignableFrom(a.abilityClass);

        private static void Prepare(EntadTraitDef m)
        {
            if (m.abilities.NullOrEmpty()) return;
            bool custom = m.abilityCharges > 0 || m.abilityCooldownTicks > 0 || m.abilityCastTicks > 0;
            for (int i = 0; i < m.abilities.Count; i++)
            {
                AbilityDef src = m.abilities[i];
                if (src == null || !(custom || IsPsycast(src))) continue;
                m.abilities[i] = MakeCopy(m, src);
            }
        }

        private static AbilityDef MakeCopy(EntadTraitDef m, AbilityDef src)
        {
            string name = "Entad_" + m.defName + "_" + src.defName;
            var existing = DefDatabase<AbilityDef>.GetNamedSilentFail(name);
            if (existing != null) return existing;

            var copy = (AbilityDef)Clone.Invoke(src, null);
            copy.defName = name;
            copy.shortHash = 0;
            copy.generated = true;
            if (IsPsycast(src))
            {
                copy.abilityClass = typeof(Ability);
                // Verb_CastPsycast casts its ability to Psycast, which the plain Ability copy is not
                if (copy.verbProperties != null && typeof(Verb_CastPsycast).IsAssignableFrom(copy.verbProperties.verbClass))
                {
                    copy.verbProperties = (VerbProperties)Clone.Invoke(copy.verbProperties, null);
                    copy.verbProperties.verbClass = typeof(Verb_CastAbility);
                }
                if (copy.statBases != null)
                    copy.statBases = copy.statBases.Where(s => s.stat != null && !s.stat.defName.Contains("Psyfocus") && !s.stat.defName.Contains("Entropy")).ToList();
            }
            if (m.abilityCharges > 0)
            {
                copy.charges = m.abilityCharges;
                copy.cooldownPerCharge = true;
            }
            if (m.abilityCooldownTicks > 0) copy.cooldownTicksRange = new IntRange(m.abilityCooldownTicks, m.abilityCooldownTicks);
            if (m.abilityCastTicks > 0 && src.verbProperties != null)
            {
                if (copy.verbProperties == src.verbProperties) copy.verbProperties = (VerbProperties)Clone.Invoke(src.verbProperties, null);
                copy.verbProperties.warmupTime = m.abilityCastTicks / 60f;
            }
            copy.cachedTooltip = null;
            copy.cachedTargets = null;
            if (!copy.iconPath.NullOrEmpty())
                copy.uiIcon = ContentFinder<Texture2D>.Get(copy.iconPath, false) ?? copy.uiIcon;
            DefDatabase<AbilityDef>.Add(copy);
            return copy;
        }
    }
}
