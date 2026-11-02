using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace EntadFramework
{
    // Damage that lands where it is cast, with no projectile to fly in from the caster: Flaring's burst, Crackling's
    // jolt, Arcing's chain, Volatile's blast. A damage-scaled trigger's power (EntadTrigger.scaleWithDamage, and a
    // diminishing adaptation) multiplies the damage; a blast's radius stays as it is. No weapon is named, so the damage
    // doesn't count as a hit with the caster's weapon for its other triggers.
    public abstract class CompProperties_AbilityEntadDamage : CompProperties_AbilityEffect
    {
        public DamageDef damageDef;
        // Negative: the damage def's own default, as GenExplosion reads it
        public int damageAmount = -1;
        public float armorPenetration = -1f;
        // Per target struck directly: the chance it catches fire
        public float igniteChance;
        // Extra damage to a mechanoid struck directly (EMP), scaled by power like the main damage
        public DamageDef mechanoidDamageDef;
        public int mechanoidDamage;
        // Played where a target is struck, before the damage (a killing blow takes the pawn off the map): Other is the
        // target or the blast's centre, Holder the caster. These are the ability's own look, as a vanilla explosion's
        // is, so they play whatever the visuals settings say.
        public List<EntadEventVisual> visuals;

        public int DamageAt(float power)
        {
            int amount = damageAmount >= 0 ? damageAmount : damageDef.defaultDamage;
            return Mathf.Max(1, Mathf.RoundToInt(amount * power));
        }

        // GenExplosion's rule, so a direct hit and a blast of the same def agree
        public float ArmorPenetrationAt(int damage)
        {
            if (armorPenetration >= 0f) return armorPenetration;
            if (damageAmount < 0 && damageDef.defaultArmorPenetration >= 0f) return damageDef.defaultArmorPenetration;
            return damage * 0.015f;
        }

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (damageDef == null) yield return "damageDef is required";
            else if (damageAmount < 0 && damageDef.defaultDamage < 0) yield return $"damageAmount is required: {damageDef.defName} has no default damage";
            if (igniteChance < 0f || igniteChance > 1f) yield return "igniteChance must be between 0 and 1";
            if ((mechanoidDamageDef != null) != (mechanoidDamage > 0)) yield return "mechanoidDamageDef and mechanoidDamage go together";
            foreach (var e in VisualErrors(visuals, parentDef)) yield return e;
        }

        // PlayEffect reads only a visual's chance: the item rules belong to a trait's own visuals
        internal static IEnumerable<string> VisualErrors(List<EntadEventVisual> list, AbilityDef parentDef)
        {
            if (list == null) yield break;
            foreach (var v in list)
            {
                foreach (var e in v.ConfigErrors(parentDef.defName)) yield return e;
                if (v.minIntervalTicks > 0 || v.whileBleeding || v.beforeIdentified.HasValue)
                    yield return $"{parentDef.defName}: {v.GetType().Name} ignores minIntervalTicks, whileBleeding and beforeIdentified on an ability";
            }
        }
    }

    public abstract class CompAbilityEffect_EntadDamage : CompAbilityEffect
    {
        public CompProperties_AbilityEntadDamage DamageProps => (CompProperties_AbilityEntadDamage)props;

        protected float Power => parent is EntadProcAbility proc ? proc.power : 1f;

        protected static bool Strikeable(Thing t, Map map) =>
            t != null && t.Spawned && t.Map == map && !t.Destroyed && !(t is Pawn p && p.Dead);

        // Damage, then the mechanoid's extra, then fire, each skipped once the target is gone
        protected void Strike(Thing target, IntVec3 from, float power)
        {
            var p = DamageProps;
            Pawn caster = parent.pawn;
            float angle = from == target.Position ? -1f : (target.Position - from).AngleFlat;
            int damage = p.DamageAt(power);
            target.TakeDamage(new DamageInfo(p.damageDef, damage, p.ArmorPenetrationAt(damage), angle, caster));
            // A pawn the first blow killed is a corpse now, not destroyed
            if (p.mechanoidDamageDef != null && target is Pawn mech && !mech.Dead && !mech.Destroyed && mech.RaceProps.IsMechanoid)
                target.TakeDamage(new DamageInfo(p.mechanoidDamageDef, Mathf.Max(1, Mathf.RoundToInt(p.mechanoidDamage * power)), 0f, angle, caster));
            if (p.igniteChance > 0f && !target.Destroyed && target.Spawned && Rand.Chance(p.igniteChance))
                target.TryAttachFire(Rand.Range(0.2f, 0.4f), caster);
        }
    }

    // A strike on the target alone (radius 0), or a blast around it. A blast can spare everything not hostile to the
    // caster (Volatile's rule: its side, neutrals, loose items, corpses, plants go into GenExplosion's ignoredThings);
    // the list is made as the blast starts, so a friend walking into the spreading blast can still be hit, and a flame
    // blast still lights the ground, sparing nobody from the fire.
    public class CompProperties_AbilityEntadStrike : CompProperties_AbilityEntadDamage
    {
        public float radius;
        public bool spareNonHostile;
        // Null: the damage def's own explosion sound
        public SoundDef explosionSound;

        public CompProperties_AbilityEntadStrike() => compClass = typeof(CompAbilityEffect_EntadStrike);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (radius < 0f) yield return "radius can't be negative";
            // GenExplosion deals the blast's damage over several ticks as it spreads, with no hook per pawn caught
            if (radius > 0f && (igniteChance > 0f || mechanoidDamageDef != null))
                yield return "igniteChance and mechanoidDamage only apply to a strike without a radius";
            if (spareNonHostile && radius <= 0f) yield return "spareNonHostile only applies to a blast (radius above 0)";
        }
    }

    public class CompAbilityEffect_EntadStrike : CompAbilityEffect_EntadDamage
    {
        public new CompProperties_AbilityEntadStrike Props => (CompProperties_AbilityEntadStrike)props;

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Map map = parent.pawn?.Map;
            if (map == null) return false;
            bool ok = Props.radius > 0f ? target.Cell.InBounds(map) : Strikeable(target.Thing, map);
            return ok && base.Valid(target, throwMessages);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            if (map == null) return;
            if (Props.radius <= 0f)
            {
                Thing t = target.Thing;
                if (!Strikeable(t, map)) return;
                EntadVisuals.PlayEffect(Props.visuals, map, caster, null, t, t.Position);
                Strike(t, caster.Position, Power);
                return;
            }
            IntVec3 center = target.Cell;
            if (!center.InBounds(map)) return;
            List<Thing> spared = null;
            if (Props.spareNonHostile)
            {
                spared = new List<Thing>();
                foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, Props.radius, true))
                    if (!t.HostileTo(caster)) spared.Add(t);
            }
            EntadVisuals.PlayEffect(Props.visuals, map, caster, null, target.Thing, center);
            int damage = Props.DamageAt(Power);
            GenExplosion.DoExplosion(center, map, Props.radius, Props.damageDef, caster, damage, Props.ArmorPenetrationAt(damage),
                Props.explosionSound, ignoredThings: spared);
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            if (Props.radius > 0f && !Patch_Ability_GetGizmos_Hide.IsHidden(parent)) GenDraw.DrawRadiusRing(target.Cell, Props.radius);
        }
    }

    // Strikes the target, then jumps to the nearest enemy within jumpRange of it that it hasn't struck yet, in sight
    // of where the last one stood, up to 'jumps' times. All in one tick: the bolts stay on screen for their arcTicks,
    // which is long enough to follow the path. Downed pawns are passed over, like the ones a chain bolt ignores.
    public class CompProperties_AbilityEntadChain : CompProperties_AbilityEntadDamage
    {
        public int jumps = 3;
        public float jumpRange = 5f;
        // Played for each jump, before it strikes: Holder is where the last target stood, Other the next target
        public List<EntadEventVisual> jumpVisuals;
        public SoundDef jumpSound;

        public CompProperties_AbilityEntadChain() => compClass = typeof(CompAbilityEffect_EntadChain);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (jumps < 0) yield return "jumps can't be negative";
            if (jumps > 0 && jumpRange <= 0f) yield return "jumpRange must be above 0";
            foreach (var e in VisualErrors(jumpVisuals, parentDef)) yield return e;
        }
    }

    public class CompAbilityEffect_EntadChain : CompAbilityEffect_EntadDamage
    {
        public new CompProperties_AbilityEntadChain Props => (CompProperties_AbilityEntadChain)props;

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Map map = parent.pawn?.Map;
            return map != null && Strikeable(target.Thing, map) && base.Valid(target, throwMessages);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            Thing current = target.Thing;
            if (map == null || !Strikeable(current, map)) return;
            float power = Power;
            var struck = new List<Thing>(Props.jumps + 1);
            IntVec3 from = caster.Position;
            for (int i = 0; ; i++)
            {
                struck.Add(current);
                IntVec3 cell = current.Position;
                Vector3 pos = current.DrawPos;
                EntadVisuals.PlayEffect(Props.visuals, map, caster, null, current, cell);
                Strike(current, from, power);
                if (i >= Props.jumps) break;
                Pawn next = NextTarget(cell, map, struck);
                if (next == null) break;
                EntadVisuals.PlayEffect(Props.jumpVisuals, map, null, pos, next, next.Position);
                Props.jumpSound?.PlayOneShot(new TargetInfo(next.Position, map));
                from = cell;
                current = next;
            }
        }

        // Nearest first, sight checked only for a pawn nearer than the best so far
        private Pawn NextTarget(IntVec3 from, Map map, List<Thing> struck)
        {
            Pawn caster = parent.pawn;
            var potential = map.attackTargetsCache.GetPotentialTargetsFor(caster);
            float best = Props.jumpRange * Props.jumpRange;
            Pawn found = null;
            for (int i = 0; i < potential.Count; i++)
            {
                if (!(potential[i].Thing is Pawn p) || p.Dead || p.Downed || !p.Spawned || struck.Contains(p)) continue;
                float d = p.Position.DistanceToSquared(from);
                if (d > best) continue;
                if (potential[i].ThreatDisabled(caster) || p.IsPsychologicallyInvisible() || !p.HostileTo(caster)) continue;
                if (!GenSight.LineOfSight(from, p.Position, map, skipFirstCell: true)) continue;
                best = d;
                found = p;
            }
            return found;
        }
    }
}
