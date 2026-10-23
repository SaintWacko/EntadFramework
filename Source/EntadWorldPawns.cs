using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // Entads carried by NPC groups (raids, visitors, trade caravans, settlement defenders). Rolled once per generated
    // group, after its pawns exist: a few of them have their own weapon or a piece of their apparel turned into an
    // entad. Nothing is added to the group, so raid points stay as they were and the entad is extra strength.
    public static class EntadWorldPawns
    {
        public static float GroupChance(Faction faction)
        {
            float c = faction.def.GetModExtension<EntadFactionExtension>()?.carrierChance ?? -1f;
            return c >= 0f ? c : EntadSettings.CarrierGroupChance;
        }

        public static float BoundChance(Faction faction)
        {
            float c = faction.def.GetModExtension<EntadFactionExtension>()?.carrierBoundChance ?? -1f;
            return c >= 0f ? c : EntadSettings.CarrierBoundChance;
        }

        public static float PointFactor(Faction faction) =>
            Mathf.Max(0f, faction.def.GetModExtension<EntadFactionExtension>()?.carrierPointFactor ?? 1f);

        public static int CarrierCount(float groupPoints)
        {
            int extra = EntadSettings.CarrierPointsPerExtra > 0f ? Mathf.FloorToInt(groupPoints / EntadSettings.CarrierPointsPerExtra) : 0;
            return Mathf.Clamp(1 + extra, 1, Mathf.Max(1, EntadSettings.MaxCarriers));
        }

        // The settings' four anchors, joined by straight lines and flat past either end
        public static float BasePoints(float groupPoints)
        {
            var curve = new SimpleCurve();
            for (int i = 0; i < EntadSettings.CarrierCurveGroupPoints.Length; i++)
                curve.Add(EntadSettings.CarrierCurveGroupPoints[i], EntadSettings.CarrierCurvePoints[i]);
            return curve.Evaluate(groupPoints);
        }

        public static void TryGiveEntads(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> pawns)
        {
            if (!EntadSettings.CarriersEnabled || parms?.faction == null || parms.faction.IsPlayer || pawns.NullOrEmpty()) return;
            if (!Rand.Chance(GroupChance(parms.faction))) return;

            // Members only. A trade caravan's slaves for sale carry the caravan's faction with no guest status, and
            // the caravan role only recognises the vanilla slave kind, so in a caravan only the trader and its guards
            // count.
            bool trader = parms.groupKind == PawnGroupKindDefOf.Trader;
            var candidates = new List<Pawn>();
            foreach (Pawn p in pawns)
                if (p.RaceProps.Humanlike && !p.Dead && p.Faction == parms.faction && !p.IsPrisoner && !p.IsSlave
                    && (!trader || IsTraderOrGuard(p, groupMaker)) && HasGearFor(p))
                    candidates.Add(p);
            if (candidates.Count == 0) return;

            int count = Mathf.Min(CarrierCount(parms.points), candidates.Count);
            float basePoints = BasePoints(parms.points) * PointFactor(parms.faction);
            float boundChance = BoundChance(parms.faction);
            foreach (Pawn pawn in candidates.InRandomOrder().Take(count))
            {
                bool bound = Rand.Chance(boundChance);
                float points = basePoints * (bound ? EntadSettings.CarrierBoundPointFactor : 1f)
                    * Rand.Range(1f - EntadSettings.CarrierPointVariance, 1f + EntadSettings.CarrierPointVariance);
                GiveEntad(pawn, Mathf.Max(0f, points), bound);
            }
        }

        private static bool IsTraderOrGuard(Pawn p, PawnGroupMaker groupMaker)
        {
            if (p.kindDef.trader) return true;
            var guards = groupMaker?.guards;
            if (guards == null) return false;
            for (int i = 0; i < guards.Count; i++)
                if (guards[i].kind == p.kindDef) return true;
            return false;
        }

        private static bool HasGearFor(Pawn p) => Candidate(p.equipment?.Primary) != null || PickApparel(p) != null;

        // Gear already holding traits (a scenario's, another mod's) is left as it is
        private static CompEntad Candidate(ThingWithComps thing)
        {
            var comp = thing?.GetComp<CompEntad>();
            return comp != null && comp.activeTraits.NullOrEmpty() && EntadUtility.IsEntadCompatible(thing.def) ? comp : null;
        }

        private static CompEntad PickApparel(Pawn p)
        {
            var worn = p.apparel?.WornApparel;
            if (worn == null) return null;
            CompEntad picked = null;
            int seen = 0;
            for (int i = 0; i < worn.Count; i++)
            {
                var comp = Candidate(worn[i]);
                if (comp != null && Rand.Chance(1f / ++seen)) picked = comp;
            }
            return picked;
        }

        // Weapon or apparel at even odds, falling back to the other when the first gets no trait (nothing fits it,
        // or a unique weapon failed its chance)
        public static bool GiveEntad(Pawn pawn, float points, bool bound)
        {
            var weapon = Candidate(pawn.equipment?.Primary);
            var apparel = PickApparel(pawn);
            var first = Rand.Bool ? weapon : apparel;
            var second = first == weapon ? apparel : weapon;
            if (TryApply(first, points) || TryApply(second, points))
            {
                var comp = first != null && !first.activeTraits.NullOrEmpty() ? first : second;
                Finish(pawn, comp, bound);
                return true;
            }
            return false;
        }

        private static bool TryApply(CompEntad comp, float points) =>
            comp != null && EntadApi.ApplyRandomTraits(comp.parent, points).Count > 0;

        private static void Finish(Pawn pawn, CompEntad comp, bool bound)
        {
            // Binding re-grants for the holder (BindingChanged); an unbound item is granted here. Either way the
            // traits were added to gear already in use, which no equip notification will announce.
            if (bound) comp.Bind(new[] { pawn });
            else EntadAbilities.Grant(pawn, comp);
            // Adding and granting reveal what the holder would notice at once (equipped stats, hediffs, moods). An
            // NPC's gear is a stranger's: the player learns nothing until it is seen in use.
            if (EntadSettings.HideTraits)
                foreach (var trait in comp.activeTraits) trait.Hide();
        }

        // Group generation is called from raids, caravans, quests and map generation. Vanilla's own try/catch only
        // covers the pawns being made, so a failure here would lose the whole group: it costs the group its entads.
        public static void TryGiveEntadsSafe(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> pawns)
        {
            bool wasQuiet = CompEntad.quietReveals;
            CompEntad.quietReveals = true;
            try { TryGiveEntads(parms, groupMaker, pawns); }
            catch (System.Exception e) { Log.ErrorOnce("[Entad Framework] Could not give entads to a pawn group: " + e, 0x45465750); }
            finally { CompEntad.quietReveals = wasQuiet; }
        }
    }

    // The base method every group kind goes through (PawnGroupMaker.GeneratePawns calls it), so raids, visitors,
    // trade caravans and settlement defenders are all covered with one postfix, once per group
    [HarmonyPatch(typeof(PawnGroupKindWorker), nameof(PawnGroupKindWorker.GeneratePawns),
        new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(bool) })]
    public static class Patch_PawnGroupKindWorker_GeneratePawns_Entads
    {
        public static void Postfix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> __result)
        {
            EntadWorldPawns.TryGiveEntadsSafe(parms, groupMaker, __result);
        }
    }
}
