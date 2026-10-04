using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public class StatPart_Entad : StatPart
    {
        public override void TransformValue(StatRequest req, ref float val)
        {
            var comp = req.HasThing ? req.Thing.TryGetComp<CompEntad>() : null;
            if (comp == null || comp.activeTraits.NullOrEmpty()) return;

            bool offsetsViaWearer = OffsetsViaWearer(req);
            // Set only while the pawn's "Relevant gear" line is being built (Patch_InfoTextLineFromGear), so the
            // number on that line leaves out unrevealed traits. Read after the early return, so items without
            // traits never pay for the thread-static read.
            // Wearer stats only: both flags exist for StatOffsetFromGear, which always has offsetsViaWearer true.
            // Without that gate the flag leaked into other stats read while the gear line is built (LabelCap reads
            // MaxHitPoints through GetStatValue), caching a value without the hidden traits.
            // The third case is the item's own info card (Patch_DrawStatsReport): every stat of THAT item leaves out
            // unrevealed traits, and the patch clears the item's stat caches when the card is done drawing, so
            // nothing computed here survives into gameplay.
            bool skipHidden = (offsetsViaWearer
                    && (EntadWearerStats.displayExcludesHidden || EntadWearerStats.gearCardThing == req.Thing))
                || (EntadWearerStats.infoCardThing == req.Thing && parentStat != StatDefOf.MaxHitPoints);
            // MaxHitPoints stays real on the card: its HP row is "HitPoints / MaxHitPoints", and HitPoints is the
            // real stored value (already scaled by the trait), so a hidden-free maximum would read e.g. 150 / 100.

            bool unidentified = false;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                // Stat effects apply whether or not they've been revealed; only displays leave them out (skipHidden).
                // The MarketValueOffset/unidentified term below is not gated: unrevealed traits already show as the
                // flat "unidentified" bonus. (A trait's own statOffsets on MarketValue, if any, are gated like others.)
                bool apply = !skipHidden || m.IsRevealed(EntadEffectKind.Stat);
                for (int i = 0; apply && !offsetsViaWearer && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat) val += m.OffsetFor(i);
                for (int i = 0; apply && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat) val *= m.FactorFor(i);
                // Identified traits add their normal value; unidentified ones share one fixed bonus (below)
                if (parentStat == StatDefOf.MarketValue && !m.AnyHidden) val += m.MarketValueOffset();
                else if (parentStat == StatDefOf.MarketValue) unidentified = true;
            }
            if (unidentified) val += EntadTraitDef.UnidentifiedMarketValue;
        }

        // Wearer-stat offsets (move speed, shooting accuracy...) reach the pawn through the StatOffsetFromGear
        // postfix in EntadWearerStats. Vanilla StatOffsetFromGear also runs stat.parts over the gear whenever the
        // def has its own equippedStatOffset for that stat, so adding them here too would count them twice.
        // Only gear defers: a building's own Meditation-category stat (MeditationFocusStrength on a focus object)
        // is a stat of the building, never reaches the wearer path, and must still be offset here.
        private bool OffsetsViaWearer(StatRequest req)
        {
            var td = req.Thing.def;
            return EntadTraitDef.IsWearerStat(parentStat) && !(req.Thing is Pawn)
                && (td.IsApparel || td.equipmentType != EquipmentType.None);
        }

        public override string ExplanationPart(StatRequest req)
        {
            var comp = req.HasThing ? req.Thing.TryGetComp<CompEntad>() : null;
            if (comp == null || comp.activeTraits.NullOrEmpty()) return null;
            // No OffsetsViaWearer gate here: where this explains gear (the item card's equipped-offset row), the value
            // already includes the entad offset via the StatOffsetFromGear postfix, so the line must stay.

            string explanation = "";
            bool unidentified = false;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                bool known = m.IsRevealed(EntadEffectKind.Stat);
                if (parentStat == StatDefOf.MarketValue)
                {
                    if (m.AnyHidden) unidentified = true;
                    else explanation += $"\n{def.LabelCap} ({m.Rarity}): +{m.MarketValueOffset().ToStringMoney()}";
                    continue;
                }
                for (int i = 0; known && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: {(m.OffsetFor(i) >= 0 ? "+" : "")}{m.OffsetFor(i).ToStringByStyle(parentStat.toStringStyle)}";
                for (int i = 0; known && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: x{m.FactorFor(i).ToStringPercent()}";
            }
            if (unidentified) explanation += $"\nUnidentified entad properties: +{EntadTraitDef.UnidentifiedMarketValue.ToStringMoney()}";
            return explanation.NullOrEmpty() ? null : explanation;
        }
    }

    // Dynamically attaches StatPart_Entad to every stat referenced by any EntadTraitDef (and MarketValue)
    [StaticConstructorOnStartup]
    public static class EntadStatPartInjector
    {
        static EntadStatPartInjector()
        {
            // Stat injection runs first and patching is isolated per class: previously one PatchAll() call sat ahead of
            // the injection, so a single patch whose runtime TargetMethods failed to bind threw out of this static
            // constructor and every trait's stat offsets, factors and market value silently stopped applying.
            try { InjectStatParts(); }
            catch (System.Exception e) { Log.Error($"[Entad Framework] Stat injection failed; trait stat effects are off, patches still apply: {e}"); }
            PatchEachClass();
        }

        private static void PatchEachClass()
        {
            var harmony = new Harmony("saintwacko.entadframework");
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(EntadStatPartInjector).Assembly))
            {
                // Only real patch classes: the class processor invokes any static method named Prepare as a Harmony
                // lifecycle hook, and EntadAbilityPrep.Prepare(EntadTraitDef) got called with null.
                // A future patch class annotated only on its methods would be skipped here: keep [HarmonyPatch] on the class.
                if (!type.IsDefined(typeof(HarmonyPatch), true)) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (System.Exception e) { Log.Error($"[Entad Framework] Patch {type.FullName} failed; that one feature is off, the rest still works: {e}"); }
            }
        }

        private static void InjectStatParts()
        {
            var stats = new HashSet<StatDef> { StatDefOf.MarketValue };
            foreach (var def in DefDatabase<EntadTraitDef>.AllDefsListForReading)
                foreach (var r in def.AllRanges())
                    if (r.stat != null) stats.Add(r.stat);

            foreach (var stat in stats)
            {
                if (stat.parts == null) stat.parts = new List<StatPart>();
                if (stat.parts.Any(p => p is StatPart_Entad)) continue;
                var part = new StatPart_Entad { parentStat = stat };
                stat.parts.Add(part);
                // StatDef.SetImmutability already ran during def loading, before static constructors. A stat that had
                // no parts may have been marked immutable, which caches each thing's first value forever: entad
                // items would keep their pre-trait value and a wearer stat would ignore entad gear changes.
                if (stat.immutable)
                {
                    stat.immutable = false;
                    stat.Worker.SetCacheability(false);
                }
            }
        }
    }
}
