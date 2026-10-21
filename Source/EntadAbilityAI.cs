using RimWorld;
using Verse;
using EntadFramework.AbilityAI;

namespace EntadFramework
{
    // Entad's side of the ability AI (Source/AbilityAI/). The AI itself knows nothing about entads; this hook tells it
    // which abilities are ours to cast.
    //
    // Claim: gear the pawn wears or wields grants the ability through a trait that is active for it, which already
    // covers binding (EntadAbilities.Grants checks ActiveFor, so an item bound to someone else grants nothing).
    // Anything else gets no opinion: a raider's own psycasts are left to the "unclaimed abilities" setting.
    //
    // Reveal needs nothing here: Patch_Ability_Activate_Reveal reveals the granting trait when any pawn's cast
    // resolves, the AI's included, so a raider's cast reveals the trait on the item it drops.
    public class EntadAbilityAIHook : IAbilityAIHook
    {
        public static readonly EntadAbilityAIHook Instance = new EntadAbilityAIHook();

        public AbilityAIVote Vote(Pawn pawn, Ability ability) =>
            EntadAbilities.Grants(pawn, ability.def, out _) ? AbilityAIVote.Claim : AbilityAIVote.NoOpinion;

        public void Notify_AICast(Pawn pawn, Ability ability, LocalTargetInfo target, LocalTargetInfo dest) { }
    }

    public partial class CompProperties_AbilityEntadPulse : IAbilityAIInfo
    {
        public float AIRadius => radius;
        public HediffDef AIHediff => hediff;
        public bool AIStuns => stunSeconds > 0f;
    }
}
