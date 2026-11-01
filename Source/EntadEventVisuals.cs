using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // What a one-off visual plays on. Hit: the item's weapon damage lands on something. Struck: its holder takes
    // damage that gets through. Heal: one of the holder's injuries heals. Dodge: the holder dodges a melee attack.
    // Trigger: one of the trait's triggers fires (after a cast's check passed, before its effects show).
    public enum EntadVisualEvent { Hit, Struck, Heal, Dodge, Trigger }

    // Where on the event it plays. Holder: the pawn (or turret) using the item. Other: the thing hit, the attacker,
    // the pawn killed, or the cell a trigger noted for it. Affected: each pawn a trigger's effects reached (the holder
    // for a trigger without a radius). Source: the furniture a Use trigger came from, else the holder.
    public enum EntadVisualSpot { Holder, Other, Affected, Source }

    // The event a visual plays on, filled by the hook that saw it. One shared instance: events are raised on the main
    // thread and a visual never raises another.
    public class EntadVisualContext
    {
        public Map map;
        public Thing holder;
        public Thing other;
        public IntVec3 otherCell = IntVec3.Invalid;
        // Where the other end is when it can't be read from a spawned thing (a blast cell)
        public Vector3? otherPos;
        public Thing source;
        public IList<Pawn> affected;
        public int trigger = -1;

        internal void Reset(Map map, Thing holder)
        {
            this.map = map;
            this.holder = holder;
            other = null;
            otherCell = IntVec3.Invalid;
            otherPos = null;
            source = null;
            affected = null;
            trigger = -1;
        }

        // Every visible spot of the kind: on the current map, in view, not fogged, and not an invisible pawn
        internal void Spots(EntadVisualSpot spot, List<(Vector3 pos, TargetInfo target)> into)
        {
            into.Clear();
            switch (spot)
            {
                case EntadVisualSpot.Holder: AddThing(holder, into); break;
                case EntadVisualSpot.Source: AddThing(source ?? holder, into); break;
                case EntadVisualSpot.Other:
                    if (otherPos.HasValue) AddPos(otherPos.Value, otherCell.IsValid ? new TargetInfo(otherCell, map) : new TargetInfo(otherPos.Value.ToIntVec3(), map), into);
                    else if (other != null && other.Spawned && other.Map == map) AddThing(other, into);
                    else if (otherCell.IsValid) AddPos(otherCell.ToVector3Shifted(), new TargetInfo(otherCell, map), into);
                    break;
                case EntadVisualSpot.Affected:
                    if (affected == null) AddThing(holder, into);
                    else for (int i = 0; i < affected.Count; i++) AddThing(affected[i], into);
                    break;
            }
        }

        private void AddThing(Thing thing, List<(Vector3, TargetInfo)> into)
        {
            if (thing == null || !thing.Spawned || thing.Map != map) return;
            if (thing is Pawn pawn && pawn.IsHiddenFromPlayer()) return;
            AddPos(thing.DrawPos, new TargetInfo(thing), into);
        }

        private void AddPos(Vector3 pos, TargetInfo target, List<(Vector3, TargetInfo)> into)
        {
            IntVec3 cell = pos.ToIntVec3();
            if (!cell.ShouldSpawnMotesAt(map, false) || cell.Fogged(map)) return;
            into.Add((pos, target));
        }
    }

    // A visual played once when something happens to the item or its holder, as against the ones drawn all the time.
    // Spawned on game ticks inside the event, never per frame, so nothing piles up while paused.
    public abstract class EntadEventVisual : EntadVisual
    {
        public EntadVisualEvent on;
        public EntadVisualSpot at = EntadVisualSpot.Holder;
        // For on Trigger: only that trigger of the trait (its index in triggers); -1 for any
        public int trigger = -1;
        public float chance = 1f;
        // At most once per this many ticks for the item, so a minigun or a hail of hits doesn't stack a hundred flashes
        public int minIntervalTicks;
        // Only while the holder is bleeding after the event (Clotting's pulse on a hit that opened a wound)
        public bool whileBleeding;

        private static int nextId;
        [Unsaved] private int id;
        internal int Id => id != 0 ? id : (id = ++nextId);

        internal static int Bit(EntadVisualEvent ev) => 1 << (int)ev;

        protected static readonly List<(Vector3 pos, TargetInfo target)> spotsA = new List<(Vector3, TargetInfo)>();
        protected static readonly List<(Vector3 pos, TargetInfo target)> spotsB = new List<(Vector3, TargetInfo)>();

        public abstract void Play(EntadVisualContext ctx);

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            if (trigger >= 0 && on != EntadVisualEvent.Trigger) yield return $"{defName}: a visual's trigger index only applies with on Trigger";
            if ((at == EntadVisualSpot.Affected || at == EntadVisualSpot.Source) && on != EntadVisualEvent.Trigger)
                yield return $"{defName}: visual spot {at} only exists on Trigger events";
        }

        protected static void Throw(FleckDef fleck, Vector3 pos, Map map, float scale, float angle, float speed, float rotationRate, Color color, float? rotation = null)
        {
            var data = FleckMaker.GetDataStatic(pos, map, fleck, scale);
            data.velocityAngle = angle;
            data.velocitySpeed = speed;
            data.rotationRate = rotationRate;
            data.rotation = rotation ?? Rand.Range(0f, 360f);
            if (color != Color.white) data.instanceColor = color;
            map.flecks.CreateFleck(data);
        }
    }

    // A burst of flecks at the spot: a burn flash, sparks, a healing pulse
    public class EntadVisual_EventFlecks : EntadEventVisual
    {
        public FleckDef fleck;
        public IntRange count = new IntRange(1, 1);
        public FloatRange scale = new FloatRange(0.5f, 0.7f);
        // Direction (0 = up the screen) and speed in cells per second
        public FloatRange angle = new FloatRange(0f, 360f);
        public FloatRange speed = FloatRange.Zero;
        public FloatRange rotationRate = FloatRange.Zero;
        // Starting rotation; narrow it for a sprite with an up, like a flame
        public FloatRange rotation = new FloatRange(0f, 360f);
        // Radius around the spot the flecks start in
        public float spread;
        public Color color = Color.white;

        public override void Play(EntadVisualContext ctx)
        {
            ctx.Spots(at, spotsA);
            foreach (var (pos, _) in spotsA)
            {
                int n = count.RandomInRange;
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = spread > 0f ? pos + Rand.InsideUnitCircleVec3 * spread : pos;
                    Throw(fleck, p, ctx.map, scale.RandomInRange, angle.RandomInRange, speed.RandomInRange, rotationRate.RandomInRange, color, rotation.RandomInRange);
                }
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (fleck == null) yield return $"{defName}: event flecks visual needs a fleck";
        }
    }

    // A vanilla effecter at the spot, triggered once (the deflection sparks armor throws). Instant effecters only:
    // it is cleaned up straight away, so one that sustains or ticks shows nothing.
    public class EntadVisual_EventEffecter : EntadEventVisual
    {
        public EffecterDef effecter;
        // The effecter's second target, which aims sprays and lines (deflect sparks fly away from it)
        public EntadVisualSpot toward = EntadVisualSpot.Other;

        public override void Play(EntadVisualContext ctx)
        {
            ctx.Spots(at, spotsA);
            if (spotsA.Count == 0) return;
            ctx.Spots(toward, spotsB);
            foreach (var (_, target) in spotsA)
            {
                var e = effecter.Spawn();
                e.Trigger(target, spotsB.Count > 0 ? spotsB[0].target : target);
                e.Cleanup();
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (effecter == null) yield return $"{defName}: event effecter visual needs an effecter";
        }
    }

    // Lightning zaps in the arc sprites the Shocking weapon crackles with: around the spot, or, with 'to', from the
    // spot to each 'to' spot as a jagged bolt (Thunderclap's strike from the corpse to everyone it stunned)
    public class EntadVisual_EventArcs : EntadEventVisual
    {
        [NoTranslate] public string texFolder = "Things/Mote/EntadArc";
        public Color arcColor = new Color(0.7f, 0.85f, 1f, 0.9f);
        public IntRange count = new IntRange(2, 3);
        public FloatRange reach = new FloatRange(0.4f, 0.9f);
        public float width = 0.18f;
        public int arcTicks = 6;
        public EntadVisualSpot? to;
        // Pieces of a bolt to a 'to' spot, each bent sideways by up to jag cells
        public int segments = 3;
        public float jag = 0.3f;
        // Spark at the far end of each zap
        public FleckDef fleck;
        public FloatRange fleckScale = new FloatRange(0.1f, 0.16f);

        [Unsaved] private List<Material> materials;

        public override void Play(EntadVisualContext ctx)
        {
            var mats = materials ??= EntadVisuals.LoadFrames(texFolder, ShaderDatabase.MoteGlow, arcColor, "arc");
            if (mats.Count == 0) return;
            ctx.Spots(at, spotsA);
            if (spotsA.Count == 0) return;
            if (to.HasValue)
            {
                ctx.Spots(to.Value, spotsB);
                foreach (var (a, _) in spotsA)
                    foreach (var (b, _) in spotsB)
                        Bolt(ctx.map, mats, a, b);
                return;
            }
            foreach (var (pos, _) in spotsA)
            {
                int n = count.RandomInRange;
                for (int i = 0; i < n; i++)
                {
                    Vector3 a = pos + Rand.InsideUnitCircleVec3 * 0.15f;
                    Vector3 b = a + Quaternion.AngleAxis(Rand.Range(0f, 360f), Vector3.up) * new Vector3(reach.RandomInRange, 0f, 0f);
                    EntadVisuals.AddZap(ctx.map, a, b, mats.RandomElement(), width, arcTicks);
                    Spark(ctx.map, b);
                }
            }
        }

        private void Bolt(Map map, List<Material> mats, Vector3 a, Vector3 b)
        {
            int n = Mathf.Max(1, segments);
            Vector3 dir = b - a;
            Vector3 side = new Vector3(-dir.z, 0f, dir.x).normalized;
            Vector3 last = a;
            for (int i = 1; i <= n; i++)
            {
                Vector3 next = i == n ? b : Vector3.Lerp(a, b, i / (float)n) + side * Rand.Range(-jag, jag);
                EntadVisuals.AddZap(map, last, next, mats.RandomElement(), width, arcTicks);
                last = next;
            }
            Spark(map, b);
        }

        private void Spark(Map map, Vector3 pos)
        {
            if (fleck != null) Throw(fleck, pos, map, fleckScale.RandomInRange, Rand.Range(0f, 360f), Rand.Range(0.2f, 0.6f), 0f, Color.white);
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (arcTicks < 1) yield return $"{defName}: event arcs arcTicks must be at least 1";
            if (to.HasValue && (to == EntadVisualSpot.Affected || to == EntadVisualSpot.Source) && on != EntadVisualEvent.Trigger)
                yield return $"{defName}: visual spot {to} only exists on Trigger events";
        }
    }

    // Flecks flowing from one spot to another, arriving as they fade: life drunk from a corpse into the wielder, or
    // madness poured from the wielder into its victim. 'at' isn't used; from and to say which way it runs.
    public class EntadVisual_EventStream : EntadEventVisual
    {
        public FleckDef fleck;
        public EntadVisualSpot from = EntadVisualSpot.Other;
        public EntadVisualSpot to = EntadVisualSpot.Holder;
        public IntRange count = new IntRange(10, 14);
        // FireGlow's bright core is a small part of the sprite: much under 0.4 and a wisp flying a tile is lost
        public FloatRange scale = new FloatRange(0.45f, 0.65f);
        public Color color = Color.white;
        // How far along the path the flecks start (0 = all at the source), so they arrive one after another
        public float lead = 0.5f;
        // Start spots scattered this far around the line
        public float spread = 0.2f;
        // When each arrives, as a fraction of its fleck's fade-in plus solid time; the rest of its life it fades out
        // at the far end
        public FloatRange arrive = new FloatRange(0.6f, 1f);

        public override void Play(EntadVisualContext ctx)
        {
            ctx.Spots(from, spotsA);
            if (spotsA.Count == 0) return;
            ctx.Spots(to, spotsB);
            float travel = Mathf.Max(0.05f, fleck.fadeInTime + fleck.solidTime);
            foreach (var (a, _) in spotsA)
                foreach (var (b, _) in spotsB)
                {
                    if ((b - a).MagnitudeHorizontalSquared() < 0.09f) continue;
                    int n = count.RandomInRange;
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 start = Vector3.Lerp(a, b, Rand.Range(0f, lead)) + Rand.InsideUnitCircleVec3 * spread;
                        Vector3 path = b - start;
                        float dist = Mathf.Sqrt(path.MagnitudeHorizontalSquared());
                        Throw(fleck, start, ctx.map, scale.RandomInRange, path.AngleFlat(), dist / (travel * arrive.RandomInRange), 0f, color);
                    }
                }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (fleck == null) yield return $"{defName}: event stream visual needs a fleck";
            if ((from == EntadVisualSpot.Affected || from == EntadVisualSpot.Source || to == EntadVisualSpot.Affected || to == EntadVisualSpot.Source)
                && on != EntadVisualEvent.Trigger)
                yield return $"{defName}: stream spots Affected and Source only exist on Trigger events";
        }
    }

    // Faded copies of the pawn sliding off it (EntadAfterimages.cs): a dodge that leaves the blow hitting an image.
    // Plays on the pawn at the spot, moving away from the other spot.
    public class EntadVisual_EventAfterimage : EntadEventVisual
    {
        public int count = 3;
        public FloatRange distance = new FloatRange(0.8f, 1.1f);
        public int lifeTicks = 30;
        public Color color = new Color(0.8f, 0.88f, 1f, 0.55f);
        // East or west only, away from the other spot when it is to one side, else either: a copy sliding north or
        // south is drawn over or under the pawn and barely shows
        public bool horizontal = true;
        // Without horizontal: off to one side of the line from the other spot, as a dodge would; false = straight away
        public bool sideways = true;

        public override void Play(EntadVisualContext ctx)
        {
            ctx.Spots(at, spotsA);
            if (spotsA.Count == 0) return;
            ctx.Spots(at == EntadVisualSpot.Other ? EntadVisualSpot.Holder : EntadVisualSpot.Other, spotsB);
            foreach (var (pos, target) in spotsA)
            {
                if (!(target.Thing is Pawn pawn)) continue;
                Vector3 dir = spotsB.Count > 0 ? pos - spotsB[0].pos : Vector3.zero;
                dir.y = 0f;
                if (horizontal)
                    dir = new Vector3(dir.x > 0.3f ? 1f : dir.x < -0.3f ? -1f : Rand.Bool ? 1f : -1f, 0f, 0f);
                else
                {
                    dir = dir.sqrMagnitude > 0.0001f ? dir.normalized : Quaternion.AngleAxis(Rand.Range(0f, 360f), Vector3.up) * Vector3.forward;
                    if (sideways) dir = new Vector3(-dir.z, 0f, dir.x) * (Rand.Bool ? 1f : -1f);
                }
                float d = distance.RandomInRange;
                for (int i = 0; i < count; i++)
                {
                    float share = (i + 1) / (float)count;
                    Color c = color;
                    c.a *= 1f - i / (float)(count + 1);
                    EntadAfterimages.Add(pawn, pos, dir * (d * share / Mathf.Max(1, lifeTicks)), lifeTicks, c);
                }
            }
        }

        public override IEnumerable<string> ConfigErrors(string defName)
        {
            foreach (var e in base.ConfigErrors(defName)) yield return e;
            if (count < 1 || lifeTicks < 1) yield return $"{defName}: event afterimage needs count and lifeTicks of at least 1";
        }
    }

    public static partial class EntadVisuals
    {
        public static bool EventsOn => EntadSettings.Visuals && EntadSettings.VisualsEvents;

        private static readonly EntadVisualContext eventContext = new EntadVisualContext();
        // Last tick each (item or holder, entry) played, for minIntervalTicks. Cleared when large: that lets one
        // flash through early, nothing worse.
        private static readonly Dictionary<long, int> lastEventTick = new Dictionary<long, int>();
        // Hits a blast has flashed so far, by the explosion's id: a grenade in a crowd flashes on the first few only
        private static readonly Dictionary<int, int> blastHits = new Dictionary<int, int>();
        private const int MaxBlastHits = 6;

        private static bool On(EntadVisualEvent ev) => EventsOn && EntadTraitDef.AnyEventVisuals(ev);

        internal static bool HasHitVisuals(CompEntad comp) =>
            On(EntadVisualEvent.Hit) && (comp.Visuals.eventMask & EntadEventVisual.Bit(EntadVisualEvent.Hit)) != 0;

        // A weapon's hit, before its damage is dealt (a killing blow destroys the pawn, and with it where it stood).
        // comp is from EntadWeaponDamage.CompOf, so it already works for the user.
        public static void WeaponHit(CompEntad comp, Thing user, Thing target)
        {
            if (comp == null || target == null || !target.Spawned || target.Map != Find.CurrentMap || !HasHitVisuals(comp)) return;
            var ctx = eventContext;
            ctx.Reset(target.Map, user);
            ctx.other = target;
            Play(comp.parent.thingIDNumber, comp.Visuals.events, EntadVisualEvent.Hit, ctx, null);
        }

        // A thing an entad weapon's blast damaged, after the damage (it may be dead and gone, so the cell stands in)
        public static void BlastHit(Explosion explosion, CompEntad comp, Thing target, IntVec3 cell)
        {
            Map map = explosion.Map;
            if (map == null || map != Find.CurrentMap || !HasHitVisuals(comp)) return;
            blastHits.TryGetValue(explosion.thingIDNumber, out int hits);
            if (hits >= MaxBlastHits) return;
            if (blastHits.Count > 64) blastHits.Clear();
            blastHits[explosion.thingIDNumber] = hits + 1;
            var ctx = eventContext;
            ctx.Reset(map, explosion.instigator);
            ctx.other = target;
            ctx.otherCell = cell;
            if (!target.Spawned) ctx.otherPos = cell.ToVector3Shifted();
            Play(comp.parent.thingIDNumber, comp.Visuals.events, EntadVisualEvent.Hit, ctx, null);
        }

        // Struck, Heal and Dodge: every worn or wielded entad of the pawn that works for it
        public static void FromGear(Pawn pawn, EntadVisualEvent ev, Thing other)
        {
            if (!On(ev) || !pawn.Spawned || pawn.Map != Find.CurrentMap) return;
            int bit = EntadEventVisual.Bit(ev);
            var eq = pawn.equipment?.AllEquipmentListForReading;
            if (eq != null) for (int i = 0; i < eq.Count; i++) FromItem(pawn, eq[i], ev, bit, other);
            var ap = pawn.apparel?.WornApparel;
            if (ap != null) for (int i = 0; i < ap.Count; i++) FromItem(pawn, ap[i], ev, bit, other);
        }

        private static void FromItem(Pawn pawn, Thing item, EntadVisualEvent ev, int bit, Thing other)
        {
            var comp = item.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0) return;
            var set = comp.Visuals;
            if ((set.eventMask & bit) == 0 || !comp.ActiveFor(pawn)) return;
            var ctx = eventContext;
            ctx.Reset(pawn.Map, pawn);
            ctx.other = other;
            Play(item.thingIDNumber, set.events, ev, ctx, null);
        }

        // From EntadTriggers.Apply once the trigger's checks passed. The trigger names its trait but not the item, so
        // the trait's own list is read, with the same identification rule as an item's set.
        public static void Triggered(EntadTriggerQueue.Pending p, IList<Pawn> affected)
        {
            var visuals = p.trait.def.visuals;
            Pawn pawn = p.pawn;
            if (visuals == null || !On(EntadVisualEvent.Trigger) || !pawn.Spawned || pawn.Map != Find.CurrentMap) return;
            var ctx = eventContext;
            ctx.Reset(pawn.Map, pawn);
            ctx.other = p.other;
            ctx.otherCell = p.otherCell;
            ctx.source = p.source;
            ctx.affected = affected;
            ctx.trigger = p.index;
            Play(Gen.HashCombineInt(pawn.thingIDNumber, p.trait.def.shortHash), visuals, EntadVisualEvent.Trigger, ctx, p.trait);
        }

        // trait is given when the list is a trait's own, whose hidden entries an item's set hasn't already filtered out
        private static void Play(int owner, IList<EntadVisual> visuals, EntadVisualEvent ev, EntadVisualContext ctx, AppliedEntadTrait trait)
        {
            bool hidden = trait != null && trait.AnyHidden;
            int now = Find.TickManager.TicksGame;
            for (int i = 0; i < visuals.Count; i++)
            {
                if (!(visuals[i] is EntadEventVisual v) || v.on != ev) continue;
                if (hidden && !(v.beforeIdentified ?? EntadSettings.VisualsUnidentified)) continue;
                if (v.trigger >= 0 && v.trigger != ctx.trigger) continue;
                if (v.whileBleeding && !(ctx.holder is Pawn p && p.health.hediffSet.BleedRateTotal > 0.0001f)) continue;
                long key = ((long)owner << 20) ^ v.Id;
                if (v.minIntervalTicks > 0)
                {
                    if (lastEventTick.TryGetValue(key, out int last) && now - last < v.minIntervalTicks) continue;
                    if (lastEventTick.Count > 2048) lastEventTick.Clear();
                    lastEventTick[key] = now;
                }
                // Seeded, so what is on screen doesn't move the game's random stream
                Rand.PushState(Gen.HashCombineInt((int)key, now));
                try
                {
                    if (v.chance >= 1f || Rand.Chance(v.chance))
                    {
                        v.Play(ctx);
                        if (EntadProcs.LogTriggers && ev == EntadVisualEvent.Trigger) LogPlayed(v, ctx);
                    }
                }
                catch (System.Exception e)
                {
                    Log.ErrorOnce($"[Entad Framework] {ev} visual {v.GetType().Name} failed: {e}", Gen.HashCombineInt(v.Id, 0x5EE7));
                }
                finally { Rand.PopState(); }
            }
        }

        // With 'Log Entad triggers' on: where a trigger visual found its spots, for one that fired and showed nothing
        private static void LogPlayed(EntadEventVisual v, EntadVisualContext ctx)
        {
            var spots = new List<(Vector3 pos, TargetInfo target)>();
            ctx.Spots(EntadVisualSpot.Holder, spots);
            int holder = spots.Count;
            ctx.Spots(EntadVisualSpot.Other, spots);
            Log.Message($"[Entad Framework] {Find.TickManager.TicksGame}: trigger visual {v.GetType().Name} played for {ctx.holder?.LabelShort}: "
                + $"holder spots {holder}, other spots {spots.Count} (other {ctx.other?.LabelShort ?? "none"}, cell {ctx.otherCell})");
        }
    }

    // Shots that hit a thing. Bullet.Impact calls this base first; explosive shells override it and are covered by
    // the blast hook instead. A prefix: the damage after it can destroy what was hit.
    [HarmonyPatch(typeof(Projectile), "Impact")]
    public static class Patch_ProjectileImpact_HitVisuals
    {
        public static void Prefix(Thing hitThing, Thing ___equipment, Thing ___launcher)
        {
            if (hitThing == null || ___equipment == null || !EntadVisuals.EventsOn || !EntadTraitDef.AnyEventVisuals(EntadVisualEvent.Hit)) return;
            EntadVisuals.WeaponHit(EntadWeaponDamage.CompOf(___equipment, ___launcher), ___launcher, hitThing);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Pawn_PostApplyDamage_StruckVisuals
    {
        public static void Postfix(Pawn __instance, DamageInfo dinfo, float totalDamageDealt)
        {
            if (totalDamageDealt > 0f && !__instance.Dead && IsBlow(dinfo, __instance))
                EntadVisuals.FromGear(__instance, EntadVisualEvent.Struck, dinfo.Instigator);
        }

        // Something hit the pawn. Heatstroke burns and roof collapses have no instigator; the flames on a burning pawn
        // name whoever lit them (or the fire) but carry no weapon. Neither do flame blasts without a weapon (a mech's
        // flame spewer, a pyrophage's fire spew), which lose their visual with them: DamageInfo turns fire's -1 angle
        // into a random one, so nothing on it tells the two apart. Surgery and execution cuts come from a doctor and
        // count as violence to vanilla, being cuts.
        private static bool IsBlow(DamageInfo dinfo, Pawn pawn)
        {
            if (dinfo.Instigator == null || !dinfo.Def.ExternalViolenceFor(pawn)) return false;
            if (dinfo.Def == DamageDefOf.Flame && dinfo.Weapon == null) return false;
            return dinfo.Def != DamageDefOf.SurgicalCut && dinfo.Def != DamageDefOf.ExecutionCut;
        }
    }

    // Natural healing, tended healing and every other heal. A pawn can heal several injuries in one tick, so a heal
    // visual wants a minIntervalTicks.
    [HarmonyPatch(typeof(Hediff_Injury), nameof(Hediff_Injury.Heal))]
    public static class Patch_InjuryHeal_Visuals
    {
        public static void Postfix(Hediff_Injury __instance, float amount)
        {
            if (amount > 0f && __instance.pawn != null) EntadVisuals.FromGear(__instance.pawn, EntadVisualEvent.Heal, null);
        }
    }

    // Vanilla picks the dodge sound only in the branch where the target dodged. Combat Extended's melee verb has its
    // own dodge and parry handling and never calls this.
    [HarmonyPatch(typeof(Verb_MeleeAttack), "SoundDodge")]
    public static class Patch_MeleeDodge_Visuals
    {
        public static void Postfix(Verb_MeleeAttack __instance, Thing target)
        {
            if (target is Pawn pawn) EntadVisuals.FromGear(pawn, EntadVisualEvent.Dodge, __instance.CasterPawn);
        }
    }
}
