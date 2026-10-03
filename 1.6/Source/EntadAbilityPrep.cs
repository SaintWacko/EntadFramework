using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Runs once at startup, after defs load:
    //  1. makes an entad modifier for every vanilla (Royalty) psycast that none of your modifiers already uses
    //  2. gives every modifier ability its own copy of the AbilityDef when it needs one: psycasts lose their psyfocus
    //     and heat cost (they become plain abilities limited by cooldown/charges), and modifiers with abilityCharges
    //     or abilityCooldownTicks get that cooldown/charge setup. Copies are per modifier so the same ability can be
    //     limited differently by different modifiers.
    [StaticConstructorOnStartup]
    public static class EntadAbilityPrep
    {
        private static readonly MethodInfo Clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);

        static EntadAbilityPrep()
        {
            GeneratePsycastModifiers();
            foreach (var m in DefDatabase<EntadModifierDef>.AllDefsListForReading) Prepare(m);
        }

        private static bool IsPsycast(AbilityDef a) => a.abilityClass != null && typeof(Psycast).IsAssignableFrom(a.abilityClass);

        private static void GeneratePsycastModifiers()
        {
            var used = new HashSet<AbilityDef>();
            foreach (var m in DefDatabase<EntadModifierDef>.AllDefsListForReading)
                if (m.abilities != null) used.UnionWith(m.abilities);

            foreach (AbilityDef a in DefDatabase<AbilityDef>.AllDefsListForReading.ToList())
            {
                if (!IsPsycast(a) || used.Contains(a)) continue;
                int level = Mathf.Clamp(a.level, 1, 6);
                var rarity = (EntadRarity)Mathf.Min(level - 1, (int)EntadRarity.Legendary);
                var def = new EntadModifierDef
                {
                    defName = "Entad_Psycast_" + a.defName,
                    label = GenText.ToTitleCaseSmart(a.label ?? a.defName),
                    description = a.description,
                    categories = new List<string> { "Weapon", "Apparel", "Furniture" },
                    rarity = rarity,
                    abilities = new List<AbilityDef> { a },
                    abilityCharges = level <= 1 ? 3 : level <= 3 ? 2 : 1,
                    abilityCooldownTicks = GenDate.TicksPerDay * level / 2,
                    generatedFromPsycast = true
                };
                DefDatabase<EntadModifierDef>.Add(def);
            }
        }

        private static void Prepare(EntadModifierDef m)
        {
            if (m.abilities.NullOrEmpty()) return;
            bool custom = m.abilityCharges > 0 || m.abilityCooldownTicks > 0;
            for (int i = 0; i < m.abilities.Count; i++)
            {
                AbilityDef src = m.abilities[i];
                if (src == null || !(custom || IsPsycast(src))) continue;
                m.abilities[i] = MakeCopy(m, src);
            }
        }

        private static AbilityDef MakeCopy(EntadModifierDef m, AbilityDef src)
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
                if (copy.statBases != null)
                    copy.statBases = copy.statBases.Where(s => s.stat != null && !s.stat.defName.Contains("Psyfocus") && !s.stat.defName.Contains("Entropy")).ToList();
            }
            if (m.abilityCharges > 0)
            {
                copy.charges = m.abilityCharges;
                copy.cooldownPerCharge = true;
            }
            if (m.abilityCooldownTicks > 0) copy.cooldownTicksRange = new IntRange(m.abilityCooldownTicks, m.abilityCooldownTicks);
            copy.cachedTooltip = null;
            copy.cachedTargets = null;
            if (!copy.iconPath.NullOrEmpty())
                copy.uiIcon = ContentFinder<Texture2D>.Get(copy.iconPath, false) ?? copy.uiIcon;
            DefDatabase<AbilityDef>.Add(copy);
            return copy;
        }
    }
}
