using System.Collections.Generic;
using System.Linq;
using System.Text;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework.CECompat
{
    // Weapon traits under Combat Extended. CE replaces vanilla's guns, bullets and melee verb with its own classes
    // (Verb_ShootCE, ProjectileCE/BulletCE, Verb_MeleeAttackCE) that don't derive from the ones the main assembly
    // patches, so without this module damage traits, Merciless and Hit triggers on a CE gun rolled and did nothing,
    // and CE melee hits counted as ranged. Each patch here feeds CE's equivalent of a vanilla read site into the same
    // main-assembly code the vanilla patch calls. Loaded only while CE is active (LoadFolders.xml).
    [StaticConstructorOnStartup]
    public static class EntadCombatExtended
    {
        static EntadCombatExtended()
        {
            // CE has no stopping power (projectiles carry none and the card row is culled), so those traits never roll
            EntadCompat.RegisterWeight(def => HasProperty(def, EntadWeaponProperty.StoppingPower) ? 0f : 1f);
            // CE aims with its own stats (sights efficiency, sway, spread) and strips vanilla's accuracy-by-range
            // stats from guns, so a trait on them would show a number nothing reads. Our own accuracy traits are moved
            // onto CE's stats by Patches/EntadTraits_CE.xml; this keeps other mods' traits on the old ones from rolling.
            foreach (var stat in new[] { StatDefOf.AccuracyTouch, StatDefOf.AccuracyShort, StatDefOf.AccuracyMedium, StatDefOf.AccuracyLong })
                EntadCompat.RegisterStat(stat, td => false);
            // A hidden stat no gun lists, so the statBases fallback would reject it. CE multiplies only the verb's own
            // recoil by it (Verb_LaunchProjectileCE.RecoilAmount), so a gun with none has nothing to reduce.
            if (CE_StatDefOf.CE_RangedWeapon_RecoilMultiplier != null)
                EntadCompat.RegisterStat(CE_StatDefOf.CE_RangedWeapon_RecoilMultiplier,
                    td => td.IsRangedWeapon && td.Verbs.Any(v => v is VerbPropertiesCE ce && ce.recoilAmount > 0f));

            // CE fires the burst its own equipment stats say (Verb_LaunchProjectileCE.ShotsPerBurst and its
            // TryCastNextBurstShot transpiler), not Verb.BurstShotCount / TicksBetweenBurstShots
            AddPart(CE_StatDefOf.BurstShotCount, EntadWeaponProperty.BurstShotCount);
            AddPart(CE_StatDefOf.TicksBetweenBurstShots, EntadWeaponProperty.BurstShotSpeed);

            // Last, unlike the other modules: everything above only keeps traits off that CE can never use, or works
            // without a patch, so a CE update that breaks one patch target shouldn't take those with it. Not rethrown
            // either: a failed static constructor would make the weight and stat checks above throw on every roll.
            // PatchAll keeps the patches applied before the one that failed, so those come off too, leaving the CE
            // traits as they were before this module existed rather than half hooked.
            var harmony = new Harmony("SaintWacko.EntadFramework.CombatExtended");
            try { harmony.PatchAll(typeof(EntadCombatExtended).Assembly); }
            catch (System.Exception e)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Error("[Entad Framework] Combat Extended patches failed, so entad weapon effects won't reach CE's bullets and melee: " + e);
            }
        }

        private static bool HasProperty(EntadTraitDef def, EntadWeaponProperty property) =>
            def.weaponProperties != null && def.weaponProperties.Any(w => w.property == property);

        private static void AddPart(StatDef stat, EntadWeaponProperty property)
        {
            if (stat == null) return;
            if (stat.parts == null) stat.parts = new List<StatPart>();
            stat.parts.Add(new StatPart_EntadWeaponProperty { parentStat = stat, property = property });
        }

        // Entad damage type on a CE projectile. CE's own unique weapon traits set damageDefOverride just before
        // launch; ours is set at launch so it wins, as Patch_ProjectileDamageDef wins over vanilla's. Every damage
        // path of the projectile reads DamageDef, the explosion included, and PenetrationAmount picks sharp or blunt
        // penetration from it.
        internal static void SetDamageType(ProjectileCE projectile)
        {
            var comp = EntadWeaponDamage.CompOf(projectile.equipment, projectile.launcher);
            if (comp == null) return;
            DamageDef over = EntadWeaponDamage.TypeOverride(comp);
            if (over != null) projectile.damageDefOverride = over;
        }

        internal static float MeleePenetrationFactor(Thing equipment)
        {
            if (equipment == null || !EntadCompInjector.MayHaveComp(equipment.def)) return 1f;
            var comp = equipment.TryGetComp<CompEntad>();
            if (comp == null || !comp.HasMeleePenetration) return 1f;
            return equipment.GetStatValue(EntadStatDefOf.Entad_MeleeArmorPenetrationMultiplier);
        }
    }

    // Burst count and fire rate for CE guns, applied to CE's own BurstShotCount and TicksBetweenBurstShots stats
    // with the same rounding as Patch_Verb_BurstShotCount / Patch_Verb_TicksBetweenBurstShots. CE truncates both
    // stats to int, so the result is already whole. On the item's own card unrevealed traits stay out.
    public class StatPart_EntadWeaponProperty : StatPart
    {
        public EntadWeaponProperty property;

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (val <= 0f) return;
            float f = Factor(req, null);
            if (f == 1f || f <= 0f) return;
            val = property == EntadWeaponProperty.BurstShotCount
                ? Mathf.Max(1, Mathf.CeilToInt(val * f))
                : Mathf.Max(1, Mathf.RoundToInt(val / f));
        }

        public override string ExplanationPart(StatRequest req)
        {
            var lines = new List<string>();
            Factor(req, lines);
            if (lines.Count == 0) return null;
            var sb = new StringBuilder("EF_Stat_EntadTraits".Translate() + ":");
            foreach (var l in lines) sb.AppendLine().Append("    ").Append(l);
            return sb.ToString();
        }

        private float Factor(StatRequest req, List<string> lines)
        {
            if (!EntadWeaponTraits.AnyWeaponProperty || !req.HasThing || !EntadCompInjector.MayHaveComp(req.Thing.def)) return 1f;
            var comp = req.Thing.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty() || !comp.ActiveForHolder) return 1f;
            bool skipHidden = EntadWearerStats.OnOwnInfoCard(req.Thing);
            float f = 1f;
            foreach (var m in comp.activeTraits)
            {
                var props = m.def.weaponProperties;
                if (props == null || (skipHidden && !m.IsRevealed(EntadEffectKind.Damage))) continue;
                for (int i = 0; i < props.Count; i++)
                {
                    if (props[i].property != property) continue;
                    float v = m.WeaponValueFor(i);
                    f *= v;
                    lines?.Add(m.def.LabelCap + ": " + v.ToStringByStyle(ToStringStyle.PercentZero, ToStringNumberSense.Factor));
                }
            }
            return f;
        }
    }

    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Launch), new[] { typeof(Thing), typeof(Vector2), typeof(Thing) })]
    public static class Patch_ProjectileCE_Launch
    {
        public static void Postfix(ProjectileCE __instance) => EntadCombatExtended.SetDamageType(__instance);
    }

    // Instant projectiles (CE lasers and other isInstant ammo) are ray cast instead of launched
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.RayCast), new[] {
        typeof(Thing), typeof(VerbProperties), typeof(Vector2), typeof(float), typeof(float), typeof(float), typeof(float),
        typeof(float), typeof(float), typeof(Thing), typeof(bool) })]
    public static class Patch_ProjectileCE_RayCast
    {
        public static void Postfix(ProjectileCE __instance) => EntadCombatExtended.SetDamageType(__instance);
    }

    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Throw))]
    public static class Patch_ProjectileCE_Throw
    {
        public static void Postfix(ProjectileCE __instance) => EntadCombatExtended.SetDamageType(__instance);
    }

    // A bullet's hit. CE's DamageInfo names the projectile as the weapon, so the gun is noted for the hit's duration
    // (Merciless and Hit triggers read it through EntadDamageSource). The extra damage follows the whole impact,
    // as Patch_BulletExtraDamage does for vanilla bullets, with the penetration and angle of the main hit taken
    // before it (CE's penetration falls with the bullet's remaining speed). The weapon in the extra's DamageInfo is
    // the gun, as CE's own extra damages have it.
    [HarmonyPatch(typeof(BulletCE), nameof(BulletCE.Impact))]
    public static class Patch_BulletCE_Impact
    {
        public struct State
        {
            public (ThingDef, ThingDef) source;
            public CompEntad comp;
            public DamageInfo template;
        }

        public static void Prefix(BulletCE __instance, Thing hitThing, out State __state)
        {
            __state = default;
            __state.source = EntadDamageSource.BeginImpact(__instance.def, __instance.equipmentDef);
            if (hitThing == null) return;
            var comp = EntadWeaponDamage.CompOf(__instance.equipment, __instance.launcher);
            if (comp == null || !EntadWeaponDamage.HasExtra(comp)) return;
            __state.comp = comp;
            __state.template = new DamageInfo(__instance.DamageDef, 0f, __instance.PenetrationAmount, __instance.ExactRotation.eulerAngles.y,
                __instance.launcher, null, __instance.equipmentDef, DamageInfo.SourceCategory.ThingOrUnknown, __instance.intendedTarget.Thing);
        }

        public static void Postfix(Thing hitThing, State __state)
        {
            if (__state.comp == null || hitThing == null || hitThing.Destroyed) return;
            EntadWeaponDamage.DealExtra(__state.comp, hitThing, __state.template, 1f);
        }

        public static void Finalizer(State __state) => EntadDamageSource.EndImpact(__state.source);
    }

    // Every CE projectile's landing goes through the base Impact (BulletCE calls it last, explosives when they go
    // off), and the blast is spawned inside it by GenExplosionCE: an ExplosionCE is an Explosion that runs vanilla's
    // AffectCell, so the main assembly's explosion patches deal the extra damage once the blast is marked here.
    [HarmonyPatch(typeof(ProjectileCE), nameof(ProjectileCE.Impact))]
    public static class Patch_ProjectileCE_Impact
    {
        public static void Prefix(ProjectileCE __instance, Thing hitThing)
        {
            // The same test ProjectileCE.Impact makes before it detonates
            float radius = (__instance.traitExplosion?.radius ?? 0f) > 0f ? __instance.traitExplosion.radius : __instance.def.projectile.explosionRadius;
            bool blast = (radius > 0f && __instance.DamageDef != null) || __instance.TryGetComp<CompExplosiveCE>() != null;
            if (blast) EntadExplosionDamage.BeginProjectileBlast(__instance.equipment, __instance.launcher);
            else if (hitThing != null) EntadWeaponReveal.Hit(__instance.launcher as Pawn, false);
        }

        public static void Finalizer() => EntadExplosionDamage.EndProjectileBlast();
    }

    // Verb_MeleeAttackCE overrides TryCastShot without calling Verb_MeleeAttack's, so the main assembly's melee
    // marker never ran and every CE melee hit counted as ranged for trigger filters
    [HarmonyPatch(typeof(Verb_MeleeAttackCE), "TryCastShot")]
    public static class Patch_MeleeAttackCE_TryCastShot
    {
        public static void Prefix() => EntadTriggers.BeginMeleeAttack();
        public static void Finalizer() => EntadTriggers.EndMeleeAttack();
    }

    // CE's melee damage infos: the main blow, the tool's extra damages, then a critical stun. The type change goes on
    // the main blow only; when it, or one of our extras, differs from the blow's type between sharp and anything else,
    // the penetration is re-read the way CE's own melee damage override picks it (sharp penetration for a sharp type,
    // doubled on a crit, else blunt).
    // CE also asks for these infos when a defender parries (DoParry hands them to the parrying weapon or shield), so
    // only the call inside ApplyMeleeDamageToTarget, the real hit, gets the traits.
    [HarmonyPatch(typeof(Verb_MeleeAttackCE), "DamageInfosToApply")]
    public static class Patch_MeleeAttackCE_DamageInfos
    {
        private static readonly AccessTools.StructFieldRef<DamageInfo, float> Penetration =
            AccessTools.StructFieldRefAccess<DamageInfo, float>("armorPenetrationInt");

        [System.ThreadStatic] internal static int hitDepth;

        public static void Postfix(Verb_MeleeAttackCE __instance, LocalTargetInfo target, bool isCrit, ref IEnumerable<DamageInfo> __result)
        {
            if (hitDepth <= 0) return;
            var comp = EntadWeaponDamage.CompOf(__instance.EquipmentSource, __instance.CasterPawn);
            if (comp == null) return;
            __result = EntadWeaponDamage.ModifyMelee(__result, comp, target.Thing as Pawn, (info, def) => Retype(__instance, info, def, isCrit));
        }

        private static DamageInfo Retype(Verb_MeleeAttackCE verb, DamageInfo info, DamageDef def, bool isCrit)
        {
            bool wasSharp = info.Def.armorCategory == DamageArmorCategoryDefOf.Sharp;
            bool sharp = def.armorCategory == DamageArmorCategoryDefOf.Sharp;
            info.Def = def;
            if (sharp != wasSharp)
            {
                bool animal = verb.CasterPawn?.def.race?.Animal ?? false;
                Penetration(ref info) = sharp ? verb.ArmorPenetrationSharp * (isCrit && !animal ? 2f : 1f) : verb.ArmorPenetrationBlunt;
            }
            return info;
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttackCE), nameof(Verb_MeleeAttackCE.ApplyMeleeDamageToTarget))]
    public static class Patch_MeleeAttackCE_ApplyDamage
    {
        public static void Prefix() => Patch_MeleeAttackCE_DamageInfos.hitDepth++;

        public static void Finalizer()
        {
            if (Patch_MeleeAttackCE_DamageInfos.hitDepth > 0) Patch_MeleeAttackCE_DamageInfos.hitDepth--;
        }

        public static void Postfix(Verb_MeleeAttackCE __instance, LocalTargetInfo target, DamageWorker.DamageResult __result) =>
            EntadWeaponReveal.MeleeHit(__instance.CasterPawn, target, __result);
    }

    // Melee Piercing. CE builds melee penetration from the tool's sharp and blunt values and never calls the
    // VerbProperties.AdjustedArmorPenetration the main assembly patches for vanilla. Both getters are read for every
    // blow (and by CE's melee readout), so the factor costs a def-table lookup and a cached flag on most weapons.
    [HarmonyPatch(typeof(Verb_MeleeAttackCE), nameof(Verb_MeleeAttackCE.ArmorPenetrationSharp), MethodType.Getter)]
    public static class Patch_MeleeAttackCE_PenetrationSharp
    {
        public static void Postfix(Verb_MeleeAttackCE __instance, ref float __result) =>
            __result *= EntadCombatExtended.MeleePenetrationFactor(__instance.EquipmentSource);
    }

    [HarmonyPatch(typeof(Verb_MeleeAttackCE), nameof(Verb_MeleeAttackCE.ArmorPenetrationBlunt), MethodType.Getter)]
    public static class Patch_MeleeAttackCE_PenetrationBlunt
    {
        public static void Postfix(Verb_MeleeAttackCE __instance, ref float __result) =>
            __result *= EntadCombatExtended.MeleePenetrationFactor(__instance.EquipmentSource);
    }

    // Stormsight: CE's aim report checks only CompUniqueWeapon.IgnoreAccuracyMaluses before adding weather and smoke,
    // so vanilla's ShotReport patch never reaches a CE gun. Mortars override this with their own report and are left as
    // CE has them.
    [HarmonyPatch(typeof(Verb_LaunchProjectileCE), nameof(Verb_LaunchProjectileCE.ShiftVecReportFor), new[] { typeof(LocalTargetInfo), typeof(IntVec3) })]
    public static class Patch_LaunchProjectileCE_ShiftVecReport
    {
        public static void Postfix(Verb_LaunchProjectileCE __instance, ShiftVecReport __result)
        {
            if (__result == null || !EntadWeaponTraits.IgnoresAccuracyMaluses(__instance)) return;
            __result.weatherShift = 0f;
            __result.smokeDensity = 0f;
        }
    }
}
