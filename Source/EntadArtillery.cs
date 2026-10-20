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
    // quality and damage traits (EntadWeaponDamage hooks Projectile.Impact and Explode) work as for an ordinary shot.
    // Friendly fire mostly doesn't: vanilla scales the chance of a stray shot hitting a non-hostile pawn by
    // VerbUtility.InterceptChanceFactorFromDistance from the projectile's origin, which is 0 within 5 tiles, and the
    // final projectiles start at the last split point, usually 2 to 8 tiles from where they land. Explosions still
    // hurt everyone in the blast. Carriers hit nothing on the way (hit flags None), so a carrier passing a pawn or a
    // sandbag never stops the volley; instead each carrier only flies as far as the cells are clear (Fire checks the
    // line), so a scattered branch splits in front of a wall rather than inside it. A shield still blocks a carrier,
    // and with it everything it would have split into.
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
        // Lock-on, for the last generation only. A final shot aimed at a cell only hits what stands on that exact
        // cell when it lands, which a moving pawn has usually left by then. So each final shot rolls lockChance, and
        // on a success it is aimed at a pawn near its landing point instead, the way a normal shot is aimed at a
        // pawn (Projectile.ImpactSomething hits usedTarget wherever it has moved to). Pawns count within the last
        // split's spread plus lockRadiusBonus of the aim. Hostiles weigh 1 and other pawns with a faction
        // allyLockWeight; downed pawns and wild animals are never picked. No pawn takes more than maxLocksPerPawn
        // locked shots from one volley, so a volley can't all go into one target (the user's rule).
        public float lockChance = 0.7f;
        public int maxLocksPerPawn = 3;
        public float lockRadiusBonus = 0.5f;
        public float allyLockWeight = 0.3f;

        public CompProperties_AbilityEntadArtillery() => compClass = typeof(CompAbilityEffect_EntadArtillery);

        public override IEnumerable<string> ConfigErrors(AbilityDef parentDef)
        {
            foreach (var e in base.ConfigErrors(parentDef)) yield return e;
            if (splitCount < 1) yield return "splitCount must be at least 1";
            if (maxLocksPerPawn < 0) yield return "maxLocksPerPawn can't be negative";
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
            var volley = new ArtilleryVolley { lockChance = Props.lockChance, maxLocksPerPawn = Props.maxLocksPerPawn,
                lockRadiusBonus = Props.lockRadiusBonus, allyLockWeight = Props.allyLockWeight };
            EntadArtillery.Fire(map, pawn, verb.EquipmentSource, projectile, from, from, aim, target, 0, splits,
                Props.splitDistances, Props.spread, Props.splitCount, volley);
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

    // What one volley shares across its carriers: the lock-on numbers and how many locked shots each pawn has
    // taken. The counts aren't saved: after a load each carrier in flight starts its own empty table, so a volley
    // saved mid-flight can lock a few more shots onto one pawn than the cap. The numbers are saved with each carrier.
    public class ArtilleryVolley : IExposable
    {
        public float lockChance = 0.7f;
        public int maxLocksPerPawn = 3;
        public float lockRadiusBonus = 0.5f;
        public float allyLockWeight = 0.3f;
        public Dictionary<Pawn, int> locks = new Dictionary<Pawn, int>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref lockChance, "lockChance", 0.7f);
            Scribe_Values.Look(ref maxLocksPerPawn, "maxLocksPerPawn", 3);
            Scribe_Values.Look(ref lockRadiusBonus, "lockRadiusBonus", 0.5f);
            Scribe_Values.Look(ref allyLockWeight, "allyLockWeight", 0.3f);
            if (Scribe.mode == LoadSaveMode.LoadingVars && locks == null) locks = new Dictionary<Pawn, int>();
        }
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

        // What Verb_LaunchProjectile.TryCastShot gives each projectile from a vanilla unique weapon's traits: a damage
        // type override and extra damages (an EMP trait, say)
        public static void ApplyUniqueWeaponTraits(Projectile p, Thing equipment)
        {
            if (equipment == null || !equipment.TryGetComp(out CompUniqueWeapon comp)) return;
            foreach (WeaponTraitDef trait in comp.TraitsListForReading)
            {
                if (trait.damageDefOverride != null) p.damageDefOverride = trait.damageDefOverride;
                if (!trait.extraDamages.NullOrEmpty())
                {
                    if (p.extraDamages == null) p.extraDamages = new List<ExtraDamage>();
                    p.extraDamages.AddRange(trait.extraDamages);
                }
            }
        }

        // How far along 'dir' from 'origin', up to 'max', the cells can be seen over: walls, rock and closed doors stop
        // it, and 'blocked' is the cell that did (invalid when nothing did). 0.2-tile steps, as vanilla's
        // Projectile.CheckForFreeInterceptBetween walks, so a line that clips a wall's corner still finds it.
        // 'fromShooter' ignores the cells around the shooter: the ability's line of sight may come from leaning out
        // past a corner, and that corner wall would otherwise cut the whole volley down to one shot.
        private static float ClearDistance(Map map, Vector3 origin, Vector3 dir, float max, bool fromShooter, out IntVec3 blocked)
        {
            blocked = IntVec3.Invalid;
            IntVec3 start = origin.ToIntVec3();
            float clear = 0f;
            for (float d = 0.2f; d <= max; d += 0.2f)
            {
                IntVec3 c = (origin + dir * d).ToIntVec3();
                if (!c.InBounds(map)) break;
                bool skip = fromShooter ? c.AdjacentTo8WayOrInside(start) : c == start;
                if (!skip && !c.CanBeSeenOver(map))
                {
                    blocked = c;
                    break;
                }
                clear = d;
            }
            return Mathf.Min(clear, max);
        }

        public static bool IsArtillery(AbilityDef ability) =>
            ability?.comps != null && ability.comps.Any(c => c is CompProperties_AbilityEntadArtillery);

        public static bool IsExplosive(ThingDef projectile) =>
            projectile != null && (projectile.projectile?.explosionRadius > 0f || typeof(Projectile_Explosive).IsAssignableFrom(projectile.thingClass));

        // Launches generation 'index' of the shot from 'origin' toward 'aim': a carrier while splits remain, the real
        // projectile after the last one
        public static void Fire(Map map, Thing launcher, Thing equipment, ThingDef projectile, Vector3 shooterPos, Vector3 origin, Vector3 aim,
            LocalTargetInfo intended, int index, int splits, List<float> distances, List<float> spread, int splitCount, ArtilleryVolley volley)
        {
            IntVec3 originCell = origin.ToIntVec3();
            if (!originCell.InBounds(map)) return;
            if (index >= splits)
            {
                // The last leg needs the wall check too. A real projectile ignores everything within 5 tiles of its
                // origin (VerbUtility.InterceptChanceFactorFromDistance), walls included, and this leg starts at the
                // last split point, so a shot scattered past a wall's edge would fly through it. Aimed at the first
                // wall on its line instead, it lands there and ImpactSomething hits the wall (fillPercent 1 gives a
                // 150% chance). Not for a shot from the shooter (the ability already checked its line, perhaps by
                // leaning past a corner), and not for a projectile that flies overhead: those arc over walls.
                bool fromSplit = (origin - shooterPos).MagnitudeHorizontalSquared() > 0.01f;
                bool overhead = projectile.projectile?.flyOverhead == true;
                // Lock-on (see CompProperties_AbilityEntadArtillery.lockChance): only for shots that split, so a short
                // single shot stays a plain cell-aimed shot
                Pawn locked = fromSplit && volley != null && Rand.Chance(volley.lockChance)
                    ? PickLockTarget(map, launcher, origin, aim, LockRadius(spread, index - 1) + volley.lockRadiusBonus, overhead, volley)
                    : null;
                if (locked != null)
                {
                    var lockedShot = (Projectile)GenSpawn.Spawn(projectile, originCell, map);
                    ApplyUniqueWeaponTraits(lockedShot, equipment);
                    lockedShot.Launch(launcher, origin, locked, locked, ProjectileHitFlags.All, false, equipment);
                    return;
                }
                if (fromSplit && !overhead)
                {
                    Vector3 leg = (aim - origin).Yto0();
                    float length = leg.MagnitudeHorizontal();
                    if (length > 0.2f)
                    {
                        ClearDistance(map, origin, leg / length, length, false, out IntVec3 wall);
                        if (wall.IsValid) aim = wall.ToVector3Shifted();
                    }
                }
                var real = (Projectile)GenSpawn.Spawn(projectile, originCell, map);
                ApplyUniqueWeaponTraits(real, equipment);
                real.Launch(launcher, origin, new LocalTargetInfo(aim.ToIntVec3()), intended, ProjectileHitFlags.All, false, equipment);
                return;
            }
            // The next split point lies on the way to the aim, where the shot is distances[index] from the shooter.
            // Measured along the flight from here, which is close enough: the scatter angles are small. It stays at
            // least a tile short of the aim, since a child's aim can be scattered back toward the shooter.
            Vector3 dir = (aim - origin).Yto0().normalized;
            float toAim = (aim - origin).MagnitudeHorizontal();
            float along = Mathf.Min(distances[index] - (origin - shooterPos).MagnitudeHorizontal(), toAim - 1f);
            along = ClearDistance(map, origin, dir, along, index == 0, out IntVec3 blocked);
            if (along < 0.5f)
            {
                // No room to fly on (a wall right ahead, or the aim too close): the remaining splits are lost and the
                // real projectile goes now. Aimed at the wall itself when one is in the way: launched this close, a
                // shot ignores everything on its path (InterceptChanceFactorFromDistance is 0 within 5 tiles of its
                // origin), so aimed past the wall it would land behind it.
                Vector3 to = blocked.IsValid ? blocked.ToVector3Shifted() : aim;
                Fire(map, launcher, equipment, projectile, shooterPos, origin, to, intended, splits, splits, distances, spread, splitCount, volley);
                return;
            }
            IntVec3 splitCell = (origin + dir * along).ToIntVec3();
            var carrier = (Projectile_EntadArtillery)GenSpawn.Spawn(EntadArtilleryDefOf.Entad_ArtilleryCarrier, originCell, map);
            // Launch first: it sets the carrier's equipment, which Setup reads for the unique weapon traits
            carrier.Launch(launcher, origin, new LocalTargetInfo(splitCell), intended, ProjectileHitFlags.None, false, equipment);
            carrier.Setup(projectile, shooterPos, aim, index, splits, distances, spread, splitCount, volley);
        }

        private static float LockRadius(List<float> spread, int split) =>
            spread != null && split >= 0 && split < spread.Count ? spread[split] : 1f;

        // A pawn near 'aim' for a final shot to lock onto, weighted hostile 1 / other faction pawns allyLockWeight,
        // skipping downed pawns, wild animals, pawns at the cap and (for a flat shot) pawns behind a wall from the
        // split point. Runs once per final shot that passed the lock roll, at most 27 per volley, over the cells of a
        // radius about 2.5 (around 20 cells): no per-tick cost.
        private static readonly List<Pawn> candidates = new List<Pawn>();
        private static Pawn PickLockTarget(Map map, Thing launcher, Vector3 origin, Vector3 aim, float radius, bool overhead, ArtilleryVolley volley)
        {
            candidates.Clear();
            IntVec3 center = aim.ToIntVec3();
            foreach (IntVec3 c in GenRadial.RadialCellsAround(center, radius, true))
            {
                if (!c.InBounds(map)) continue;
                List<Thing> things = c.GetThingList(map);
                for (int i = 0; i < things.Count; i++)
                {
                    if (!(things[i] is Pawn p) || p.Dead || p.Downed || p == launcher) continue;
                    if (volley.locks.TryGetValue(p, out int n) && n >= volley.maxLocksPerPawn) continue;
                    if (LockWeight(p, launcher, volley) <= 0f) continue;
                    if (!overhead)
                    {
                        Vector3 leg = (p.DrawPos - origin).Yto0();
                        float length = leg.MagnitudeHorizontal();
                        if (length > 0.2f)
                        {
                            ClearDistance(map, origin, leg / length, length, false, out IntVec3 wall);
                            if (wall.IsValid) continue;
                        }
                    }
                    candidates.Add(p);
                }
            }
            if (candidates.Count == 0) return null;
            Pawn pick = candidates.RandomElementByWeight(p => LockWeight(p, launcher, volley));
            volley.locks.TryGetValue(pick, out int had);
            volley.locks[pick] = had + 1;
            candidates.Clear();
            return pick;
        }

        private static float LockWeight(Pawn p, Thing launcher, ArtilleryVolley volley)
        {
            if (launcher != null && p.HostileTo(launcher)) return 1f;
            return p.Faction != null ? volley.allyLockWeight : 0f;
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
        private ArtilleryVolley volley;

        // Nine carriers split on the same tick at the second split; one shot sound is enough
        private static int lastSplitSoundTick = -1;

        public void Setup(ThingDef realProjectile, Vector3 shooterPos, Vector3 aim, int index, int splits,
            List<float> distances, List<float> spread, int splitCount, ArtilleryVolley volley)
        {
            this.volley = volley;
            this.realProjectile = realProjectile;
            this.shooterPos = shooterPos;
            this.aim = aim;
            this.index = index;
            this.splits = splits;
            this.distances = distances;
            this.spread = spread;
            this.splitCount = splitCount;
            CopyRealDamage();
        }

        // A shield reads the carrier's damage: DamageAmount for the energy it loses, DamageDef and ExtraDamages for
        // whether it is EMP (CompProjectileInterceptor.CheckIntercept). The carrier passes on the weapon's own: a
        // shield that stops one pays what one round of the weapon would cost it, and an EMP shot still breaks it.
        // Vanilla doesn't save damageDefOverride or extraDamages, so this runs again after a load.
        private void CopyRealDamage()
        {
            if (realProjectile?.projectile == null) return;
            damageDefOverride = realProjectile.projectile.damageDef;
            extraDamages = realProjectile.projectile.extraDamages != null
                ? new List<ExtraDamage>(realProjectile.projectile.extraDamages) : new List<ExtraDamage>();
            EntadArtillery.ApplyUniqueWeaponTraits(this, equipment);
        }

        // ExtraDamages already adds the carrier def's own (none) to the copied list, so only the amount needs this
        public override int DamageAmount => realProjectile?.projectile != null ? realProjectile.projectile.GetDamageAmount(equipment) : base.DamageAmount;

        public override float ArmorPenetration => realProjectile?.projectile != null ? realProjectile.projectile.GetArmorPenetration(equipment) : base.ArmorPenetration;

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
                    distances, spread, splitCount, volley);
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
            // Animated projectiles draw through their graphic class, as vanilla does (spinning is left out)
            if (realProjectile.projectile?.useGraphicClass == true) graphic.Draw(drawLoc, Rotation, this, ExactRotation.eulerAngles.y);
            else Graphics.DrawMesh(MeshPool.GridPlane(graphic.drawSize), drawLoc, ExactRotation, graphic.MatSingle, 0);
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
            Scribe_Deep.Look(ref volley, "volley");
            if (Scribe.mode == LoadSaveMode.PostLoadInit) CopyRealDamage();
        }
    }

    [DefOf]
    public static class EntadArtilleryDefOf
    {
        public static ThingDef Entad_ArtilleryCarrier;

        static EntadArtilleryDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(EntadArtilleryDefOf));
    }
}
