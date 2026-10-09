using System;
using System.Collections.Generic;
using System.Linq;
using LudeonTK;
using Verse;
using RimWorld;

namespace EntadFramework
{
    public static class DebugActionsEntad
    {
        [DebugAction(
            category = "Entad Framework",
            name = "Apply Random Entad",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap
        )]
        private static void ApplyRandomEntad()
        {
            IntVec3 cell = UI.MouseCell();
            Map map = Find.CurrentMap;

            if (map == null || !cell.InBounds(map)) return;

            // Find first thing at cursor location with CompEntad attached
            List<Thing> thingList = cell.GetThingList(map);
            Thing targetThing = thingList.Find(t => t.TryGetComp<CompEntad>() != null);

            if (targetThing == null)
            {
                Messages.Message("No valid Entad-compatible item found at clicked cell.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var applied = EntadApi.ApplyRandomTraitsIgnoringUniqueChance(targetThing);
            if (applied.Count == 0)
            {
                Messages.Message("No applicable EntadTraitDefs for this item!", MessageTypeDefOf.RejectInput, false);
                return;
            }

            Messages.Message($"Applied {string.Join(", ", applied.Select(t => t.LabelCap.Resolve()))} to {targetThing.Label}!", MessageTypeDefOf.PositiveEvent, false);
        }

        [DebugAction(
            category = "Entad Framework",
            name = "Generate Random Entad Item",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap
        )]
        private static void GenerateRandomEntadItem()
        {
            IntVec3 cell = UI.MouseCell();
            Map map = Find.CurrentMap;
            if (map == null || !cell.InBounds(map)) return;

            var options = new List<DebugMenuOption>();
            var r = EntadSettings.PointTarget;
            options.Add(new DebugMenuOption($"Settings target ({r.min:0.#}-{r.max:0.#} points)", DebugMenuOptionMode.Action, () => PlaceGenerated(cell, map, r.RandomInRange)));
            foreach (float p in new[] { 1f, 2f, 4f, 6f, 8f, 12f, 16f, 24f })
            {
                float points = p;
                options.Add(new DebugMenuOption($"{points} points", DebugMenuOptionMode.Action, () => PlaceGenerated(cell, map, points)));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        private static void PlaceGenerated(IntVec3 cell, Map map, float points)
        {
            Thing thing = EntadApi.GenerateEntadItem(points);
            if (thing == null)
            {
                Messages.Message("Could not generate an entad item.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near);
            // Dev only: what the target bought and the total, so point values can be tuned
            var comp = thing.TryGetComp<CompEntad>();
            string spent = comp == null ? "" : string.Join(", ", comp.activeTraits.Select(m => $"{m.def.defName} {m.Rarity} ({m.def.PointsAt(m.Rarity):0.#})"));
            float total = comp == null ? 0f : comp.activeTraits.Sum(m => m.def.PointsAt(m.Rarity));
            Messages.Message($"Generated {thing.LabelCap}, target {points:0.#}, total {total:0.#}: {spent}.", thing, MessageTypeDefOf.PositiveEvent, false);
        }

        private static CompEntad EntadAtMouse(out Thing thing)
        {
            thing = null;
            Map map = Find.CurrentMap;
            IntVec3 cell = UI.MouseCell();
            if (map == null || !cell.InBounds(map)) return null;
            thing = cell.GetThingList(map).Find(t => t.TryGetComp<CompEntad>() != null);
            if (thing == null) Messages.Message("No valid Entad-compatible item found at clicked cell.", MessageTypeDefOf.RejectInput, false);
            return thing?.TryGetComp<CompEntad>();
        }

        [DebugAction(category = "Entad Framework", name = "Add specific Entad trait", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void AddSpecificEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;

            var options = new List<DebugMenuOption>();
            // Applicable traits first, then alphabetical by defName within each group.
            // CanApplyTo is evaluated once per def so the sort and the label agree.
            var sorted = DefDatabase<EntadTraitDef>.AllDefsListForReading
                .Select(d => (def: d, ok: d.CanApplyTo(thing)))
                .OrderByDescending(p => p.ok)
                .ThenBy(p => p.def.defName, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in sorted)
            {
                var d = pair.def;
                bool ok = pair.ok;
                options.Add(new DebugMenuOption(d.defName + (ok ? "" : " (not applicable)"), DebugMenuOptionMode.Action, () =>
                {
                    comp.AddTrait(d, null, true);
                    Messages.Message($"Applied '{d.LabelCap}' to {thing.Label}.", MessageTypeDefOf.PositiveEvent, false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        [DebugAction(category = "Entad Framework", name = "Remove specific Entad trait", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveSpecificEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            if (comp.activeTraits.Count == 0)
            {
                Messages.Message("That item has no entad traits.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            var options = new List<DebugMenuOption>();
            foreach (var applied in comp.activeTraits.ToList())
            {
                var a = applied;
                options.Add(new DebugMenuOption(a.def.defName, DebugMenuOptionMode.Action, () =>
                {
                    comp.RemoveTrait(a);
                    Messages.Message($"Removed '{a.def.LabelCap}' from {thing.Label}.", MessageTypeDefOf.NeutralEvent, false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        [DebugAction(category = "Entad Framework", name = "Remove all Entad traits", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RemoveAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            int n = comp.activeTraits.Count;
            comp.ClearTraits();
            Messages.Message($"Removed {n} trait(s) from {thing.Label}.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(category = "Entad Framework", name = "Reveal all Entad traits", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void RevealAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            foreach (var m in comp.activeTraits.ToList()) m.Reveal(EntadEffectKind.All);
        }

        [DebugAction(category = "Entad Framework", name = "Bind Entad to pawn", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void BindEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            var options = new List<DebugMenuOption>();
            foreach (Pawn p in thing.Map.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike).OrderBy(p => p.LabelShort))
            {
                Pawn pawn = p;
                options.Add(new DebugMenuOption(pawn.LabelShort, DebugMenuOptionMode.Action, () =>
                {
                    comp.Bind(new[] { pawn });
                    Messages.Message($"Bound {thing.Label} to {pawn.LabelShort}.", MessageTypeDefOf.NeutralEvent, false);
                }));
            }
            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }

        [DebugAction(category = "Entad Framework", name = "Unbind Entad", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void UnbindEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            comp.Unbind();
            Messages.Message($"Unbound {thing.Label}.", MessageTypeDefOf.NeutralEvent, false);
        }

        [DebugAction(category = "Entad Framework", name = "Hide all Entad traits", actionType = DebugActionType.ToolMap, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void HideAllEntad()
        {
            CompEntad comp = EntadAtMouse(out Thing thing);
            if (comp == null) return;
            foreach (var m in comp.activeTraits.ToList()) m.Hide();
        }

        // Testing appraisers without waiting on the caravan chance. The pawn still needs to be in a trade caravan
        // that's trading (spawn one with the vanilla trader caravan incident) for the option to appear.
        [DebugAction(category = "Entad Framework", name = "Make entad appraiser", actionType = DebugActionType.ToolMapForPawns, allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void MakeAppraiser(Pawn p)
        {
            if (p.health.hediffSet.GetFirstHediff<Hediff_EntadAppraiser>() != null) { Messages.Message($"{p.LabelShort} is already an appraiser.", MessageTypeDefOf.RejectInput, false); return; }
            var hediff = (Hediff_EntadAppraiser)HediffMaker.MakeHediff(EntadAppraisalDefOf.Entad_Appraiser, p);
            hediff.state = AppraiserState.Roll(1f);
            p.health.AddHediff(hediff);
            Messages.Message($"{p.LabelShort} is now an entad appraiser (max {hediff.state.max} a day).", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
