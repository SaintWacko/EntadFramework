using System;
using System.Runtime.CompilerServices;
using AbilityAI;
using RimWorld;
using Verse;

namespace EntadFramework.AbilityAICompat
{
    // Entad's side of the Ability AI mod (saintwacko.AbilityAI). This assembly lives in Mods/AbilityAI/, which
    // LoadFolders.xml loads only when Ability AI is active, so it is the one place allowed to name Ability AI types.
    // The main EntadFramework.dll must not: a type there with a base, interface or field from AbilityAI.dll would make
    // RimWorld skip the whole assembly when Ability AI is missing (ReflectionTypeLoadException).
    //
    // Ability AI knows nothing about entads; the hook tells it which abilities are ours to cast.
    // Claim: gear the pawn wears or wields grants the ability through a trait that is active for it, which already
    // covers binding (EntadAbilities.Grants checks ActiveFor, so an item bound to someone else grants nothing).
    // Anything else gets no opinion: a raider's own psycasts are left to Ability AI's "abilities no mod claims" setting.
    // Veto: an ability whose trait is still hidden, on the player's own pawns. For them the veto is what keeps the
    // autocast menu and badge off the gizmo, which would otherwise give an unidentified ability away. NPCs still cast
    // hidden abilities; that is how the player learns what a raider's gear does.
    //
    // Reveal needs nothing here: Patch_Ability_Activate_Reveal in the main assembly reveals the granting trait when
    // any pawn's cast resolves, Ability AI's included, so a raider's cast reveals the trait on the item it drops.
    public class EntadAbilityAIHook : IAbilityAIHook
    {
        public static readonly EntadAbilityAIHook Instance = new EntadAbilityAIHook();

        public AbilityAIVote Vote(Pawn pawn, Ability ability)
        {
            if (!EntadAbilities.Grants(pawn, ability.def, out bool hidden)) return AbilityAIVote.NoOpinion;
            return hidden && pawn.Faction != null && pawn.Faction.IsPlayer ? AbilityAIVote.Veto : AbilityAIVote.Claim;
        }

        public void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest) { }
    }

    [StaticConstructorOnStartup]
    internal static class EntadAbilityAIStartup
    {
        static EntadAbilityAIStartup()
        {
            AbilityAIHooks.Register(EntadAbilityAIHook.Instance);
            // Ability AI caches these answers per comp properties object. Entad never changes a pulse comp after
            // startup (the per-trait ability copies share the source's comps list), so the cache stays right.
            AbilityAIInfo.Register<CompProperties_AbilityEntadPulse>(p => p.radius, p => p.hediff, p => p.stunSeconds > 0f);
            // Ability AI's AssemblyVersion doesn't change between releases, so an older copy without these loads
            // fine and only fails when the calls are compiled. In their own method so that failure lands in the catch.
            try { RegisterHeld(); }
            catch (Exception e) when (e is MissingMethodException || e is TypeLoadException || e is MissingMemberException)
            {
                Log.Warning("[Entad Framework] This Ability AI is older than the one Entad was built for, so NPCs won't cast abilities from Entad items that use another ability system (Vanilla Psycasts Expanded). Updating Ability AI fixes this.");
            }
        }

        // Abilities kept on items (the Vanilla Psycasts Expanded module's), which no tracker of the pawn's lists.
        // Same rule as the vanilla hook: Claim what the gear grants, Veto it on the player's pawns while hidden.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterHeld()
        {
            AIHeldAbilities.Register(EntadCompat.HasHeldAbilities, EntadCompat.CollectHeldAbilities);
            AbilityAIHooks.RegisterVote((pawn, raw) =>
                !EntadCompat.HeldAbilityGrants(pawn, raw, out bool hidden) ? AbilityAIVote.NoOpinion
                : hidden && pawn.Faction != null && pawn.Faction.IsPlayer ? AbilityAIVote.Veto : AbilityAIVote.Claim);
        }
    }
}
