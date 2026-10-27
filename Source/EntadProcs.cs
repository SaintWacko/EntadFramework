using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using System.Linq;
using RimWorld;
using Verse;
using Verse.Sound;

namespace EntadFramework
{
    // One cast of a trigger, as handed to whatever casts it (here for an AbilityDef, a module for an externalAbility)
    public class EntadProcContext
    {
        public Pawn caster;
        public CompEntad item;
        public AppliedEntadTrait trait;
        public EntadTriggerCast cast;
        public LocalTargetInfo target;
        public object state;   // a module's own: what its canCast worked out, for its cast

        internal EntadProcAbility vanilla;
    }

    // The throwaway ability a vanilla or mod AbilityDef is cast through. A psycast loses its Psycast class with it, so
    // no psylink, psyfocus or neural heat is asked for; what Psycast does besides that is kept: a psychically deaf pawn,
    // or a mechanoid where an effect isn't for mechs, is immune to a psychic effect (each target of an area one), and a
    // psycast plays its psychic pulse.
    public class EntadProcAbility : Ability
    {
        public EntadProcAbility(Pawn pawn, AbilityDef def) : base(pawn, def) { }

        private bool IsPsycast => def.abilityClass != null && typeof(Psycast).IsAssignableFrom(def.abilityClass);

        public bool CanAffect(LocalTargetInfo target)
        {
            var comps = EffectComps;
            if (comps == null || !comps.Any(c => c.Props.psychic)) return true;
            Pawn p = target.Pawn;
            if (p == null) return true;
            if (p.GetStatValue(StatDefOf.PsychicSensitivity) < float.Epsilon) return false;
            return p.Faction == null || p.Faction != Faction.OfMechanoids || comps.All(c => c.Props.applicableToMechs);
        }

        public override bool Activate(LocalTargetInfo target, LocalTargetInfo dest)
        {
            if (IsPsycast && def.showPsycastEffects && EffectComps != null)
            {
                if (EffectComps.Any(c => c.Props.psychic))
                {
                    if (def.HasAreaOfEffect)
                    {
                        FleckMaker.Static(target.Cell, pawn.Map, FleckDefOf.PsycastAreaEffect, def.EffectRadius);
                        SoundDefOf.PsycastPsychicPulse.PlayOneShot(new TargetInfo(target.Cell, pawn.Map));
                    }
                    else SoundDefOf.PsycastPsychicEffect.PlayOneShot(new TargetInfo(target.Cell, pawn.Map));
                }
                else if (def.HasAreaOfEffect && def.canUseAoeToGetTargets)
                    SoundDefOf.Psycast_Skip_Pulse.PlayOneShot(new TargetInfo(target.Cell, pawn.Map));
            }
            return base.Activate(target, dest);
        }

        protected override void ApplyEffects(IEnumerable<CompAbilityEffect> effects, LocalTargetInfo target, LocalTargetInfo dest)
        {
            if (CanAffect(target)) base.ApplyEffects(effects, target, dest);
            else MoteMaker.ThrowText(target.CenterVector3, pawn.Map, "TextMote_Immune".Translate());
        }
    }

    // Abilities cast by triggers ("procs"). A vanilla or mod AbilityDef is cast through an EntadProcAbility made for the
    // holder and never added to their tracker, so the holder's own abilities and cooldowns are untouched (an ability in a
    // cooldown group would put the group's on cooldown: EntadTriggerCast.ConfigErrors flags those). Ability.Activate
    // applies the effect comps at once, as the end of a cast would, with no job, warmup or range check.
    public static class EntadProcs
    {
        // Ability keeps the effecters it maintains (a skip's entry and exit flashes) in a private list that only its own
        // tick advances, and the throwaway abilities are in nobody's tracker. Looked up as plain reflection so a renamed
        // member only loses the flashes.
        private static readonly MethodInfo EffectersTick = AccessTools.Method(typeof(Ability), "EffectersTick");
        private static readonly FieldInfo MaintainedEffecters = AccessTools.Field(typeof(Ability), "maintainedEffecters");
        private static readonly System.Action<Ability> tickEffecters =
            EffectersTick != null ? AccessTools.MethodDelegate<System.Action<Ability>>(EffectersTick) : null;

        // Ticked until their effecters run out; vanilla maintains them for 60 ticks at most, the limit is a backstop
        private const int MaintainTicks = 600;
        private static readonly List<Ability> maintaining = new List<Ability>();
        private static readonly List<int> maintainUntil = new List<int>();

        internal static void Clear()
        {
            maintaining.Clear();
            maintainUntil.Clear();
        }

        internal static bool TryPrepare(EntadTrigger t, EntadTriggerQueue.Pending p, out List<EntadProcContext> prepared)
        {
            prepared = null;
            Pawn caster = p.pawn;
            // Carried, in a caravan or a transporter: nowhere on a map to cast from
            if (!caster.Spawned) return false;
            var list = new List<EntadProcContext>(t.casts.Count);
            for (int i = 0; i < t.casts.Count; i++)
            {
                var cast = t.casts[i];
                var ctx = new EntadProcContext { caster = caster, item = p.trait.owner, trait = p.trait, cast = cast };
                bool ok = TryTarget(cast, p, out ctx.target)
                    && (cast.IsExternal ? EntadCompat.CanCastExternal(ctx) : CanCastVanilla(ctx));
                if (ok) list.Add(ctx);
                else if (!cast.optional) return false;
            }
            if (list.Count == 0) return false;
            prepared = list;
            return true;
        }

