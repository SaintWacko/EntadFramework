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
        }
    }
}
