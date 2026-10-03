using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace EntadFramework
{
    // What has to happen before a stat trait is noticed. Each stat belongs to one trigger, and a trait
    // is revealed when any of its stats' triggers fires. Triggers are fired from a few cheap game events
    // (job start, hits, damage...) rather than by watching stat lookups, which would be far too hot a path.
    public enum EntadStatTrigger
    {
        Equip, Use, Hit, Armor,
        Work, GeneralLabor, Mining, Construction, Plant, Research, Doctor, Animals, Trade, Social, Eat, Rest, Learn,
        Shoot, MeleeAttack, MeleeDodge, Damage, Toxic
    }

    public static class EntadStatReveal
    {
        public static EntadStatTrigger Classify(StatDef stat, ThingDef td)
        {
            if (EntadTraitDef.IsWearerStat(stat))
            {
                switch (stat.defName)
                {
                    case "WorkSpeedGlobal": return EntadStatTrigger.Work;
                    case "GeneralLaborSpeed": return EntadStatTrigger.GeneralLabor;
                    case "MiningSpeed": return EntadStatTrigger.Mining;
                    case "ConstructionSpeed": case "SmoothingSpeed": return EntadStatTrigger.Construction;
                    case "PlantWorkSpeed": case "PlantHarvestYield": return EntadStatTrigger.Plant;
                    case "ResearchSpeed": return EntadStatTrigger.Research;
                    case "MedicalTendQuality": case "MedicalTendSpeed": case "SurgerySuccessChanceFactor": return EntadStatTrigger.Doctor;
                    case "TameAnimalChance": case "TrainAnimalChance": return EntadStatTrigger.Animals;
                    case "NegotiationAbility": case "TradePriceImprovement": return EntadStatTrigger.Trade;
                    case "SocialImpact": return EntadStatTrigger.Social;
                    case "EatingSpeed": return EntadStatTrigger.Eat;
                    case "RestRateMultiplier": return EntadStatTrigger.Rest;
                    case "GlobalLearningFactor": return EntadStatTrigger.Learn;
                    case "ShootingAccuracyPawn": return EntadStatTrigger.Shoot;
                    case "MeleeHitChance": return EntadStatTrigger.MeleeAttack;
                    case "MeleeDodgeChance": return EntadStatTrigger.MeleeDodge;
                    case "PainShockThreshold": return EntadStatTrigger.Damage;
                    case "ToxicResistance": return EntadStatTrigger.Toxic;
                    default: return EntadStatTrigger.Equip;
                }
            }
            if (td.IsWeapon) return EntadStatTrigger.Hit;
            if (td.IsApparel) return stat.defName.StartsWith("ArmorRating_") ? EntadStatTrigger.Armor : EntadStatTrigger.Equip;
            return EntadStatTrigger.Use;
        }

        private static bool Has(AppliedEntadTrait m, ThingDef td, EntadStatTrigger trigger)
        {
            foreach (var r in m.def.AllRanges())
                if (r.stat != null && r.stat != StatDefOf.MarketValue && Classify(r.stat, td) == trigger) return true;
            return false;
        }

        public static void Fire(CompEntad comp, EntadStatTrigger trigger)
        {
            if (comp == null || !comp.HasHidden) return;
            ThingDef td = comp.parent.def;
            comp.RevealWhere(EntadEffectKind.Stat, m => Has(m, td, trigger));
        }

        // Fires the trigger on everything the pawn wears or wields
        public static void Fire(Pawn pawn, EntadStatTrigger trigger)
        {
            if (pawn == null) return;
            var gear = pawn.equipment?.AllEquipmentListForReading;
            if (gear != null) for (int i = 0; i < gear.Count; i++) Fire(gear[i].TryGetComp<CompEntad>(), trigger);
            var worn = pawn.apparel?.WornApparel;
            if (worn != null) for (int i = 0; i < worn.Count; i++) Fire(worn[i].TryGetComp<CompEntad>(), trigger);
        }

        public static void OnJobFinished(Pawn pawn, Job job)
        {
            if (job == null) return;
            if (job.workGiverDef != null)
            {
                Fire(pawn, EntadStatTrigger.Work);
                switch (job.workGiverDef.workType?.defName)
                {
                    case "Mining": Fire(pawn, EntadStatTrigger.Mining); break;
                    case "Construction": Fire(pawn, EntadStatTrigger.Construction); break;
                    case "Growing": Fire(pawn, EntadStatTrigger.Plant); break;
                    case "Research": Fire(pawn, EntadStatTrigger.Research); break;
                    case "Doctor": Fire(pawn, EntadStatTrigger.Doctor); break;
                    case "Handling": Fire(pawn, EntadStatTrigger.Animals); break;
                }
            }
            // Crafting recipes (stonecutting, chemfuel, burning items, tailoring, art, smithing, smelting...) are
            // paced by whichever stat the recipe names, so ask the recipe rather than guessing from work types
            if (job.bill?.recipe?.workSpeedStat == StatDefOf.GeneralLaborSpeed) Fire(pawn, EntadStatTrigger.GeneralLabor);
            switch (job.def.defName)
            {
                case "Ingest": Fire(pawn, EntadStatTrigger.Eat); break;
                case "LayDown": Fire(pawn, EntadStatTrigger.Rest); break;
                case "TradeWithPawn": Fire(pawn, EntadStatTrigger.Trade); break;
                case "Research": Fire(pawn, EntadStatTrigger.Research); break;
                case "Mine": Fire(pawn, EntadStatTrigger.Mining); break;
                case "Tame": case "Train": Fire(pawn, EntadStatTrigger.Animals); break;
            }
        }
    }

    // Revealed when the job is completed, not while the pawn is still walking to it
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_EndCurrentJob_StatReveal
    {
        public static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            if (condition == JobCondition.Succeeded) EntadStatReveal.OnJobFinished(__instance.pawn, __instance.curJob);
        }
    }

    [HarmonyPatch(typeof(Pawn_InteractionsTracker), nameof(Pawn_InteractionsTracker.TryInteractWith))]
    public static class Patch_Interact_StatReveal
    {
        private static readonly AccessTools.FieldRef<Pawn_InteractionsTracker, Pawn> PawnField = AccessTools.FieldRefAccess<Pawn_InteractionsTracker, Pawn>("pawn");

        public static void Postfix(Pawn_InteractionsTracker __instance, bool __result)
        {
            if (__result) EntadStatReveal.Fire(PawnField(__instance), EntadStatTrigger.Social);
        }
    }

    [HarmonyPatch(typeof(SkillRecord), nameof(SkillRecord.Learn))]
    public static class Patch_Learn_StatReveal
    {
        private static readonly AccessTools.FieldRef<SkillRecord, Pawn> PawnField = AccessTools.FieldRefAccess<SkillRecord, Pawn>("pawn");
        private static readonly Dictionary<int, int> lastCheck = new Dictionary<int, int>();
        private const int CheckInterval = 250;

        // Learn fires on every xp gain, so each pawn is only checked a few times a minute
        public static void Postfix(SkillRecord __instance, float xp)
        {
            if (xp <= 0f) return;
            Pawn pawn = PawnField(__instance);
            if (pawn == null) return;
            int now = Find.TickManager.TicksGame;
            if (lastCheck.TryGetValue(pawn.thingIDNumber, out int last) && now - last < CheckInterval) return;
            lastCheck[pawn.thingIDNumber] = now;
            EntadStatReveal.Fire(pawn, EntadStatTrigger.Learn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Damage_StatReveal
    {
        public static void Postfix(Pawn __instance, DamageInfo dinfo)
        {
            EntadStatReveal.Fire(__instance, EntadStatTrigger.Damage);
            if (dinfo.Def != null && dinfo.Def.defName.Contains("Tox")) EntadStatReveal.Fire(__instance, EntadStatTrigger.Toxic);
        }
    }

    [HarmonyPatch(typeof(ArmorUtility), nameof(ArmorUtility.GetPostArmorDamage))]
    public static class Patch_Armor_StatReveal
    {
        public static void Postfix(Pawn pawn)
        {
            EntadStatReveal.Fire(pawn, EntadStatTrigger.Armor);
        }
    }
}
