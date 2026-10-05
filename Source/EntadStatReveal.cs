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
                    case "MedicalTendQuality": case "MedicalTendSpeed": case "SurgerySuccessChanceFactor": case "MedicalSurgerySuccessChance": return EntadStatTrigger.Doctor;
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

        // Stats a partialStats trait has but that don't apply to this item don't count: they do nothing, so they
        // can't be what gets noticed
        private static bool Has(AppliedEntadTrait m, Thing thing, EntadStatTrigger trigger)
        {
            foreach (var r in m.def.AllRanges())
                if (r.stat != null && r.stat != StatDefOf.MarketValue && Classify(r.stat, thing.def) == trigger && m.def.UsesStat(r.stat, thing)) return true;
            return false;
        }

        public static void Fire(CompEntad comp, EntadStatTrigger trigger)
        {
            if (comp == null || !comp.HasHidden) return;
            Thing thing = comp.parent;
            comp.RevealWhere(EntadEffectKind.Stat, m => Has(m, thing, trigger));
        }

        // Fires the trigger on everything the pawn wears or wields
        public static void Fire(Pawn pawn, EntadStatTrigger trigger)
        {
            if (pawn == null) return;
            var gear = pawn.equipment?.AllEquipmentListForReading;
            if (gear != null) for (int i = 0; i < gear.Count; i++) FireFor(pawn, gear[i].TryGetComp<CompEntad>(), trigger);
            var worn = pawn.apparel?.WornApparel;
            if (worn != null) for (int i = 0; i < worn.Count; i++) FireFor(pawn, worn[i].TryGetComp<CompEntad>(), trigger);
        }

        // A bound item's traits do nothing for someone outside the bloodline, so that use can't reveal them either
        private static void FireFor(Pawn pawn, CompEntad comp, EntadStatTrigger trigger)
        {
            if (comp != null && comp.ActiveFor(pawn)) Fire(comp, trigger);
        }

        // True when the pawn wears or wields anything with a still-hidden trait. Checked before any trigger work, so
        // the job-end and damage patches cost a couple of list scans for everyone else (animals have neither tracker).
        public static bool HasHiddenGear(Pawn pawn)
        {
            if (pawn == null) return false;
            var gear = pawn.equipment?.AllEquipmentListForReading;
            if (gear != null) for (int i = 0; i < gear.Count; i++) if (HiddenFor(gear[i], pawn)) return true;
            var worn = pawn.apparel?.WornApparel;
            if (worn != null) for (int i = 0; i < worn.Count; i++) if (HiddenFor(worn[i], pawn)) return true;
            return false;
        }

        // Hidden traits this pawn could reveal: a bound item outside their bloodline has none, so it doesn't count
        private static bool HiddenFor(Thing item, Pawn pawn)
        {
            var comp = item.TryGetComp<CompEntad>();
            return comp != null && comp.HasHidden && comp.ActiveFor(pawn);
        }

        public static void OnJobFinished(Pawn pawn, Job job)
        {
            if (job == null || !HasHiddenGear(pawn)) return;
            if (job.workGiverDef != null)
            {
                Fire(pawn, EntadStatTrigger.Work);
                WorkTypeDef work = job.workGiverDef.workType;
                if (work == WorkTypeDefOf.Mining) Fire(pawn, EntadStatTrigger.Mining);
                else if (work == WorkTypeDefOf.Construction) Fire(pawn, EntadStatTrigger.Construction);
                else if (work == WorkTypeDefOf.Growing) Fire(pawn, EntadStatTrigger.Plant);
                else if (work == WorkTypeDefOf.Research) Fire(pawn, EntadStatTrigger.Research);
                else if (work == WorkTypeDefOf.Doctor) Fire(pawn, EntadStatTrigger.Doctor);
                else if (work == WorkTypeDefOf.Handling) Fire(pawn, EntadStatTrigger.Animals);
            }
            // Crafting recipes (stonecutting, chemfuel, burning items, tailoring, art, smithing, smelting...) are
            // paced by whichever stat the recipe names, so ask the recipe rather than guessing from work types
            if (job.bill?.recipe?.workSpeedStat == StatDefOf.GeneralLaborSpeed) Fire(pawn, EntadStatTrigger.GeneralLabor);
            JobDef d = job.def;
            if (d == JobDefOf.Ingest) Fire(pawn, EntadStatTrigger.Eat);
            else if (d == JobDefOf.LayDown) Fire(pawn, EntadStatTrigger.Rest);
            else if (d == JobDefOf.TradeWithPawn) Fire(pawn, EntadStatTrigger.Trade);
            else if (d == JobDefOf.Research) Fire(pawn, EntadStatTrigger.Research);
            else if (d == JobDefOf.Mine) Fire(pawn, EntadStatTrigger.Mining);
            else if (d == JobDefOf.Tame || d == JobDefOf.Train) Fire(pawn, EntadStatTrigger.Animals);
        }
    }

    // Revealed when the job is completed, not while the pawn is still walking to it
    [HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class Patch_EndCurrentJob_StatReveal
    {
        public static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn, JobCondition condition)
        {
            if (condition == JobCondition.Succeeded) EntadStatReveal.OnJobFinished(___pawn, __instance.curJob);
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
        // Learn fires on every xp gain. Work grants a trickle on each of the pawn's tick intervals (0.05 to 0.25 xp
        // per tick, times the interval), so trickle gains are only checked on the pawn's 250-tick hash tick; the pawn's
        // interval ticks share its hash offset, so a working pawn lines up with it every few hundred ticks.
        // One-off grants (a shot, a melee swing, a social interaction, a ritual) are rare and usually 1 xp or more, so
        // those are always checked: a hash gate would give each of them about a 1 in 250 chance.
        // Stateless on purpose: an earlier per-pawn "last checked" table was static, so it kept ticks from a previous
        // game and skipped reveals after loading an earlier save.
        public static void Postfix(SkillRecord __instance, float xp)
        {
            if (xp <= 0f) return;
            Pawn pawn = PawnField(__instance);
            if (pawn == null || (xp < 1f && !pawn.IsHashIntervalTick(250))) return;
            if (!EntadStatReveal.HasHiddenGear(pawn)) return;
            EntadStatReveal.Fire(pawn, EntadStatTrigger.Learn);
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.PostApplyDamage))]
    public static class Patch_Damage_StatReveal
    {
        // Damage defs that count as toxic, found once by name (ToxGas, Tox bullets, mod tox damage)
        private static HashSet<DamageDef> toxic;

        public static void Postfix(Pawn __instance, DamageInfo dinfo)
        {
            if (!EntadStatReveal.HasHiddenGear(__instance)) return;
            EntadStatReveal.Fire(__instance, EntadStatTrigger.Damage);
            if (toxic == null)
            {
                toxic = new HashSet<DamageDef>();
                foreach (var d in DefDatabase<DamageDef>.AllDefsListForReading) if (d.defName.Contains("Tox")) toxic.Add(d);
            }
            if (dinfo.Def != null && toxic.Contains(dinfo.Def)) EntadStatReveal.Fire(__instance, EntadStatTrigger.Toxic);
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
