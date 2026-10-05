using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Entad damage effects on weapons: a replaced damage type and/or extra damage of other types on each hit.
    // They are applied whether or not they have been revealed; a hit that lands reveals them.
    public static class EntadWeaponDamage
    {
        public static CompEntad CompOf(Thing weapon)
        {
            var comp = weapon?.TryGetComp<CompEntad>();
            return comp == null || comp.activeTraits.Count == 0 ? null : comp;
        }

        public static DamageDef TypeOverride(CompEntad comp)
        {
            foreach (var m in comp.activeTraits)
                if (m.def.changeDamageType != null) return m.def.changeDamageType;
            return null;
        }

        public static void Reveal(CompEntad comp)
        {
            if (comp.HasHidden) comp.RevealWhere(EntadEffectKind.Damage);
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttackDamage), "DamageInfosToApply")]
    public static class Patch_MeleeDamageInfos
    {
        public static void Postfix(Verb_MeleeAttackDamage __instance, ref IEnumerable<DamageInfo> __result)
        {
            var comp = EntadWeaponDamage.CompOf(__instance.EquipmentSource);
            if (comp == null) return;
            __result = Modify(__result, comp);
        }

        private static IEnumerable<DamageInfo> Modify(IEnumerable<DamageInfo> infos, CompEntad comp)
        {
            DamageDef over = EntadWeaponDamage.TypeOverride(comp);
            bool first = true;
            foreach (var original in infos)
            {
                var info = original;
                if (over != null) info.Def = over;
                yield return info;

                // Extra damage rides along with the first (main) blow only
                if (!first) continue;
                first = false;
                foreach (var m in comp.activeTraits)
                {
                    if (m.def.extraDamage == null) continue;
                    for (int i = 0; i < m.def.extraDamage.Count; i++)
                    {
                        var extra = new DamageInfo(original);
                        extra.Def = m.def.extraDamage[i].damageType;
                        extra.SetAmount(m.ExtraDamageFor(i));
                        yield return extra;
                    }
                }
            }
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.DamageDef), MethodType.Getter)]
    public static class Patch_ProjectileDamageDef
    {
        // Read on every hit and by several projectile code paths, so the protected field is injected (no reflection)
        public static void Postfix(Thing ___equipment, ref DamageDef __result)
        {
            var comp = EntadWeaponDamage.CompOf(___equipment);
            if (comp == null) return;
            DamageDef over = EntadWeaponDamage.TypeOverride(comp);
            if (over != null) __result = over;
        }
    }

    // Bullet.Impact calls base.Impact before dealing its own damage, so a postfix on Projectile.Impact alone would
    // land the extra damage before the main hit. Bullets get it after their own Impact; everything else after the base.
    [HarmonyPatch(typeof(Projectile), "Impact")]
    public static class Patch_ProjectileExtraDamage
    {
        public static void Postfix(Projectile __instance, Thing hitThing, Thing ___equipment, Thing ___launcher)
        {
            if (__instance is Bullet) return;
            EntadProjectileDamage.ApplyExtra(__instance, hitThing, ___equipment, ___launcher);
        }
    }

    [HarmonyPatch(typeof(Bullet), "Impact")]
    public static class Patch_BulletExtraDamage
    {
        public static void Postfix(Bullet __instance, Thing hitThing, Thing ___equipment, Thing ___launcher)
        {
            EntadProjectileDamage.ApplyExtra(__instance, hitThing, ___equipment, ___launcher);
        }
    }

    public static class EntadProjectileDamage
    {
        public static void ApplyExtra(Projectile projectile, Thing hitThing, Thing equipment, Thing launcher)
        {
            if (hitThing == null || hitThing.Destroyed) return;
            var comp = EntadWeaponDamage.CompOf(equipment);
            if (comp == null) return;

            var intended = projectile.intendedTarget;
            foreach (var m in comp.activeTraits)
            {
                if (m.def.extraDamage == null) continue;
                for (int i = 0; i < m.def.extraDamage.Count; i++)
                {
                    var info = new DamageInfo(m.def.extraDamage[i].damageType, m.ExtraDamageFor(i), 0f, -1f, launcher, null, equipment.def, DamageInfo.SourceCategory.ThingOrUnknown, intended.Thing);
                    hitThing.TakeDamage(info);
                }
            }
        }
    }
}
