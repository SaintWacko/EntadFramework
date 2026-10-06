using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // Weapon effects modelled on vanilla's unique weapon traits: burst count and speed, stopping power, ignoring
    // accuracy penalties, equipped hediffs and kill memories. Vanilla reads these from CompUniqueWeapon, which an
    // entad doesn't have, so each read site gets a small patch. Every weapon carries the entad comp, so each patch
    // first checks a flag saying whether any loaded trait has the effect at all: with none, a patch is one branch.
    public static class EntadWeaponTraits
    {
        // Def data, fixed once the game has loaded; worked out on the first read, which happens in play
        private static bool? anyWeaponProperty, anyIgnoreMaluses;

        internal static bool AnyWeaponProperty =>
            anyWeaponProperty ?? (anyWeaponProperty = DefDatabase<EntadTraitDef>.AllDefsListForReading.Any(d => !d.weaponProperties.NullOrEmpty())).Value;

        private static bool AnyIgnoreMaluses =>
            anyIgnoreMaluses ?? (anyIgnoreMaluses = DefDatabase<EntadTraitDef>.AllDefsListForReading.Any(d => d.ignoreAccuracyMaluses)).Value;

        // The comp, when the weapon has entad traits that work for this user
        private static CompEntad WeaponComp(Thing weapon, Thing user)
        {
            if (weapon == null || !EntadCompInjector.MayHaveComp(weapon.def)) return null;
            return EntadWeaponDamage.CompOf(weapon, user);
        }

        // Product of the burst factors, or the sum of stopping power offsets, over the weapon's traits
        public static float Combined(Thing weapon, Thing user, EntadWeaponProperty property)
        {
            var comp = AnyWeaponProperty ? WeaponComp(weapon, user) : null;
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
            if (!AnyIgnoreMaluses) return false;
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

    // The info card's burst count, burst fire rate and stopping power rows come from ThingDef.SpecialDisplayStats,
    // which reads the verb props and CompUniqueWeapon only, so an entad showed the base value there (3) next to its
    // own "x128%" row while actually firing 4. This rewrites those three rows to the value the gun really uses,
    // with each revealed trait listed in the tooltip the way vanilla lists unique weapon traits. Hidden traits stay
    // out of the numbers, like everywhere else on the card. Runs only while an info card is being built.
    [HarmonyPatch(typeof(ThingDef), nameof(ThingDef.SpecialDisplayStats))]
    public static class Patch_ThingDef_SpecialDisplayStats_EntadWeapon
    {
        private const int BurstCountOrder = 5391, FireRateOrder = 5395, StoppingPowerOrder = 5402;

        public static void Postfix(ThingDef __instance, StatRequest req, ref IEnumerable<StatDrawEntry> __result)
        {
            if (!EntadWeaponTraits.AnyWeaponProperty || !req.HasThing || !__instance.IsRangedWeapon) return;
            if (!EntadCompInjector.MayHaveComp(__instance)) return;
            var comp = req.Thing.TryGetComp<CompEntad>();
            if (comp == null || !comp.ActiveForHolder) return;
            var verb = __instance.Verbs?.FirstOrDefault(v => v.Ranged);
            if (verb == null || !Revealed(comp).Any()) return;
            __result = Rewrite(__result, comp, verb);
        }

        // (trait, property, rolled value) for every revealed weapon property
        private static IEnumerable<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> Revealed(CompEntad comp)
        {
            foreach (var m in comp.activeTraits)
            {
                if (m.def.weaponProperties == null || !m.IsRevealed(EntadEffectKind.Damage)) continue;
                for (int i = 0; i < m.def.weaponProperties.Count; i++)
                    yield return (m, m.def.weaponProperties[i], m.WeaponValueFor(i));
            }
        }

        private static IEnumerable<StatDrawEntry> Rewrite(IEnumerable<StatDrawEntry> rows, CompEntad comp, VerbProperties verb)
        {
            var cat = StatCategoryDefOf.Weapon_Ranged;
            var props = Revealed(comp).ToList();
            bool hasCount = props.Any(p => p.w.property == EntadWeaponProperty.BurstShotCount);
            bool hasSpeed = props.Any(p => p.w.property == EntadWeaponProperty.BurstShotSpeed);
            bool hasStop = props.Any(p => p.w.property == EntadWeaponProperty.StoppingPower);
            bool stopSeen = false;
            foreach (var row in rows)
            {
                if (row.category != cat || row.stat != null) { yield return row; continue; }
                int order = row.DisplayPriorityWithinCategory;
                if (order == BurstCountOrder && hasCount) yield return BurstCountRow(cat, verb, props);
                else if (order == FireRateOrder && hasSpeed) yield return FireRateRow(cat, verb, props);
                else if (order == StoppingPowerOrder && hasStop)
                {
                    stopSeen = true;
                    var stop = StoppingPowerRow(cat, verb, props);
                    if (stop != null) yield return stop;
                }
                else yield return row;
            }
            // Vanilla leaves the stopping power row out when the base is 0, so a trait may have to add it
            if (hasStop && !stopSeen && verb.defaultProjectile?.projectile != null)
            {
                var row = StoppingPowerRow(cat, verb, props);
                if (row != null) yield return row;
            }
        }

        private static string Explain(string descKey, string baseValue, IEnumerable<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> lines, string finalValue)
        {
            var sb = new StringBuilder(descKey.Translate());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("StatsReport_BaseValue".Translate() + ": " + baseValue);
            sb.AppendLine();
            sb.AppendLine("EF_Stat_EntadTraits".Translate() + ":");
            foreach (var l in lines) sb.AppendLine("    " + l.m.def.LabelCap + ": " + l.w.ValueString(l.v));
            sb.AppendLine();
            sb.Append("StatsReport_FinalValue".Translate() + ": " + finalValue);
            return sb.ToString();
        }

        // Same rounding as Patch_Verb_BurstShotCount, so the card matches the shots fired
        private static StatDrawEntry BurstCountRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.BurstShotCount).ToList();
            float f = 1f;
            foreach (var l in lines) f *= l.v;
            string final = Mathf.Max(1, Mathf.CeilToInt(verb.burstShotCount * f)).ToString();
            return new StatDrawEntry(cat, "BurstShotCount".Translate(), final,
                Explain("Stat_Thing_Weapon_BurstShotCount_Desc", verb.burstShotCount.ToString(), lines, final), BurstCountOrder);
        }

        // Same rounding as Patch_Verb_TicksBetweenBurstShots
        private static StatDrawEntry FireRateRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.BurstShotSpeed).ToList();
            float f = 1f;
            foreach (var l in lines) f *= l.v;
            int ticks = f > 0f ? Mathf.Max(1, Mathf.RoundToInt(verb.ticksBetweenBurstShots / f)) : verb.ticksBetweenBurstShots;
            string Rpm(int t) => (60f / Mathf.Max(1, t).TicksToSeconds()).ToString("0.##") + " rpm";
            string final = Rpm(ticks);
            return new StatDrawEntry(cat, "BurstShotFireRate".Translate(), final,
                Explain("Stat_Thing_Weapon_BurstShotFireRate_Desc", Rpm(verb.ticksBetweenBurstShots), lines, final), FireRateOrder);
        }

        private static StatDrawEntry StoppingPowerRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.StoppingPower).ToList();
            float baseValue = verb.defaultProjectile?.projectile?.stoppingPower ?? 0f;
            float total = baseValue;
            foreach (var l in lines) total += l.v;
            if (total <= 0f) return null;
            string final = total.ToString("F1");
            return new StatDrawEntry(cat, "StoppingPower".Translate(), final,
                Explain("StoppingPowerExplanation", baseValue.ToString("F1"), lines, final), StoppingPowerOrder);
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
            // Only when the weather cap is in force, and only lifting it: other changes to the range are kept
            Map map = attacker?.MapHeld;
            if (map == null || map.weatherManager.CurWeatherMaxRangeCap < 0f || !EntadWeaponTraits.IgnoresAccuracyMaluses(ownerVerb)) return;
            float uncapped = __instance.rangeStat == null ? __instance.range : attacker.GetStatValue(__instance.rangeStat);
            __result = Mathf.Max(__result, uncapped);
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
