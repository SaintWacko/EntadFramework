using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
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

        // Private caches on AbilityDef that the clone would otherwise share with the source ability
        private static readonly AccessTools.FieldRef<AbilityDef, string> CachedTooltip = AccessTools.FieldRefAccess<AbilityDef, string>("cachedTooltip");
        private static readonly AccessTools.FieldRef<AbilityDef, List<string>> CachedTargets = AccessTools.FieldRefAccess<AbilityDef, List<string>>("cachedTargets");

        // ShortHashGiver's own per-type set of taken hashes and its picker, so copies get a hash no other AbilityDef has
        private static readonly AccessTools.FieldRef<Dictionary<System.Type, HashSet<ushort>>> TakenHashes =
            AccessTools.StaticFieldRefAccess<Dictionary<System.Type, HashSet<ushort>>>(AccessTools.Field(typeof(ShortHashGiver), "takenHashesPerDeftype"));
        private static readonly MethodInfo GiveShortHash = AccessTools.Method(typeof(ShortHashGiver), "GiveShortHash");

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
            // Def equality and GetHashCode use defNameHash, which MemberwiseClone copied from the source. Without this
            // the copy counts as equal to the vanilla ability in every HashSet, Dictionary and Contains check.
            copy.ResolveDefNameHash();
            copy.generated = true;
            AssignShortHash(copy);
            // The clone shares these lists with the source by reference. They are only ever replaced below, never
            // mutated: mutating one in place would change the vanilla ability too.
            // comps, modExtensions, descriptionHyperlinks, statBases (until filtered)
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
            CachedTooltip(copy) = null;
            CachedTargets(copy) = null;
            if (!copy.iconPath.NullOrEmpty())
                copy.uiIcon = ContentFinder<Texture2D>.Get(copy.iconPath, false) ?? copy.uiIcon;
            DefDatabase<AbilityDef>.Add(copy);
            return copy;
        }

        private static void AssignShortHash(AbilityDef copy)
        {
            copy.shortHash = 0;
            try
            {
                var all = TakenHashes();
                if (!all.TryGetValue(typeof(AbilityDef), out var taken))
                {
                    taken = new HashSet<ushort>();
                    all[typeof(AbilityDef)] = taken;
                }
                GiveShortHash.Invoke(null, new object[] { copy, typeof(AbilityDef), taken });
            }
            catch (System.Exception e)
            {
                Log.Warning($"[Entad Framework] Could not give {copy.defName} a short hash: {e.Message}");
            }
        }
    }
}
