using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace EntadFramework
{
    // Artillery: an ability on a ranged weapon that fires one shot which splits in flight. The splits happen at fixed
    // distances from the shooter (8, 16 and 24 tiles by default), not at fractions of the target distance, so the shot
    // is stronger the further away it lands: 1 projectile under 8 tiles, then 3, 9 and 27. An explosive projectile
    // stops one split earlier (9 at most). The user asked for the fixed distances so it behaves like real artillery.
    //
    // The shot is a chain of carriers (Projectile_EntadArtillery). Each one flies, drawing the weapon's projectile
    // graphic, to the next split point and there launches its children toward targets scattered around its own aim
    // point. The last generation is the weapon's real projectile, launched with the weapon as its equipment, so
    // quality, damage traits (EntadWeaponDamage hooks Projectile.Impact and Explode) and friendly fire all work as for
    // an ordinary shot. Carriers hit nothing on the way (hit flags None), so a carrier passing a pawn or a sandbag
    // never stops the volley; the ability needs line of sight to the target cell instead. A shield still blocks a
    // carrier, and with it everything it would have split into.
    public class CompProperties_AbilityEntadArtillery : CompProperties_AbilityEffect
    {
        // Distances from the shooter, in tiles, ascending
        public List<float> splitDistances = new List<float> { 8f, 16f, 24f };
        // How many children each split makes
        public int splitCount = 3;
        // Most splits for a projectile that explodes
        public int maxSplitsExplosive = 2;
        // How far, in tiles, each split scatters its children's aim from its own; one value per split
        public List<float> spread = new List<float> { 2f, 1.4f, 1f };
        // A split needs at least this much flight left before the target, so a target just past a split distance
        // doesn't split right on top of it
        public float minFlightAfterSplit = 2f;

        public CompProperties_AbilityEntadArtillery() => compClass = typeof(CompAbilityEffect_EntadArtillery);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (splitCount < 1) yield return "splitCount must be at least 1";
            if (splitDistances.NullOrEmpty()) yield return "splitDistances is empty";
            else
            {
                for (int i = 1; i < splitDistances.Count; i++)
                    if (splitDistances[i] <= splitDistances[i - 1]) yield return "splitDistances must be ascending";
                if (spread == null || spread.Count < splitDistances.Count) yield return "spread needs one value per split distance";
            }
            if (parentDef.verbProperties?.verbClass == null || !typeof(Verb_EntadArtillery).IsAssignableFrom(parentDef.verbProperties.verbClass))
                yield return "the ability's verbClass should be EntadFramework.Verb_EntadArtillery, or its range isn't the weapon's";
        }

        public int SplitsFor(float distance, ThingDef projectile)
        {
            int max = Mathf.Min(splitDistances.Count, EntadArtillery.IsExplosive(projectile) ? maxSplitsExplosive : splitDistances.Count);
            int n = 0;
            while (n < max && splitDistances[n] <= distance - minFlightAfterSplit) n++;
            return n;
        }

        public int ProjectilesFor(int splits)
        {
            int count = 1;
            for (int i = 0; i < splits; i++) count *= splitCount;
            return count;
        }
    }

    public class CompAbilityEffect_EntadArtillery : CompAbilityEffect
    {
        public new CompProperties_AbilityEntadArtillery Props => (CompProperties_AbilityEntadArtillery)props;

        public override void Apply(LocalTargetInfo target, LocalTargetInfo dest)
        {
            base.Apply(target, dest);
            Pawn pawn = parent.pawn;
            Map map = pawn?.Map;
            var verb = EntadArtillery.WeaponVerb(pawn);
            ThingDef projectile = verb?.Projectile;
            if (map == null || projectile == null || !target.IsValid) return;

            // The weapon's own shot sound and muzzle flash, as Verb.TryCastNextBurstShot plays them
            if (verb.verbProps.muzzleFlashScale > 0.01f) FleckMaker.Static(pawn.Position, map, FleckDefOf.ShotFlash, verb.verbProps.muzzleFlashScale);
            verb.verbProps.soundCast?.PlayOneShot(new TargetInfo(pawn.Position, map));

            Vector3 from = pawn.DrawPos;
            Vector3 aim = target.Cell.ToVector3Shifted();
            int splits = Props.SplitsFor((aim - from).MagnitudeHorizontal(), projectile);
            EntadArtillery.Fire(map, pawn, verb.EquipmentSource, projectile, from, from, aim, target, 0, splits,
                Props.splitDistances, Props.spread, Props.splitCount);
        }

        public override bool Valid(LocalTargetInfo target, bool throwMessages = false)
        {
            if (EntadArtillery.WeaponVerb(parent.pawn)?.Projectile == null)
            {
                if (throwMessages)
                    Messages.Message("EF_ArtilleryNoWeapon".Translate(), parent.pawn, MessageTypeDefOf.RejectInput, historical: false);
                return false;
            }
            return base.Valid(target, throwMessages);
        }

        // The number of projectiles the shot will have split into by the cell under the cursor
        public override string ExtraLabelMouseAttachment(LocalTargetInfo target)
        {
            if (!target.IsValid || parent.pawn?.Spawned != true) return null;
            ThingDef projectile = EntadArtillery.WeaponVerb(parent.pawn)?.Projectile;
            if (projectile == null) return null;
            float distance = (target.Cell.ToVector3Shifted() - parent.pawn.DrawPos).MagnitudeHorizontal();
            return "EF_ArtilleryCount".Translate(Props.ProjectilesFor(Props.SplitsFor(distance, projectile)));
        }

        // A faint ring at each split distance the weapon can reach. Skipped while the trait is hidden, like the
        // cursor label (Patch_VerbCastAbility_ExtraLabel_Hide), since the rings would give the ability away.
        public override void DrawEffectPreview(LocalTargetInfo target)
        {
            Pawn pawn = parent.pawn;
            if (pawn?.Spawned != true || Patch_Ability_GetGizmos_Hide.IsHidden(parent)) return;
            ThingDef projectile = EntadArtillery.WeaponVerb(pawn)?.Projectile;
            if (projectile == null) return;
            float range = parent.verb.EffectiveRange;
            int max = Props.SplitsFor(float.MaxValue, projectile);
            for (int i = 0; i < max; i++)
            {
                float d = Props.splitDistances[i];
                if (d >= range || d >= GenRadial.MaxRadialPatternRadius) break;
                GenDraw.DrawRadiusRing(pawn.Position, d, RingColor);
            }
        }

        private static readonly Color RingColor = new Color(1f, 0.6f, 0.3f, 0.5f);
    }

    // The ability's verb: its range is the wielded weapon's range, so Far-Reaching, weather caps and a sniper rifle's
    // long reach all carry over. Vanilla's range ring and target check both read EffectiveRange.
    public class Verb_EntadArtillery : Verb_CastAbility
    {
        public override float EffectiveRange => EntadArtillery.WeaponVerb(CasterPawn)?.EffectiveRange ?? base.EffectiveRange;
    }

    public static class EntadArtillery
    {
        // The wielded weapon's projectile verb, or null for melee, beams, sprays and one-use launchers
        public static Verb_LaunchProjectile WeaponVerb(Pawn pawn)
        {
            var verb = pawn?.equipment?.PrimaryEq?.PrimaryVerb as Verb_LaunchProjectile;
            return verb == null || verb is Verb_ShootOneUse ? null : verb;
        }

        // Which weapons the Artillery trait can roll on: the same rule WeaponVerb applies at cast time, from the def
        public static bool Fits(ThingDef td)
        {
            if (td == null || !td.IsRangedWeapon || td.Verbs.NullOrEmpty()) return false;
            VerbProperties v = td.Verbs.FirstOrDefault(x => x.isPrimary) ?? td.Verbs[0];
            return v.verbClass != null && typeof(Verb_LaunchProjectile).IsAssignableFrom(v.verbClass)
                && !typeof(Verb_ShootOneUse).IsAssignableFrom(v.verbClass) && v.defaultProjectile?.projectile != null;
        }

        public static bool IsArtillery(AbilityDef ability) =>
            ability?.comps != null && ability.comps.Any(c => c is CompProperties_AbilityEntadArtillery);

        public static bool IsExplosive(ThingDef projectile) =>
            projectile != null && (projectile.projectile?.explosionRadius > 0f || typeof(Projectile_Explosive).IsAssignableFrom(projectile.thingClass));

        // Launches generation 'index' of the shot from 'origin' toward 'aim': a carrier while splits remain, the real
        // projectile after the last one
        public static void Fire(Map map, Thing launcher, Thing equipment, ThingDef projectile, Vector3 shooterPos, Vector3 origin, Vector3 aim,
            LocalTargetInfo intended, int index, int splits, List<float> distances, List<float> spread, int splitCount)
        {
            IntVec3 originCell = origin.ToIntVec3();
            if (!originCell.InBounds(map)) return;
            if (index >= splits)
            {
                var real = (Projectile)GenSpawn.Spawn(projectile, originCell, map);
                real.Launch(launcher, origin, new LocalTargetInfo(aim.ToIntVec3()), intended, ProjectileHitFlags.All, false, equipment);
                return;
            }
            // The next split point lies on the way to the aim, where the shot is distances[index] from the shooter.
            // Measured along the flight from here, which is close enough: the scatter angles are small.
            Vector3 dir = (aim - origin).Yto0().normalized;
            float along = Mathf.Max(0.5f, distances[index] - (origin - shooterPos).MagnitudeHorizontal());
            IntVec3 splitCell = (origin + dir * along).ToIntVec3();
            if (!splitCell.InBounds(map)) splitCell = aim.ToIntVec3();
            var carrier = (Projectile_EntadArtillery)GenSpawn.Spawn(EntadArtilleryDefOf.Entad_ArtilleryCarrier, originCell, map);
            carrier.Setup(projectile, shooterPos, aim, index, splits, distances, spread, splitCount);
            carrier.Launch(launcher, origin, new LocalTargetInfo(splitCell), intended, ProjectileHitFlags.None, false, equipment);
        }
    }

    public class Projectile_EntadArtillery : Projectile
    {
        private ThingDef realProjectile;
        private Vector3 shooterPos;
        private Vector3 aim;
        private int index;
        private int splits;
        private List<float> distances;
        private List<float> spread;
        private int splitCount = 3;

        // Nine carriers split on the same tick at the second split; one shot sound is enough
        private static int lastSplitSoundTick = -1;

        public void Setup(ThingDef realProjectile, Vector3 shooterPos, Vector3 aim, int index, int splits,
            List<float> distances, List<float> spread, int splitCount)
        {
            this.realProjectile = realProjectile;
            this.shooterPos = shooterPos;
            this.aim = aim;
            this.index = index;
            this.splits = splits;
            this.distances = distances;
            this.spread = spread;
            this.splitCount = splitCount;
        }

        // Reached the split point (ImpactSomething finds nothing to hit, since CanHit is false for every thing with
        // hit flags None), or a shield caught it
        protected override void Impact(Thing hitThing, bool blockedByShield = false)
        {
            Map map = Map;
            if (blockedByShield || map == null || realProjectile == null || distances == null)
            {
                base.Impact(hitThing, blockedByShield);
                return;
            }
            Vector3 at = destination.Yto0();
            FleckMaker.Static(at, map, FleckDefOf.ShotFlash, 1.2f);
            if (lastSplitSoundTick != Find.TickManager.TicksGame)
            {
                lastSplitSoundTick = Find.TickManager.TicksGame;
                VerbProperties weaponVerb = equipment?.def.Verbs?.FirstOrDefault(v => v.isPrimary) ?? equipment?.def.Verbs?.FirstOrDefault();
                weaponVerb?.soundCast?.PlayOneShot(new TargetInfo(at.ToIntVec3(), map));
            }
            float r = spread != null && index < spread.Count ? spread[index] : 1f;
            float start = Rand.Range(0f, 360f);
            for (int i = 0; i < splitCount; i++)
            {
                float angle = start + i * 360f / splitCount + Rand.Range(-20f, 20f);
                Vector3 childAim = aim + Quaternion.AngleAxis(angle, Vector3.up) * Vector3.forward * (r * Rand.Range(0.5f, 1f));
                if (!childAim.ToIntVec3().InBounds(map)) childAim = aim;
                EntadArtillery.Fire(map, launcher, equipment, realProjectile, shooterPos, at, childAim, intendedTarget, index + 1, splits,
                    distances, spread, splitCount);
            }
            Destroy();
        }

        // Looks like the weapon's own projectile; a carrier flies straight, so no arc or shadow
        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            Graphic graphic = realProjectile?.graphic;
            if (graphic == null)
            {
                base.DrawAt(drawLoc, flip);
                return;
            }
            Graphics.DrawMesh(MeshPool.GridPlane(graphic.drawSize), drawLoc, ExactRotation, graphic.MatSingle, 0);
            Comps_PostDraw();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref realProjectile, "realProjectile");
            Scribe_Values.Look(ref shooterPos, "shooterPos");
            Scribe_Values.Look(ref aim, "aim");
            Scribe_Values.Look(ref index, "index");
            Scribe_Values.Look(ref splits, "splits");
            Scribe_Values.Look(ref splitCount, "splitCount", 3);
            Scribe_Collections.Look(ref distances, "distances", LookMode.Value);
            Scribe_Collections.Look(ref spread, "spread", LookMode.Value);
        }
    }

    [DefOf]
    public static class EntadArtilleryDefOf
    {
        public static ThingDef Entad_ArtilleryCarrier;

        static EntadArtilleryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(EntadArtilleryDefOf));
    }
}
