using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // Weapon effects modelled on vanilla's unique weapon traits: burst count and speed, stopping power, ignoring
    // accuracy penalties, equipped hediffs and kill memories. Vanilla reads these from CompUniqueWeapon, which an
    // entad doesn't have, so each read site gets a small patch. Every patch first asks EntadCompInjector whether the
    // weapon's def can carry an entad at all, so ordinary weapons pay one array lookup.
    public static class EntadWeaponTraits
    {
        // The comp, when the weapon has a trait with weapon effects that work for this user
        private static CompEntad WeaponComp(Thing weapon, Thing user)
        {
            if (weapon == null || !EntadCompInjector.MayHaveComp(weapon.def)) return null;
            return EntadWeaponDamage.CompOf(weapon, user);
        }

        // Product of the burst factors, or the sum of stopping power offsets, over the weapon's traits
        public static float Combined(Thing weapon, Thing user, EntadWeaponProperty property)
        {
            var comp = WeaponComp(weapon, user);
            bool factor = property != EntadWeaponProperty.StoppingPower;
            float v = factor ? 1f : 0f;
            if (comp == null) return v;
            foreach (var m in comp.activeTraits)
            {
                var props = m.def.weaponProperties;
                if (props == null) continue;
                for (int i = 0; i < props.Count; i++)
                {
                    if (props[i].property != property) continue;
                    if (factor) v *= m.WeaponValueFor(i); else v += m.WeaponValueFor(i);
                }
            }
            return v;
        }

        public static bool IgnoresAccuracyMaluses(Verb verb)
        {
            var comp = WeaponComp(verb?.EquipmentSource, verb?.caster);
            if (comp == null) return false;
            foreach (var m in comp.activeTraits) if (m.def.ignoreAccuracyMaluses) return true;
            return false;
        }

        // Called from EntadAbilities.Grant once the traits are known to work for the pawn
        public static void AddHediffs(Pawn pawn, CompEntad comp)
        {
            if (pawn?.health == null) return;
            foreach (var m in comp.activeTraits)
            {
                if (m.def.equippedHediffs.NullOrEmpty()) continue;
                foreach (var h in m.def.equippedHediffs)
                    if (!pawn.health.hediffSet.HasHediff(h)) pawn.health.AddHediff(h, pawn.health.hediffSet.GetBrain());
                m.Reveal(EntadEffectKind.Hediff);
            }
        }

        // Called from EntadAbilities.Revoke: removes this item's hediffs unless other active gear still gives them
        public static void RemoveHediffs(Pawn pawn, CompEntad comp, IEnumerable<Thing> equipped)
        {
            if (pawn?.health == null) return;
            HashSet<HediffDef> keep = null;
            foreach (var m in comp.activeTraits)
            {
                if (m.def.equippedHediffs.NullOrEmpty()) continue;
                if (keep == null)
                {
                    keep = new HashSet<HediffDef>();
                    foreach (Thing other in equipped)
                    {
                        if (other == comp.parent) continue;
                        var oc = other.TryGetComp<CompEntad>();
                        if (oc == null || !oc.ActiveFor(pawn)) continue;
                        foreach (var om in oc.activeTraits)
                            if (om.def.equippedHediffs != null) keep.UnionWith(om.def.equippedHediffs);
                    }
                }
                foreach (var h in m.def.equippedHediffs)
                {
                    if (keep.Contains(h)) continue;
                    var existing = pawn.health.hediffSet.GetFirstHediffOfDef(h);
                    if (existing != null) pawn.health.RemoveHediff(existing);
                }
            }
        }
    }

    public partial class CompEntad
    {
        // Vanilla calls this on every weapon the killer has equipped, with the killer as the argument
        public override void Notify_KilledPawn(Pawn pawn)
        {
            base.Notify_KilledPawn(pawn);
            if (pawn?.needs?.mood == null || !ActiveFor(pawn)) return;
            foreach (var m in activeTraits)
            {
                if (m.def.killThought == null) continue;
                pawn.needs.mood.thoughts.memories.TryGainMemory(m.def.killThought);
                m.Reveal(EntadEffectKind.Mood);
            }
        }
    }

    // Verb caches both burst values in private fields; the postfixes below run on each read, after the cache
    [HarmonyPatch(typeof(Verb), nameof(Verb.BurstShotCount), MethodType.Getter)]
    public static class Patch_Verb_BurstShotCount
    {
        public static void Postfix(Verb __instance, ref int __result)
        {
            if (__result <= 0) return;
            float f = EntadWeaponTraits.Combined(__instance.EquipmentSource, __instance.caster, EntadWeaponProperty.BurstShotCount);
            if (f != 1f) __result = Mathf.Max(1, Mathf.CeilToInt(__result * f));
        }
    }

    [HarmonyPatch(typeof(Verb), nameof(Verb.TicksBetweenBurstShots), MethodType.Getter)]
    public static class Patch_Verb_TicksBetweenBurstShots
    {
        public static void Postfix(Verb __instance, ref int __result)
        {
            float f = EntadWeaponTraits.Combined(__instance.EquipmentSource, __instance.caster, EntadWeaponProperty.BurstShotSpeed);
            if (f != 1f && f > 0f) __result = Mathf.Max(1, Mathf.RoundToInt(__result / f));
        }
    }

    // Once per projectile fired; vanilla adds unique weapon stopping power at the same place
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch), new[] {
        typeof(Thing), typeof(Vector3), typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(ProjectileHitFlags),
        typeof(bool), typeof(Thing), typeof(ThingDef) })]
    public static class Patch_Projectile_Launch_StoppingPower
    {
        public static void Postfix(Thing launcher, Thing equipment, ref float ___stoppingPower)
        {
            if (equipment == null) return;
            ___stoppingPower += EntadWeaponTraits.Combined(equipment, launcher, EntadWeaponProperty.StoppingPower);
        }
    }

    // The four places vanilla checks IgnoreAccuracyMaluses: weather and smoke in the hit chance, the weather range
    // cap, blind smoke blocking target search, and the weather note on the attack gizmo
    [HarmonyPatch(typeof(ShotReport), nameof(ShotReport.HitReportFor))]
    public static class Patch_ShotReport_IgnoreMaluses
    {
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Weather =
            AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromWeather");
        private static readonly AccessTools.StructFieldRef<ShotReport, float> Gas =
            AccessTools.StructFieldRefAccess<ShotReport, float>("factorFromCoveringGas");

        public static void Postfix(Verb verb, ref ShotReport __result)
        {
            if (!EntadWeaponTraits.IgnoresAccuracyMaluses(verb)) return;
            Weather(ref __result) = 1f;
            Gas(ref __result) = 1f;
        }
    }

    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedRange))]
    public static class Patch_VerbProperties_AdjustedRange_IgnoreMaluses
    {
        public static void Postfix(VerbProperties __instance, Verb ownerVerb, Thing attacker, ref float __result)
        {
            if (attacker == null || !EntadWeaponTraits.IgnoresAccuracyMaluses(ownerVerb)) return;
            __result = __instance.rangeStat == null ? __instance.range : attacker.GetStatValue(__instance.rangeStat);
        }
    }

    // The gas flag is only read for blind smoke line of sight in this method
    [HarmonyPatch(typeof(AttackTargetFinder), nameof(AttackTargetFinder.BestAttackTarget))]
    public static class Patch_BestAttackTarget_IgnoreMaluses
    {
        public static void Prefix(IAttackTargetSearcher searcher, ref TargetScanFlags flags)
        {
            if ((flags & TargetScanFlags.LOSBlockableByGas) == 0) return;
            if (EntadWeaponTraits.IgnoresAccuracyMaluses(searcher?.CurrentEffectiveVerb)) flags &= ~TargetScanFlags.LOSBlockableByGas;
        }
    }

    [HarmonyPatch(typeof(VerbTracker), "CreateVerbTargetCommand")]
    public static class Patch_VerbTracker_WeatherNote
    {
        public static void Postfix(Verb verb, Command_VerbTarget __result)
        {
            if (__result != null && EntadWeaponTraits.IgnoresAccuracyMaluses(verb)) __result.defaultDescPostfix = null;
        }
    }
}
