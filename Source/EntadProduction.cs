using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Masterwork and Abundant: workbench traits that change what a bill makes. Both are plain stat traits on the
    // bench (Entad_ProductQualityChance, Entad_ProductDoubleChance), so they roll, scale, show on the card and reveal
    // like any furniture stat (a bill at the bench is a use). They only work for a crafter the bench is active for.
    //
    // The hook is a postfix on GenRecipe.MakeRecipeProducts, which every production bill goes through (with or
    // without an unfinished thing) and which knows the bench. Vanilla turns the result into a list at once, so the
    // wrapper below runs inside the bill's last toil. Cost: one def-table lookup per finished bill for a bench that
    // can't be an entad; for an entad bench, two stat reads per bill.
    [HarmonyPatch(typeof(GenRecipe), nameof(GenRecipe.MakeRecipeProducts))]
    public static class Patch_GenRecipe_MakeRecipeProducts_Entad
    {
        public static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Pawn worker, IBillGiver billGiver)
        {
            if (!(billGiver is Thing bench) || worker == null || !EntadCompInjector.MayHaveComp(bench.def)) return __result;
            var comp = bench.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.Count == 0 || !comp.ActiveFor(worker)) return __result;
            float quality = bench.GetStatValue(EntadStatDefOf.Entad_ProductQualityChance);
            float doubled = bench.GetStatValue(EntadStatDefOf.Entad_ProductDoubleChance);
            if (quality <= 0f && doubled <= 0f) return __result;
            // One roll per bill for doubling, so a butchered animal gives double meat and double leather or neither
            return Wrap(__result, worker, bench, comp, quality, doubled > 0f && Rand.Chance(doubled));
        }

        private static IEnumerable<Thing> Wrap(IEnumerable<Thing> products, Pawn worker, Thing bench, CompEntad comp, float qualityChance, bool doubled)
        {
            bool anyDoubled = false;
            Thing firstDoubled = null;
            foreach (var product in products)
            {
                if (product == null) { yield return product; continue; }
                // One step up from what the crafter's skill rolled (an inspiration included), at most legendary.
                // Art was already made for the first roll, and stays. A sculpture or other minifiable product comes
                // out of PostProcessProduct already packed in a MinifiedThing, so the quality comp is on the inner thing.
                // (Vanilla's masterwork and legendary notifications ran at the first roll, so a raise to those
                // levels gets only our message.)
                var qualityComp = product.GetInnerIfMinified().TryGetComp<CompQuality>();
                if (qualityChance > 0f && qualityComp != null && qualityComp.Quality < QualityCategory.Legendary && Rand.Chance(qualityChance))
                {
                    var raised = qualityComp.Quality + 1;
                    // With a source, SetQuality initializes art. That's a no-op where the first roll already made art
                    // (CompArt keeps an existing title), and makes it for a weapon whose art starts at the raised
                    // level (minQualityForArtistic). The author was set at the first roll (JustCreatedBy).
                    qualityComp.SetQuality(raised, ArtGenerationContext.Colony);
                    // Vanilla records the art tale from excellent up; a raise into that band records it here instead.
                    // Only with art in place: the tale reads the thing's art title.
                    var inner = product.GetInnerIfMinified();
                    var art = inner.TryGetComp<CompArt>();
                    if (raised == QualityCategory.Excellent && art != null && art.Active)
                        TaleRecorder.RecordTale(TaleDefOf.CraftedArt, worker, inner);
                    Reveal(comp, EntadStatDefOf.Entad_ProductQualityChance);
                    Messages.Message("EF_ProductQualityRaised".Translate(worker.LabelShort, bench.LabelShort, product.LabelNoCount, raised.GetLabel()),
                        new LookTargets(bench), MessageTypeDefOf.PositiveEvent);
                }
                if (!doubled || product.def.stackLimit <= 1)
                {
                    yield return product;
                    continue;
                }
                // Twice the stack. Past the stack limit the rest comes as more stacks (SplitOff copies the comps that
                // matter, such as a meal's ingredients), after the original, which the crafter carries to storage.
                product.stackCount *= 2;
                var extra = new List<Thing>();
                while (product.stackCount > product.def.stackLimit) extra.Add(product.SplitOff(product.def.stackLimit));
                if (!anyDoubled) firstDoubled = product;
                anyDoubled = true;
                yield return product;
                for (int i = 0; i < extra.Count; i++) yield return extra[i];
            }
            if (anyDoubled)
            {
                Reveal(comp, EntadStatDefOf.Entad_ProductDoubleChance);
                Messages.Message("EF_ProductDoubled".Translate(worker.LabelShort, bench.LabelShort, firstDoubled.def.label),
                    new LookTargets(bench), MessageTypeDefOf.PositiveEvent);
            }
        }

        // The trait that did it is now known, whether or not anyone had worked at the bench long enough before
        private static void Reveal(CompEntad comp, StatDef stat)
        {
            comp.RevealWhere(EntadEffectKind.Stat, m =>
            {
                var offsets = m.def.statOffsets;
                for (int i = 0; offsets != null && i < offsets.Count; i++) if (offsets[i].stat == stat) return true;
                return false;
            });
        }
    }
}
