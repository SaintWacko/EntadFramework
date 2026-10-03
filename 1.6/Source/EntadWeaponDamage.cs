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
        public static void Postfix(Projectile __instance, ref DamageDef __result)
        {
            var comp = EntadWeaponDamage.CompOf(Traverse.Create(__instance).Field("equipment").GetValue<Thing>());
            if (comp == null) return;
            DamageDef over = EntadWeaponDamage.TypeOverride(comp);
            if (over != null) __result = over;
        }
    }

    [HarmonyPatch(typeof(Projectile), "Impact")]
    public static class Patch_ProjectileExtraDamage
    {
        public static void Postfix(Projectile __instance, Thing hitThing)
        {
            if (hitThing == null || hitThing.Destroyed) return;
            var trav = Traverse.Create(__instance);
            Thing equipment = trav.Field("equipment").GetValue<Thing>();
            var comp = EntadWeaponDamage.CompOf(equipment);
            if (comp == null) return;

            Thing launcher = trav.Field("launcher").GetValue<Thing>();
            var intended = trav.Field("intendedTarget").GetValue<LocalTargetInfo>();
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
