using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Mirrored: worn apparel with Entad_DeflectChance turns aside some of the shots that hit its wearer, Bastle
    // Framework's mirror-scaled. Asked from CompEntad.PostPreApplyDamage, which vanilla calls for each worn piece in
    // turn (worn order, first absorber wins) before the hit reaches the wearer's health, so a deflected shot causes
    // no stagger, stun, clamor or thought.
    public static class EntadDeflect
    {
        // Share of deflected shots that fly back at the shooter, aimed; the rest glance off at random
        public const float ReflectChance = 0.4f;

        // Accuracy of a shot sent back, as a ShootingAccuracyPawn-style per-tile value: 0.95 is roughly a middling
        // colonist, about 60% on target at 10 tiles
        private const float ReflectAccuracy = 0.95f;

        public static bool TryDeflect(CompEntad comp, DamageInfo dinfo)
        {
            // Shots only: explosions and melee get through. The apparel's own armor damage has no instigator.
            if (dinfo.Def == null || !dinfo.Def.isRanged || dinfo.Def.isExplosive || dinfo.Instigator == null || dinfo.Amount <= 0f) return false;
            if (!(comp.parent is Apparel apparel) || !(apparel.Wearer is Pawn wearer) || !wearer.Spawned) return false;
            // A shield takes the shot instead. Vanilla asks pieces in worn order, so a jacket would otherwise get its
            // try before the belt. On the shield belt itself the shield comp comes first anyway.
            if (!dinfo.Def.ignoreShields && HasActiveShield(wearer)) return false;
            // The stat part applies binding: a bound item on someone outside its bloodline reads 0
            float chance = apparel.GetStatValue(EntadStatDefOf.Entad_DeflectChance);
            if (chance <= 0f || !Rand.Chance(chance)) return false;

            EntadVisuals.Deflected(comp, wearer, dinfo.Instigator);
            if (wearer.Map == Find.CurrentMap && !wearer.Position.Fogged(wearer.Map))
                FleckMaker.ThrowMicroSparks(wearer.DrawPos, wearer.Map);
            Redirect(wearer, dinfo);
            comp.RevealStat(EntadStatDefOf.Entad_DeflectChance);
            return true;
        }

        private static bool HasActiveShield(Pawn wearer)
        {
            var worn = wearer.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                var shield = worn[i].TryGetComp<CompShield>();
                if (shield != null && shield.ShieldState == ShieldState.Active) return true;
            }
            return false;
        }

        // Back at the shooter or off at a random angle, as the weapon's own projectile. Weapons that fire something
        // that isn't a vanilla Projectile (Combat Extended's ProjectileCE) still have the hit turned aside, just not
        // sent anywhere. The launch waits a tick so nothing is hit inside this damage call.
        private static void Redirect(Pawn wearer, DamageInfo dinfo)
        {
            ThingDef weapon = EntadDamageSource.WeaponOf(dinfo);
            ThingDef proj = weapon?.Verbs?.FirstOrDefault(v => v.defaultProjectile != null)?.defaultProjectile;
            if (proj == null || !typeof(Projectile).IsAssignableFrom(proj.thingClass)) return;
            Thing shooter = dinfo.Instigator;
            bool reflect = Rand.Chance(ReflectChance);
            EntadTriggerQueue.NextTick(() =>
            {
                if (!wearer.Spawned) return;
                if (reflect && !shooter.Destroyed && shooter.Spawned && shooter.Map == wearer.Map)
                    LaunchReflected(wearer, shooter, proj);
                else
                    LaunchGlancing(wearer, proj, shooter);
            });
        }

        // Off at a random angle, 4 to 12 tiles. It can hit anyone along the way, the wearer's allies and the shooter
        // included. The shooter stays the intended target (as with vanilla's wild misses), so a stray hit on a
        // bystander hostile to the shooter counts as crossfire and costs no goodwill.
        private static void LaunchGlancing(Pawn wearer, ThingDef proj, Thing shooter)
        {
            Map map = wearer.Map;
            Vector3 dir = Quaternion.AngleAxis(Rand.Range(0f, 360f), Vector3.up) * Vector3.forward;
            IntVec3 cell = (wearer.DrawPos + dir * Rand.Range(4f, 12f)).ToIntVec3().ClampInsideMap(map);
            if (cell == wearer.Position) return;
            var p = (Projectile)GenSpawn.Spawn(proj, wearer.Position, map);
            LocalTargetInfo intended = shooter != null && shooter.Spawned && shooter.Map == map ? new LocalTargetInfo(shooter) : new LocalTargetInfo(cell);
            p.Launch(wearer, wearer.DrawPos, cell, intended, ProjectileHitFlags.All);
        }

        // As if the wearer had aimed it, following Verb_LaunchProjectile: a wild miss, else the shooter's cover, else
        // the shooter
        private static void LaunchReflected(Pawn wearer, Thing shooter, ThingDef proj)
        {
            Map map = wearer.Map;
            var p = (Projectile)GenSpawn.Spawn(proj, wearer.Position, map);
            float dist = (shooter.Position - wearer.Position).LengthHorizontal;
            float size = shooter is Pawn sp ? Mathf.Clamp(sp.BodySize, 0.5f, 2f) : 1f;
            float aim = ShotReport.HitFactorFromShooter(null, dist, ReflectAccuracy) * size;
            if (!Rand.Chance(aim))
            {
                IntVec3 cell = shooter.Position + GenRadial.RadialPattern[Rand.Range(1, GenRadial.NumCellsInRadius(2.9f))];
                if (!cell.InBounds(map)) cell = shooter.Position;
                var flags = ProjectileHitFlags.NonTargetWorld;
                if (Rand.Chance(0.5f)) flags |= ProjectileHitFlags.NonTargetPawns;
                p.Launch(wearer, wearer.DrawPos, cell, shooter, flags);
                return;
            }
            if (Rand.Chance(CoverUtility.CalculateOverallBlockChance(shooter, wearer.Position, map))
                && CoverUtility.CalculateCoverGiverSet(shooter, wearer.Position, map).TryRandomElementByWeight(c => c.BlockChance, out var cover))
            {
                p.Launch(wearer, wearer.DrawPos, cover.Thing, shooter, ProjectileHitFlags.NonTargetWorld);
                return;
            }
            p.Launch(wearer, wearer.DrawPos, shooter, shooter, ProjectileHitFlags.IntendedTarget);
        }
    }
}
