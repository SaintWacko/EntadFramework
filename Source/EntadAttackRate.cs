using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // How fast a weapon attacks and how hard each attack hits, for triggers that scale by them (EntadTrigger.rateNormalized,
    // scaleWithDamage). Read from the item, not the wielder: the pawn's aiming time, melee speed and skill aren't part
    // of it, and neither is accuracy, which depends on range, cover and the shooter as much as on the weapon.
    public static class EntadAttackRate
    {
        // An assault rifle: 3-round bursts, 1 s warmup, 1.7 s cooldown, 11 damage a bullet
        public const float RangedReferenceSeconds = 1f;
        public const float RangedReferenceDamage = 11f;
        // A steel longsword, its strikes averaged
        public const float MeleeReferenceSeconds = 2f;
        public const float MeleeReferenceDamage = 18f;
        // A modded cannon or a 1-damage pistol shouldn't turn a proc into a map-wide blast or a fizzle
        public const float MinPower = 0.5f, MaxPower = 2.5f;

        // Weapon stats change with quality, traits and the CE fire mode, but nothing a few seconds of staleness matters to
        private const int RefreshTicks = GenTicks.TickRareInterval * 10;

        public class Cache
        {
            public int tick = int.MinValue;
            public float rangedSeconds, meleeSeconds, rangedDamage, meleeDamage;
        }

        public static bool IsWeapon(CompEntad item) => item?.parent?.def?.IsWeapon == true;

        // Multiplier on a per-hit chance: the weapon's time per attack against the reference, to the power of exponent
        public static float ChanceFactor(CompEntad item, bool melee, float exponent)
        {
            var c = Read(item);
            if (c == null) return 1f;
            float seconds = melee ? c.meleeSeconds : c.rangedSeconds;
            return seconds > 0f ? Raise(seconds / (melee ? MeleeReferenceSeconds : RangedReferenceSeconds), exponent) : 1f;
        }

        // Multiplier on an effect's strength: the weapon's damage per attack against the reference, clamped, to the
        // power of exponent
        public static float DamagePower(CompEntad item, bool melee, float exponent)
        {
            var c = Read(item);
            if (c == null) return 1f;
            float damage = melee ? c.meleeDamage : c.rangedDamage;
            return damage > 0f ? Raise(Mathf.Clamp(damage / (melee ? MeleeReferenceDamage : RangedReferenceDamage), MinPower, MaxPower), exponent) : 1f;
        }

        private static float Raise(float ratio, float exponent) => exponent >= 1f ? ratio : Mathf.Pow(ratio, exponent);

        private static Cache Read(CompEntad item)
        {
            if (!IsWeapon(item)) return null;
            var c = item.attackRate ?? (item.attackRate = new Cache());
            int now = Current.Game != null ? Find.TickManager.TicksGame : 0;
            if (c.tick == int.MinValue || now - c.tick >= RefreshTicks || now < c.tick)
            {
                Compute(item.parent, c);
                c.tick = now;
            }
            return c;
        }

        private static void Compute(Thing weapon, Cache c)
        {
            c.rangedSeconds = c.meleeSeconds = c.rangedDamage = c.meleeDamage = 0f;
            var verb = weapon.TryGetComp<CompEquippable>()?.PrimaryVerb;
            if (weapon.def.IsRangedWeapon && verb != null && !verb.verbProps.IsMeleeAttack)
            {
                // Verb.BurstShotCount and TicksBetweenBurstShots include unique weapon traits and our burst traits
                int burst = Mathf.Max(1, verb.BurstShotCount);
                float seconds = verb.verbProps.warmupTime + weapon.GetStatValue(StatDefOf.RangedWeapon_Cooldown)
                    + (burst - 1) * verb.TicksBetweenBurstShots / (float)GenTicks.TicksPerRealSecond;
                c.rangedSeconds = seconds / burst;
                ThingDef projectile = (verb as Verb_LaunchProjectile)?.Projectile ?? verb.verbProps.defaultProjectile;
                if (projectile?.projectile != null) c.rangedDamage = projectile.projectile.GetDamageAmount(weapon);
            }
            // Melee: every tool counts the same (vanilla weighs them by damage, which would only push this up a little)
            var tools = weapon.def.tools;
            if (tools.NullOrEmpty()) return;
            float cooldown = 0f, damage = 0f;
            int n = 0;
            for (int i = 0; i < tools.Count; i++)
            {
                var tool = tools[i];
                if (tool == null || tool.cooldownTime <= 0f) continue;
                DamageDef def = null;
                foreach (var m in tool.Maneuvers) { def = m.verb?.meleeDamageDef; break; }
                cooldown += tool.AdjustedCooldown(weapon);
                // The stuff's sharp or blunt multiplier reads the damage def's armor category
                damage += tool.AdjustedBaseMeleeDamageAmount(weapon, def?.armorCategory != null ? def : null);
                n++;
            }
            if (n == 0) return;
            c.meleeSeconds = cooldown / n;
            c.meleeDamage = damage / n;
        }
    }
}