        internal static void Fire(List<EntadProcContext> prepared)
        {
            for (int i = 0; i < prepared.Count; i++)
            {
                var ctx = prepared[i];
                // An earlier cast may have moved, downed or killed the caster
                if (!ctx.caster.Spawned || ctx.caster.Dead) return;
                if (ctx.cast.IsExternal) EntadCompat.CastExternal(ctx);
                else CastVanilla(ctx);
            }
        }

        private static bool TryTarget(EntadTriggerCast cast, EntadTriggerQueue.Pending p, out LocalTargetInfo target)
        {
            target = LocalTargetInfo.Invalid;
            Pawn caster = p.pawn;
            Map map = caster.Map;
            Thing other = p.other;
            bool otherHere = other != null && other.Spawned && other.Map == map && !(other is Pawn op && op.Dead);
            switch (cast.target)
            {
                case EntadCastTarget.Self:
                    target = caster;
                    break;
                case EntadCastTarget.Attacker:
                case EntadCastTarget.Victim:
                    if (otherHere) target = other;
                    break;
                case EntadCastTarget.VictimCell:
                    target = p.otherCell;
                    break;
                case EntadCastTarget.Between:
                {
                    IntVec3 far = otherHere ? other.Position : p.otherCell;
                    if (far.IsValid) target = new IntVec3((caster.Position.x + far.x) / 2, 0, (caster.Position.z + far.z) / 2);
                    break;
                }
                case EntadCastTarget.Origin:
                    target = p.origin;
                    break;
            }
            return target.IsValid && target.Cell.InBounds(map);
        }

        // What the ability itself would refuse, without its range and line of sight: the kinds of thing it can target
        // (a pawn it can't target falls back to the pawn's cell when it can target cells), and every effect comp's own
        // check (psychically deaf, a boss, no room to teleport to)
        private static bool CanCastVanilla(EntadProcContext ctx)
        {
            var def = ctx.cast.ability;
            var tp = def.verbProperties?.targetParams;
            var target = ctx.target;
            if (tp != null)
            {
                if (target.HasThing && !tp.CanTarget(target.Thing))
                {
                    if (!tp.canTargetLocations) return false;
                    target = target.Cell;
                }
                else if (!target.HasThing && !tp.canTargetLocations) return false;
            }
            // A mod's ability whose comps throw here would otherwise throw on every event once its cooldown is back
            try
            {
                var ability = new EntadProcAbility(ctx.caster, def);
                // EffectComps first: Ability.CanApplyOn reads the comp list that getter fills
                var comps = ability.EffectComps;
                if (!ability.CanApplyOn(target) || !ability.CanAffect(target)) return false;
                if (comps != null)
                    for (int i = 0; i < comps.Count; i++)
                        if (!comps[i].CanApplyOn(target, LocalTargetInfo.Invalid) || !comps[i].Valid(target, false)) return false;
                ctx.target = target;
                ctx.vanilla = ability;
                return true;
            }
            catch (System.Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] checking {def.defName} for {ctx.item?.parent} failed: {e}", def.shortHash ^ 0x5eec);
                return false;
            }
        }

        private static void CastVanilla(EntadProcContext ctx)
        {
            var ability = ctx.vanilla;
            // A thing target that has gone since the check (killed by an earlier cast) is skipped rather than aimed at
            // a stale reference
            if (ctx.target.HasThing && !ctx.target.Thing.Spawned) return;
            try { ability.Activate(ctx.target, LocalTargetInfo.Invalid); }
            catch (System.Exception e)
            {
                Log.ErrorOnce($"[Entad Framework] casting {ability.def.defName} for {ctx.item?.parent} failed: {e}", ability.def.shortHash ^ 0x5eed);
                return;
            }
            if (tickEffecters != null && MaintainedEffecters?.GetValue(ability) is System.Collections.ICollection effecters && effecters.Count > 0)
            {
                maintaining.Add(ability);
                maintainUntil.Add(Find.TickManager.TicksGame + MaintainTicks);
            }
        }

        // From EntadTriggerQueue's tick: one count check while nothing is maintained
        internal static void Tick()
        {
            if (maintaining.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = maintaining.Count - 1; i >= 0; i--)
            {
                var ability = maintaining[i];
                if (now <= maintainUntil[i] && MaintainedEffecters.GetValue(ability) is System.Collections.ICollection effecters && effecters.Count > 0)
                {
                    try { tickEffecters(ability); continue; }
                    catch (System.Exception e) { Log.ErrorOnce($"[Entad Framework] effecters of {ability.def.defName} failed: {e}", ability.def.shortHash ^ 0x5eef); }
                }
                maintaining.RemoveAt(i);
                maintainUntil.RemoveAt(i);
            }
        }
    }
}
