using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Entad damage effects on weapons: a replaced damage type and/or extra damage of other types on each hit.
    // They are applied whether or not they have been revealed; a hit that lands reveals them.
    public static class EntadWeaponDamage
    {
        // The weapon's entad comp when its traits work for whoever is using it (user: the wielder, or a projectile's
        // launcher). A turret or other non-pawn user counts as active, like an item nobody holds.
        public static CompEntad CompOf(Thing weapon, Thing user)
        {
            var comp = weapon?.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return null;
            return comp.ActiveFor(user as Pawn) ? comp : null;
        }

        public static DamageDef TypeOverride(CompEntad comp)
        {
            foreach (var m in comp.activeTraits)
                if (m.def.changeDamageType != null) return m.def.changeDamageType;
            return null;
        }

        public static bool HasExtra(CompEntad comp)
        {
            foreach (var m in comp.activeTraits)
                if (m.def.extraDamage != null && m.def.extraDamage.Count > 0) return true;
            return false;
        }

        // Every extra damage the comp's traits deal, each scaled by factor, as a copy of template with only the
        // type and amount changed (so armor penetration, angle, instigator and weapon follow the main hit)
        public static void DealExtra(CompEntad comp, Thing target, DamageInfo template, float factor, bool explosion = false)
        {
            foreach (var m in comp.activeTraits)
            {
                if (m.def.extraDamage == null) continue;
                for (int i = 0; i < m.def.extraDamage.Count; i++)
                {
                    // A pawn killed by the main hit or an earlier extra is dead but not destroyed; hitting it again
                    // would add wound tales, memories and records for damage to a corpse
                    if (target.Destroyed || (target is Pawn p && p.Dead)) return;
                    var info = new DamageInfo(template);
                    info.Def = m.def.extraDamage[i].damageType;
                    info.SetAmount(m.ExtraDamageFor(i) * factor);
                    // As DamageWorker.ExplosionDamageThing does for a blast of that type
                    if (explosion && info.Def.explosionAffectOutsidePartsOnly) info.SetBodyRegion(BodyPartHeight.Undefined, BodyPartDepth.Outside);
                    target.TakeDamage(info);
                }
            }
        }

        public static void Reveal(CompEntad comp)
        {
            if (comp.HasHidden) comp.RevealWhere(EntadEffectKind.Damage);
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttackDamage), "DamageInfosToApply")]
    public static class Patch_MeleeDamageInfos
    {
        public static void Postfix(Verb_MeleeAttackDamage __instance, LocalTargetInfo target, ref IEnumerable<DamageInfo> __result)
        {
            var comp = EntadWeaponDamage.CompOf(__instance.EquipmentSource, __instance.CasterPawn);
            if (comp == null) return;
            __result = Modify(__result, comp, target.Thing as Pawn);
        }

        // Lazy: ApplyMeleeDamageToTarget deals each info before asking for the next, so the dead check sees the main blow
        private static IEnumerable<DamageInfo> Modify(IEnumerable<DamageInfo> infos, CompEntad comp, Pawn victim)
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
                        if (victim != null && victim.Dead) yield break;
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
        public static void Postfix(Thing ___equipment, Thing ___launcher, ref DamageDef __result)
        {
            var comp = EntadWeaponDamage.CompOf(___equipment, ___launcher);
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
            var comp = EntadWeaponDamage.CompOf(equipment, launcher);
            if (comp == null) return;

            // Armor penetration and angle as Bullet.Impact gives the main hit; until build 190 the extra had none
            var template = new DamageInfo(projectile.DamageDef, 0f, projectile.ArmorPenetration, projectile.ExactRotation.eulerAngles.y,
                launcher, null, equipment.def, DamageInfo.SourceCategory.ThingOrUnknown, projectile.intendedTarget.Thing);
            EntadWeaponDamage.DealExtra(comp, hitThing, template, 1f);
        }
    }

    // Explosive projectiles (grenades, rockets, shells). Projectile_Explosive.Impact overrides Projectile.Impact
    // without calling it, and the damage is dealt by an Explosion thing that spreads out over several ticks, so the
    // projectile hooks above never saw it until build 190 (the type change already worked: Explode reads DamageDef).
    // The prefix on Explode marks the weapon; the Explosion that GenExplosion.DoExplosion spawns inside Explode is
    // linked to it in a weak table (no cleanup, and an explosion saved mid-blast simply loses the extra); and each
    // AffectCell deals the extra damage to every thing the blast damaged in that cell, with the blast's falloff.
    public static class EntadExplosionDamage
    {
        internal static CompEntad pending;
        internal static readonly ConditionalWeakTable<Explosion, CompEntad> sources = new ConditionalWeakTable<Explosion, CompEntad>();

        public static void Apply(Explosion explosion, CompEntad comp, IntVec3 cell, List<Thing> damaged, int start, List<Thing> ignored)
        {
            // Same falloff as Explosion.GetDamageAmountAt: full at the centre, 20% at the edge
            float factor = explosion.damageFalloff && explosion.radius > 0f
                ? Mathf.Lerp(1f, 0.2f, cell.DistanceTo(explosion.Position) / explosion.radius) : 1f;
            float ap = explosion.GetArmorPenetrationAt(cell);
            // Captured first: extra damage can kill something that explodes, but that is another Explosion's list
            int end = damaged.Count;
            for (int k = start; k < end; k++)
            {
                Thing t = damaged[k];
                // damagedThings also holds ignored things and fires a bomb put out; neither took the blast
                if (t == null || t.Destroyed || t is Fire || (ignored != null && ignored.Contains(t))) continue;
                float angle = t.Position == explosion.Position ? Rand.RangeInclusive(0, 359) : (t.Position - explosion.Position).AngleFlat;
                var template = new DamageInfo(explosion.damType, 0f, ap, angle, explosion.instigator, null, explosion.weapon,
                    DamageInfo.SourceCategory.ThingOrUnknown, explosion.intendedTarget);
                EntadWeaponDamage.DealExtra(comp, t, template, factor, true);
            }
        }
    }

    [HarmonyPatch(typeof(Projectile_Explosive), "Explode")]
    public static class Patch_ExplosiveExplode
    {
        public static void Prefix(Projectile_Explosive __instance, Thing ___equipment, Thing ___launcher)
        {
            // Projectile.Impact never runs for these, so its reveal hook doesn't either
            EntadWeaponReveal.Hit(___launcher as Pawn, false);
            var comp = EntadWeaponDamage.CompOf(___equipment, ___launcher);
            EntadExplosionDamage.pending = comp != null && EntadWeaponDamage.HasExtra(comp) ? comp : null;
        }

        public static void Finalizer() => EntadExplosionDamage.pending = null;
    }

    [HarmonyPatch(typeof(Explosion), nameof(Explosion.SpawnSetup))]
    public static class Patch_ExplosionSpawn
    {
        public static void Postfix(Explosion __instance, bool respawningAfterLoad)
        {
            var comp = EntadExplosionDamage.pending;
            if (comp == null || respawningAfterLoad) return;
            // Only the projectile's own explosion; anything the blast sets off later is not ours
            EntadExplosionDamage.pending = null;
            EntadExplosionDamage.sources.Remove(__instance);
            EntadExplosionDamage.sources.Add(__instance, comp);
        }
    }

    // Runs for every cell of every explosion, so the weapon check (null for most non-weapon blasts) comes before
    // the table lookup. Reading the private damagedThings list before and after picks up exactly the things the
    // damage worker chose to hit, whatever worker it is.
    [HarmonyPatch(typeof(Explosion), "AffectCell")]
    public static class Patch_ExplosionAffectCell
    {
        public static void Prefix(Explosion __instance, List<Thing> ___damagedThings, out int __state)
        {
            __state = -1;
            if (__instance.weapon != null && ___damagedThings != null && EntadExplosionDamage.sources.TryGetValue(__instance, out _))
                __state = ___damagedThings.Count;
        }

        public static void Postfix(Explosion __instance, IntVec3 c, List<Thing> ___damagedThings, List<Thing> ___ignoredThings, int __state)
        {
            if (__state < 0 || ___damagedThings == null || ___damagedThings.Count <= __state) return;
            if (!EntadExplosionDamage.sources.TryGetValue(__instance, out var comp)) return;
            EntadExplosionDamage.Apply(__instance, comp, c, ___damagedThings, __state, ___ignoredThings);
        }
    }

    // Beam weapons (the Biotech beam graser and modded beams) make no projectile: the private ApplyDamage builds a
    // DamageInfo from verbProps.beamDamageDef and hands it to TakeDamage. Until build 190 neither the type change
    // nor the extra damage reached them. The verb props are shared by every gun of that def, so the type is swapped
    // on the DamageInfo just before TakeDamage rather than on the def.
    public static class EntadBeamDamage
    {
        public static DamageInfo Modify(DamageInfo dinfo, Verb_ShootBeam verb)
        {
            var comp = EntadWeaponDamage.CompOf(verb.EquipmentSource, verb.caster);
            if (comp == null) return dinfo;
            DamageDef over = EntadWeaponDamage.TypeOverride(comp);
            if (over != null) dinfo.Def = over;
            return dinfo;
        }
    }

    [HarmonyPatch(typeof(Verb_ShootBeam), "ApplyDamage", new[] { typeof(Thing), typeof(IntVec3), typeof(float) })]
    public static class Patch_BeamDamage
    {
        private static readonly MethodInfo TakeDamage = AccessTools.Method(typeof(Thing), nameof(Thing.TakeDamage));
        private static readonly MethodInfo Modify = AccessTools.Method(typeof(EntadBeamDamage), nameof(EntadBeamDamage.Modify));

        // Before each thing.TakeDamage(dinfo) the stack is [thing, dinfo]; push this and pass dinfo through Modify
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int found = 0;
            foreach (var ins in instructions)
            {
                if (ins.Calls(TakeDamage))
                {
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, Modify);
                    found++;
                }
                yield return ins;
            }
            if (found == 0) Log.Warning("[Entad Framework] Verb_ShootBeam.ApplyDamage has no TakeDamage call; entad damage types won't change beam damage");
        }

        public static void Postfix(Verb_ShootBeam __instance, Thing thing, float damageFactor, HashSet<IntVec3> ___pathCells)
        {
            // ApplyDamage returns early without hitting in these cases
            var props = __instance.verbProps;
            if (thing == null || thing.Destroyed || props.beamDamageDef == null) return;
            var comp = EntadWeaponDamage.CompOf(__instance.EquipmentSource, __instance.caster);
            if (comp == null || !EntadWeaponDamage.HasExtra(comp)) return;
            // Each burst shot hits once, like a bullet. A beam with beamTotalDamage shares that total over its path
            // cells, so the extra is shared the same way and a whole burst deals it about once.
            float factor = damageFactor;
            if (props.beamTotalDamage > 0f && ___pathCells != null && ___pathCells.Count > 0) factor /= ___pathCells.Count;
            DamageDef type = EntadWeaponDamage.TypeOverride(comp) ?? props.beamDamageDef;
            var template = new DamageInfo(type, 0f, props.beamDamageDef.defaultArmorPenetration,
                (__instance.CurrentTarget.Cell - __instance.caster.Position).AngleFlat, __instance.caster, null,
                __instance.EquipmentSource?.def, DamageInfo.SourceCategory.ThingOrUnknown, __instance.CurrentTarget.Thing);
            EntadWeaponDamage.DealExtra(comp, thing, template, factor);
        }
    }
}
