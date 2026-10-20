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
    // accuracy penalties and equipped hediffs. Vanilla reads these from CompUniqueWeapon, which an
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

    // (Kill memories moved to the trigger system: killThought becomes a Kill trigger, see EntadTriggers.cs)

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
            // The verb vanilla builds these rows from
            var verb = __instance.Verbs?.FirstOrDefault(v => v.isPrimary);
            if (verb == null || !verb.Ranged) return;
            if (verb == null || !Revealed(comp).Any()) return;
            __result = Rewrite(__result, comp, verb, req.Thing.TryGetComp<CompUniqueWeapon>()?.TraitsListForReading);
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

        // unique: a vanilla unique weapon's own traits (Odyssey), which vanilla had already folded into these rows.
        // The Verb getters apply them first and round, then our postfixes apply the entad factor and round again.
        private static IEnumerable<StatDrawEntry> Rewrite(IEnumerable<StatDrawEntry> rows, CompEntad comp, VerbProperties verb, List<WeaponTraitDef> unique)
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
                if (order == BurstCountOrder && hasCount) yield return BurstCountRow(cat, verb, props, unique);
                else if (order == FireRateOrder && hasSpeed) yield return FireRateRow(cat, verb, props, unique);
                else if (order == StoppingPowerOrder && hasStop)
                {
                    stopSeen = true;
                    var stop = StoppingPowerRow(cat, verb, props, unique);
                    if (stop != null) yield return stop;
                }
                else yield return row;
            }
            // Vanilla leaves the stopping power row out when the base is 0, so a trait may have to add it.
            // (No burst rows to add: burst traits only go on weapons that already fire bursts, see AppliesTo.)
            if (hasStop && !stopSeen && verb.defaultProjectile?.projectile?.damageDef?.harmsHealth == true)
            {
                var row = StoppingPowerRow(cat, verb, props, unique);
                if (row != null) yield return row;
            }
        }

        private static string Explain(string descKey, string baseValue, List<string> uniqueLines,
            IEnumerable<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> lines, string finalValue)
        {
            var sb = new StringBuilder(descKey.Translate());
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("StatsReport_BaseValue".Translate() + ": " + baseValue);
            sb.AppendLine();
            if (uniqueLines.Count > 0)
            {
                sb.AppendLine("StatsReport_WeaponTraits".Translate() + ":");
                foreach (var u in uniqueLines) sb.AppendLine(u);
                sb.AppendLine();
            }
            sb.AppendLine("EF_Stat_EntadTraits".Translate() + ":");
            foreach (var l in lines) sb.AppendLine("    " + l.m.def.LabelCap + ": " + l.w.ValueString(l.v));
            sb.AppendLine();
            sb.Append("StatsReport_FinalValue".Translate() + ": " + finalValue);
            return sb.ToString();
        }

        // Vanilla's tooltip line for a unique trait: "    Name: x150%"
        private static string UniqueLine(WeaponTraitDef t, float v, ToStringStyle style, ToStringNumberSense sense) =>
            "    " + t.LabelCap + ": " + v.ToStringByStyle(style, sense);

        // Same rounding as Verb.BurstShotCount then Patch_Verb_BurstShotCount, so the card matches the shots fired
        private static StatDrawEntry BurstCountRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props, List<WeaponTraitDef> unique)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.BurstShotCount).ToList();
            float u = verb.burstShotCount;
            var uLines = new List<string>();
            if (unique != null)
                foreach (var t in unique)
                    if (!Mathf.Approximately(t.burstShotCountMultiplier, 1f))
                    {
                        u *= t.burstShotCountMultiplier;
                        uLines.Add(UniqueLine(t, t.burstShotCountMultiplier, ToStringStyle.PercentOne, ToStringNumberSense.Factor));
                    }
            float f = 1f;
            foreach (var l in lines) f *= l.v;
            string final = Mathf.Max(1, Mathf.CeilToInt(Mathf.CeilToInt(u) * f)).ToString();
            return new StatDrawEntry(cat, "BurstShotCount".Translate(), final,
                Explain("Stat_Thing_Weapon_BurstShotCount_Desc", verb.burstShotCount.ToString(), uLines, lines, final), BurstCountOrder);
        }

        // Same rounding as Verb.TicksBetweenBurstShots then Patch_Verb_TicksBetweenBurstShots
        private static StatDrawEntry FireRateRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props, List<WeaponTraitDef> unique)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.BurstShotSpeed).ToList();
            float u = verb.ticksBetweenBurstShots;
            var uLines = new List<string>();
            if (unique != null)
                foreach (var t in unique)
                    if (!Mathf.Approximately(t.burstShotSpeedMultiplier, 1f))
                    {
                        u /= t.burstShotSpeedMultiplier;
                        uLines.Add(UniqueLine(t, t.burstShotSpeedMultiplier, ToStringStyle.PercentOne, ToStringNumberSense.Factor));
                    }
            int baseTicks = Mathf.RoundToInt(u);
            float f = 1f;
            foreach (var l in lines) f *= l.v;
            int ticks = f > 0f ? Mathf.Max(1, Mathf.RoundToInt(baseTicks / f)) : baseTicks;
            string Rpm(int t) => (60f / Mathf.Max(1, t).TicksToSeconds()).ToString("0.##") + " rpm";
            string final = Rpm(ticks);
            return new StatDrawEntry(cat, "BurstShotFireRate".Translate(), final,
                Explain("Stat_Thing_Weapon_BurstShotFireRate_Desc", Rpm(verb.ticksBetweenBurstShots), uLines, lines, final), FireRateOrder);
        }

        // Same base as Projectile.Launch: the projectile's value, or its damage type's default when that is 0
        private static StatDrawEntry StoppingPowerRow(StatCategoryDef cat, VerbProperties verb, List<(AppliedEntadTrait m, WeaponPropertyRange w, float v)> props, List<WeaponTraitDef> unique)
        {
            var lines = props.Where(p => p.w.property == EntadWeaponProperty.StoppingPower).ToList();
            var proj = verb.defaultProjectile?.projectile;
            float baseValue = proj?.stoppingPower ?? 0f;
            if (baseValue == 0f && proj?.damageDef != null) baseValue = proj.damageDef.defaultStoppingPower;
            float total = baseValue;
            var uLines = new List<string>();
            if (unique != null)
                foreach (var t in unique)
                    if (!Mathf.Approximately(t.additionalStoppingPower, 0f))
                    {
                        total += t.additionalStoppingPower;
                        uLines.Add(UniqueLine(t, t.additionalStoppingPower, ToStringStyle.FloatOne, ToStringNumberSense.Offset));
                    }
            foreach (var l in lines) total += l.v;
            if (total <= 0f) return null;
            string final = total.ToString("F1");
            return new StatDrawEntry(cat, "StoppingPower".Translate(), final,
                Explain("StoppingPowerExplanation", baseValue.ToString("F1"), uLines, lines, final), StoppingPowerOrder);
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

    [DefOf]
    public static class EntadStatDefOf
    {
        // Melee counterpart of vanilla's RangedWeapon_ArmorPenetrationMultiplier, which only projectiles read
        public static StatDef Entad_MeleeArmorPenetrationMultiplier;
        // Merciless: damage against downed or fleeing enemies (Patch_Pawn_PreApplyDamage_Merciless)
        public static StatDef Entad_MercilessDamageFactor;
        // Tidy: a pawn stat, the chance to pick up filth when vanilla would (Patch_FilthTracker_TryPickupFilth)
        public static StatDef Entad_FilthPickupFactor;
        // Masterwork and Abundant: workbench stats read when a bill's products are made (EntadProduction.cs)
        public static StatDef Entad_ProductQualityChance;
        public static StatDef Entad_ProductDoubleChance;

        static EntadStatDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadStatDefOf)); }
    }

    // Melee armor penetration. Vanilla multiplies a tool's own armor penetration by the weapon's melee damage
    // multiplier and has no penetration stat for melee, so Entad_MeleeArmorPenetrationMultiplier is read here. This
    // overload is the one every melee hit (through the Verb overload), the pawn's melee penetration stat and the
    // weapon's average penetration row on the info card go through; the ThingDef overload is for an abstract def
    // with no item, so no traits. Per call: a def-table lookup, then for an entad a cached flag, and GetStatValue
    // only when one of its traits has the stat.
    [HarmonyPatch(typeof(VerbProperties), nameof(VerbProperties.AdjustedArmorPenetration),
        new[] { typeof(Tool), typeof(Pawn), typeof(Thing), typeof(HediffComp_VerbGiver) })]
    public static class Patch_VerbProperties_AdjustedArmorPenetration
    {
        public static void Postfix(Thing equipment, ref float __result)
        {
            if (equipment == null || __result <= 0f || !EntadCompInjector.MayHaveComp(equipment.def)) return;
            var comp = equipment.TryGetComp<CompEntad>();
            if (comp == null || !comp.HasMeleePenetration) return;
            // The stat part applies binding (a bound item outside its bloodline does nothing) and leaves hidden
            // traits out of the item's own info card, as for every other stat. The stat keeps vanilla's permanent
            // cache (EntadStatPartInjector.KeepsImmutableCache), so after the first hit this is a dictionary lookup.
            __result *= equipment.GetStatValue(EntadStatDefOf.Entad_MeleeArmorPenetrationMultiplier);
        }
    }

    // Merciless: more damage to an enemy that is downed or fleeing, from the attacker's primary weapon. A prefix on
    // Pawn.PreApplyDamage changes the amount before armor and the damage worker see it, the same place vanilla
    // applies gene and hediff damage factors. Covers every attack that names the weapon in its DamageInfo: melee
    // hits, bullets, blasts and beams.
    // Cost: runs for every damage to every pawn. A pawn neither downed nor fleeing (nearly every hit) costs two
    // field reads and a few comparisons. Past that, one def-table lookup, the comp's cached flag, and the stat
    // read, which keeps vanilla's permanent cache (EntadStatPartInjector.KeepsImmutableCache).
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PreApplyDamage))]
    public static class Patch_Pawn_PreApplyDamage_Merciless
    {
        public static void Prefix(Pawn __instance, ref DamageInfo dinfo)
        {
            if (dinfo.Weapon == null || dinfo.Amount <= 0f || !(dinfo.Instigator is Pawn attacker)) return;
            if (!__instance.Downed && !IsFleeing(__instance)) return;
            var weapon = attacker.equipment?.Primary;
            if (weapon == null || weapon.def != dinfo.Weapon || !EntadCompInjector.MayHaveComp(weapon.def)) return;
            var comp = weapon.TryGetComp<CompEntad>();
            if (comp == null || !comp.HasMerciless || !__instance.HostileTo(attacker)) return;
            // The stat part applies binding and the info card's hidden-trait rule, as for every other stat
            float factor = weapon.GetStatValue(EntadStatDefOf.Entad_MercilessDamageFactor);
            if (factor != 1f) dinfo.SetAmount(dinfo.Amount * factor);
        }

        // Running away in any of vanilla's ways: a panic flee (raiders whose morale broke get this from
        // LordToil_PanicFlee), a flee job (from danger or a predator), or a lord duty that leaves the map, which
        // includes carrying off loot or a kidnapped colonist. Hostility is checked separately, so a trader's
        // caravan leaving the map doesn't count.
        public static bool IsFleeing(Pawn p)
        {
            if (p.MentalStateDef == MentalStateDefOf.PanicFlee) return true;
            var job = p.CurJobDef;
            if (job != null && (job == JobDefOf.Flee || job == JobDefOf.FleeAndCower)) return true;
            var duty = p.mindState?.duty?.def;
            return duty != null && (duty == DutyDefOf.ExitMapRandom || duty == DutyDefOf.ExitMapBest
                || duty == DutyDefOf.ExitMapBestAndDefendSelf || duty == DutyDefOf.Steal || duty == DutyDefOf.Kidnap);
        }
    }

    // Tidy: vanilla's TryPickupFilth (dirt from the terrain, filth on the floor, rain of ash or acid) runs on one
    // cell move in ten. With Entad_FilthPickupFactor below 1 the pickup only happens at that chance. The stat comes
    // from worn gear through the wearer-stat path (EntadWearerStats); read with a 250-tick cache, so a walking pawn
    // works it out a few times a minute at most.
    // A bool prefix on purpose: skipping the pickup means skipping the whole method. That includes thinning the
    // floor filth the pawn would have picked up, which is right: the dirt stays where it is instead of travelling.
    // No other mod in the corpus patches this private method, so nothing else is silenced.
    [HarmonyPatch(typeof(Pawn_FilthTracker), "TryPickupFilth")]
    public static class Patch_FilthTracker_TryPickupFilth
    {
        public static bool Prefix(Pawn ___pawn)
        {
            if (___pawn?.apparel == null || ___pawn.apparel.WornApparelCount == 0) return true;
            float chance = ___pawn.GetStatValue(EntadStatDefOf.Entad_FilthPickupFactor, true, 250);
            return chance >= 1f || Rand.Chance(chance);
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
