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
            // Runs for every MaxHitPoints and MarketValue read of every thing: a table lookup rules out most of them
            if (!req.HasThing || !EntadCompInjector.MayHaveComp(req.Thing.def)) return;
            var comp = req.Thing.TryGetComp<CompEntad>();
            if (comp == null || comp.activeTraits.NullOrEmpty()) return;
            bool isValue = parentStat == StatDefOf.MarketValue;
            // CompEntad.BaseMarketValue: the value before any entad part, which trait prices are a share of
            if (isValue && CompEntad.baseValueFor == req.Thing) return;

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

            // A bound item held by someone outside the bloodline: its trait stats don't apply. Market value and
            // durability are the item's own and stay. An item nobody holds counts as active (ActiveFor(null)).
            bool userActive = isValue || parentStat == StatDefOf.MaxHitPoints || comp.ActiveForHolder;
            // Market value by context. Wealth (and so raid size) counts every trait: the storyteller isn't fooled by a
            // trait nobody has found yet. Traders price only what's identified, plus one mystery premium if anything
            // isn't. The item's own card shows identified traits only. Trade prices are read while a TradeSession is
            // open (the deal is built after the session starts), and nothing caches market value between reads.
            bool trade = isValue && EntadTrade.Open;
            bool valueSkipsHidden = trade || EntadWearerStats.infoCardThing == req.Thing;
            float baseValue = val, traitValue = 0f;
            bool unidentified = false;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                // Stat effects apply whether or not they've been revealed; only displays leave them out (skipHidden).
                // (A trait's own statOffsets on MarketValue, if any, are gated like other stats.)
                bool apply = userActive && (!skipHidden || m.IsRevealed(EntadEffectKind.Stat));
                for (int i = 0; apply && !offsetsViaWearer && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat) val += m.OffsetFor(i);
                for (int i = 0; apply && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat) val *= m.FactorFor(i);
                if (!isValue) continue;
                if (m.AnyHidden) unidentified = true;
                if (!m.AnyHidden || !valueSkipsHidden) traitValue += m.MarketValueOffset(baseValue);
            }
            if (isValue)
            {
                // Drawbacks can't take the item below the settings' share of its base value
                traitValue = UnityEngine.Mathf.Max(traitValue, -(1f - EntadSettings.ValueFloor) * baseValue);
                if (trade && unidentified) traitValue += baseValue * EntadSettings.MysteryBonus;
                val += traitValue;
            }
            // Durability: every entad, by its highest trait rarity. Visible from the start, so never hidden.
            if (parentStat == StatDefOf.MaxHitPoints) val *= comp.DurabilityFactor;
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
            // The finished value with our part left out. TransformValue prices from the value at our place in the
            // stat's parts instead; the two match while ours is the last part, which it is unless another mod injects a
            // MarketValue part from code after our static constructor (XML-declared parts always come first).
            float baseValue = parentStat == StatDefOf.MarketValue ? comp.BaseMarketValue() : 0f;
            float traitValue = 0f;
            bool userActive = parentStat == StatDefOf.MaxHitPoints || comp.ActiveForHolder;
            foreach (var m in comp.activeTraits)
            {
                var def = m.def;
                bool known = userActive && m.IsRevealed(EntadEffectKind.Stat);
                if (parentStat == StatDefOf.MarketValue)
                {
                    if (m.AnyHidden) unidentified = true;
                    else
                    {
                        float v = m.MarketValueOffset(baseValue);
                        traitValue += v;
                        explanation += "\n" + "EF_Explain_TraitValue".Translate(def.LabelCap, m.Rarity.Label(), v.ToStringMoneyOffset());
                    }
                    continue;
                }
                for (int i = 0; known && def.statOffsets != null && i < def.statOffsets.Count; i++)
                    if (def.statOffsets[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: {parentStat.Worker.ValueToString(m.OffsetFor(i), false, ToStringNumberSense.Offset)}";
                for (int i = 0; known && def.statFactors != null && i < def.statFactors.Count; i++)
                    if (def.statFactors[i].stat == parentStat)
                        explanation += $"\n{def.LabelCap}: x{m.FactorFor(i).ToStringPercent()}";
            }
            // Same clamp as TransformValue, so the lines add up to the value shown
            float lowest = -(1f - EntadSettings.ValueFloor) * baseValue;
            if (parentStat == StatDefOf.MarketValue && traitValue < lowest)
                explanation += "\n" + "EF_Explain_ValueFloor".Translate(EntadSettings.ValueFloor.ToStringPercent(), (lowest - traitValue).ToStringMoneyOffset());
            // The premium only exists in trade, so that's the only place it's explained
            if (unidentified && EntadTrade.Open)
                explanation += "\n" + "EF_Explain_Unidentified".Translate((baseValue * EntadSettings.MysteryBonus).ToStringMoney());
            if (parentStat == StatDefOf.MaxHitPoints)
                explanation += "\n" + "EF_Explain_Durability".Translate(comp.HighestRarity.Label(), comp.DurabilityFactor.ToStringPercent());
            return explanation.NullOrEmpty() ? null : explanation;
        }
    }

    // Whether a trade window is open. TradeSession.Active alone isn't enough: vanilla never clears the session's
    // trader after a trade, so it stays true for the rest of the game and wealth would be priced like a trade.
    public static class EntadTrade
    {
        private static bool open;

        public static bool Open => open && TradeSession.Active;

        public static void Reset() => open = false;

        // Dialog_Trade calls SetupWith from its constructor, before any price is read
        [HarmonyPatch(typeof(TradeSession), nameof(TradeSession.SetupWith))]
        public static class Patch_SetupWith
        {
            public static void Postfix() => open = true;
        }

        // Window.PostClose runs however a window is removed (Close, escape, WindowStack.TryRemove); Dialog_Trade
        // doesn't override it, so a postfix on the base method sees it
        [HarmonyPatch(typeof(Window), nameof(Window.PostClose))]
        public static class Patch_PostClose
        {
            public static void Postfix(Window __instance)
            {
                if (open && __instance is Dialog_Trade) open = false;
            }
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
            // MaxHitPoints always: every entad gets the durability multiplier, whatever its traits touch
            var stats = new HashSet<StatDef> { StatDefOf.MarketValue, StatDefOf.MaxHitPoints };
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
                // MaxHitPoints is the exception: every Thing reads it constantly, so it keeps vanilla's permanent cache.
                // That's safe because its value never changes for display only (the info card leaves it real), and
                // every real change (trait add/remove/reveal, durability sync) clears it with ClearCacheForThing,
                // which empties the immutable cache too.
                if (stat.immutable && stat != StatDefOf.MaxHitPoints)
                {
                    stat.immutable = false;
                    stat.Worker.SetCacheability(false);
                }
            }
        }
    }
}
