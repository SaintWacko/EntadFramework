using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace EntadFramework
{
    // Ability effects made for triggers to cast (Defs/EntadProcAbilities.xml). They work as ordinary abilities too,
    // but each Valid refuses a target it would do nothing to, so a trigger whose cast would be wasted keeps its
    // cooldown (EntadProcs.TryPrepare).

    // A second shot from the caster's own weapon, from where the first one landed to the nearest other enemy in sight
    // of that spot. It is the weapon's real projectile, launched with the weapon, so quality, the weapon's own entad
    // damage traits and a vanilla unique weapon's traits all apply. It only hits its target: nothing else on the way.
    public class CompProperties_AbilityEntadRicochet : CompProperties_AbilityEffect
    {
        public float range = 8f;

        public CompProperties_AbilityEntadRicochet() => compClass = typeof(CompAbilityEffect_EntadRicochet);
    }

    public class CompAbilityEffect_EntadRicochet : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadRicochet Props => (CompProperties_AbilityEntadRicochet)props;

        // Nearest first, and sight checked only for a pawn nearer than the best so far. Anyone standing on the cell
        // itself is the pawn just hit.
        private Pawn NextTarget(IntVec3 from)
        {
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            var potential = map.attackTargetsCache.GetPotentialTargetsFor(caster);
            float best = Props.range * Props.range;
            Pawn found = null;
            for (int i = 0; i < potential.Count; i++)
            {
                if (!(potential[i].Thing is Pawn p) || p.Position == from || p.Dead || p.Downed || !p.Spawned) continue;
                float d = p.Position.DistanceToSquared(from);
                if (d > best) continue;
                if (potential[i].ThreatDisabled(caster) || p.IsPsychologicallyInvisible() || !p.HostileTo(caster)) continue;
                if (!GenSight.LineOfSight(from, p.Position, map, skipFirstCell: true)) continue;
                best = d;
                found = p;
            }
            return found;
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Pawn caster = parent.pawn;
            if (caster?.Map == null || EntadArtillery.WeaponVerb(caster)?.Projectile == null) return false;
            return NextTarget(target.Cell) != null && base.Valid(target, throwMessages);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            var verb = EntadArtillery.WeaponVerb(caster);
            ThingDef projectile = verb?.Projectile;
            IntVec3 from = target.Cell;
            Pawn next = projectile != null ? NextTarget(from) : null;
            if (next == null) return;
            Thing weapon = verb.EquipmentSource;
            var shot = (Projectile)GenSpawn.Spawn(projectile, from, map);
            EntadArtillery.ApplyUniqueWeaponTraits(shot, weapon);
            shot.Launch(caster, from.ToVector3Shifted(), next, next, ProjectileHitFlags.IntendedTarget, false, weapon);
            FleckMaker.ThrowMicroSparks(from.ToVector3Shifted(), map);
            verb.verbProps.soundCast?.PlayOneShot(new TargetInfo(from, map));
        }
    }

    // Knocks the target's weapon out of their hands and a few tiles away. People only: a mechanoid's or a turret's
    // gun is destroyed when dropped (destroyOnDrop), which would make this a weapon-destroyer.
    public class CompProperties_AbilityEntadDisarm : CompProperties_AbilityEffect
    {
        public float throwDistance = 3f;

        public CompProperties_AbilityEntadDisarm() => compClass = typeof(CompAbilityEffect_EntadDisarm);
    }

    public class CompAbilityEffect_EntadDisarm : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadDisarm Props => (CompProperties_AbilityEntadDisarm)props;

        private static bool CanDisarm(Pawn p) =>
            p != null && p.Spawned && !p.Dead && !p.Downed && p.RaceProps.Humanlike
            && p.equipment?.Primary != null && !p.equipment.Primary.def.destroyOnDrop;

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false) =>
            CanDisarm(target.Pawn) && base.Valid(target, throwMessages);

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn victim = target.Pawn;
            if (!CanDisarm(victim)) return;
            Map map = victim.Map;
            IntVec3 pos = victim.Position;
            // Away from their feet, so getting it back costs them a few seconds of the fight
            if (!CellFinder.TryFindRandomCellNear(pos, map, Mathf.CeilToInt(Props.throwDistance),
                    c => c.DistanceTo(pos) >= 2f && c.Standable(map) && GenSight.LineOfSight(pos, c, map), out IntVec3 cell))
                cell = pos;
            if (!victim.equipment.TryDropEquipment(victim.equipment.Primary, out _, cell)) return;
            MoteMaker.ThrowText(victim.DrawPos, map, "EF_Disarmed".Translate());
        }
    }

    // Throws every enemy around the caster a few tiles straight back, with vanilla's stunning flyer (the one
    // fleshbeasts burst out with): airborne for a moment, then stunned 1 to 3 seconds where they land. Downed pawns,
    // pawns carrying someone and anything too big stay put.
    public class CompProperties_AbilityEntadShockwave : CompProperties_AbilityEffect
    {
        public float radius = 3.9f;
        public float distance = 3f;
        public float maxBodySize = 2f;
        public FleckDef fleck;

        public CompProperties_AbilityEntadShockwave() => compClass = typeof(CompAbilityEffect_EntadShockwave);
    }

    public class CompAbilityEffect_EntadShockwave : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadShockwave Props => (CompProperties_AbilityEntadShockwave)props;

        private static readonly List<Pawn> thrown = new List<Pawn>();
        private static readonly HashSet<IntVec3> taken = new HashSet<IntVec3>();

        // Copied out first: a flyer despawns its pawn, which changes the spawned list
        private void CollectTargets(IntVec3 center, Map map)
        {
            Pawn caster = parent.pawn;
            float r2 = Props.radius * Props.radius;
            thrown.Clear();
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
            {
                Pawn p = all[i];
                if (p == caster || p.Dead || p.Downed || (p.Position - center).LengthHorizontalSquared > r2) continue;
                if (p.BodySize > Props.maxBodySize || p.carryTracker?.CarriedThing != null || !p.HostileTo(caster)) continue;
                thrown.Add(p);
            }
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Map map = parent.pawn?.Map;
            if (map == null) return false;
            CollectTargets(parent.pawn.Position, map);
            bool any = thrown.Count > 0;
            thrown.Clear();
            return any && base.Valid(target, throwMessages);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            IntVec3 center = caster.Position;
            CollectTargets(center, map);
            // Farthest first: a nearer pawn thrown first could find its line blocked by one still standing behind it
            thrown.SortByDescending(p => (p.Position - center).LengthHorizontalSquared);
            var targets = thrown.ToArray();
            thrown.Clear();
            taken.Clear();
            if (Props.fleck != null) FleckMaker.Static(center, map, Props.fleck, Props.radius + 1f);
            for (int i = 0; i < targets.Length; i++)
            {
                Pawn p = targets[i];
                if (!p.Spawned || !TryFindLanding(p, center, map, out IntVec3 landing)) continue;
                taken.Add(landing);
                p.rotationTracker.FaceCell(center);
                var flyer = PawnFlyer.MakeFlyer(ThingDefOf.PawnFlyer_Stun, p, landing, null, null);
                if (flyer != null) GenSpawn.Spawn(flyer, landing, map);
            }
            taken.Clear();
        }

        // Straight away from the caster, as far as the ground allows: the farthest free cell on that line
        private bool TryFindLanding(Pawn p, IntVec3 center, Map map, out IntVec3 landing)
        {
            landing = IntVec3.Invalid;
            Vector3 dir = (p.Position - center).ToVector3();
            if (dir.sqrMagnitude < 0.01f) dir = Vector3Utility.RandomHorizontalOffset(1f);
            dir.Normalize();
            Vector3 start = p.Position.ToVector3Shifted();
            for (float d = Props.distance; d >= 1f; d -= 1f)
            {
                IntVec3 c = (start + dir * d).ToIntVec3();
                if (c == p.Position || !c.InBounds(map) || c.Fogged(map) || !c.Standable(map) || taken.Contains(c) || c.GetFirstPawn(map) != null) continue;
                if (!GenSight.LineOfSight(p.Position, c, map, skipFirstCell: true)) continue;
                landing = c;
                return true;
            }
            return false;
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            if (!Patch_Ability_GetGizmos_Hide.IsHidden(parent)) GenDraw.DrawRadiusRing(parent.pawn.Position, Props.radius);
        }
    }

    // Each enemy around the target may panic and flee: 'chance' times their psychic sensitivity, so a psychically
    // deaf one never does. Mechanoids and entities don't feel fear, and a pawn already in a mental state is left in it.
    public class CompProperties_AbilityEntadHarrow : CompProperties_AbilityEffect
    {
        public float radius = 6f;
        public float chance = 0.35f;
        public MentalStateDef mentalState;
        public FleckDef fleck;

        public CompProperties_AbilityEntadHarrow() => compClass = typeof(CompAbilityEffect_EntadHarrow);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (mentalState == null) yield return "mentalState is required";
            if (radius <= 0f) yield return "radius must be above 0";
        }
    }

    public class CompAbilityEffect_EntadHarrow : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadHarrow Props => (CompProperties_AbilityEntadHarrow)props;

        private static readonly List<Pawn> nearby = new List<Pawn>();

        private bool CanFrighten(Pawn p, Pawn caster) =>
            !p.Dead && !p.Downed && !p.InMentalState && !p.RaceProps.IsMechanoid && !p.RaceProps.IsAnomalyEntity
            && p.mindState?.mentalStateHandler != null && p.HostileTo(caster);

        private void Collect(IntVec3 center, Map map)
        {
            Pawn caster = parent.pawn;
            float r2 = Props.radius * Props.radius;
            nearby.Clear();
            var all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
                if ((all[i].Position - center).LengthHorizontalSquared <= r2 && CanFrighten(all[i], caster)) nearby.Add(all[i]);
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            Map map = parent.pawn?.Map;
            if (map == null) return false;
            Collect(target.Cell, map);
            bool any = nearby.Count > 0;
            nearby.Clear();
            return any && base.Valid(target, throwMessages);
        }

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            IntVec3 center = target.Cell;
            Collect(center, map);
            var targets = nearby.ToArray();
            nearby.Clear();
            if (Props.fleck != null) FleckMaker.Static(center, map, Props.fleck, Props.radius);
            for (int i = 0; i < targets.Length; i++)
            {
                Pawn p = targets[i];
                if (!Rand.Chance(Props.chance * p.GetStatValue(StatDefOf.PsychicSensitivity))) continue;
                if (p.mindState.mentalStateHandler.TryStartMentalState(Props.mentalState, forceWake: true, otherPawn: caster))
                    MoteMaker.ThrowText(p.DrawPos, map, "EF_Harrowed".Translate());
            }
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            if (!Patch_Ability_GetGizmos_Hide.IsHidden(parent)) GenDraw.DrawRadiusRing(target.Cell, Props.radius);
        }
    }

    // A blast that only hurts the caster's enemies: pawns hostile to the caster and things a hostile faction owns.
    // Everything else in reach (the caster's side, neutrals, loose items, corpses, plants) goes into GenExplosion's
    // ignoredThings. The list is made as the blast starts, so a friend walking into the spreading blast can still be
    // hit. No weapon is named, so the blast doesn't count as a hit with the caster's weapon for its other triggers.
    public class CompProperties_AbilityEntadVolatile : CompProperties_AbilityEffect
    {
        public float radius = 2.9f;
        public DamageDef damageDef;
        public int damageAmount = -1;
        public float armorPenetration = -1f;
        public SoundDef explosionSound;

        public CompProperties_AbilityEntadVolatile() => compClass = typeof(CompAbilityEffect_EntadVolatile);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (damageDef == null) yield return "damageDef is required";
            if (radius <= 0f) yield return "radius must be above 0";
        }
    }

    public class CompAbilityEffect_EntadVolatile : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadVolatile Props => (CompProperties_AbilityEntadVolatile)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn caster = parent.pawn;
            Map map = caster.Map;
            IntVec3 center = target.Cell;
            if (map == null || !center.InBounds(map)) return;
            var spared = new List<Thing>();
            foreach (Thing t in GenRadial.RadialDistinctThingsAround(center, map, Props.radius, true))
            {
                if (!t.HostileTo(caster)) spared.Add(t);
            }
            GenExplosion.DoExplosion(center, map, Props.radius, Props.damageDef, caster, Props.damageAmount, Props.armorPenetration,
                Props.explosionSound, ignoredThings: spared);
        }

        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            if (!Patch_Ability_GetGizmos_Hide.IsHidden(parent)) GenDraw.DrawRadiusRing(target.Cell, Props.radius);
        }
    }
}
