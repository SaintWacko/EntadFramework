using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // A caravan pawn wearing, wielding or carrying an entad that holds a room can use what the room holds:
    //
    // Items: the room's loose items, and what its storage buildings hold (a bookcase's books, an outfit stand's
    // clothes), join the caravan's inventory (AllInventoryItems), so the caravan eats the food, uses the medicine and
    // drugs, sells them and pays with the silver. They sit in the space's own ThingOwner (PortableSpace.Contents), so
    // the vanilla code that takes one (SplitOff, Destroy, TryTransferToContainer) removes it from the room. GetOwnerOf
    // names the carrier for them, which vanilla code only uses to call Remove on the carrier's inventory, a no-op here.
    // Adaptive Storage keeps items as ordinary things on the cells, like vanilla shelves, so a captured ASF container's
    // contents are loose room items and come along with nothing special.
    //
    // Weight: room items weigh nothing while they travel with the entad. Every mass bar (trade, split) is worked out
    // by CollectionsMassCalculator.MassUsage over one side's list, so a room item counts there only when its entad (or
    // the pawn wearing it) isn't in that list. Caravan.MassUsage reads pawns' gear and inventory directly and never
    // sees room items.
    //
    // Splitting: room items are listed like any item. Those sent to the side without the entad move into that side's
    // pawns' packs and weigh as usual; those on the entad's side stay in the room. Decided after the split, once the
    // entad's side is known (an entad in someone's pack moves like any item).
    //
    // Beds: every bed set up in the room is a caravan bed (Caravan_BedsTracker.GetUsableBeds), any kind, since the
    // room is already furnished; vanilla's bed_caravansCanUse (bedrolls only) is about carrying a bed, not this.
    //
    // Room items don't tick while held, so food in a room doesn't rot (the caravan's rot estimate still counts it).
    //
    // The Royalty shuttle permit loads AllInventoryItems into the shuttle: room items are held back from that list
    // while it launches, so they stay in their room (Patch_CallShuttleLaunch_Space).
    public static class EntadSpaceCaravan
    {
        internal static bool holdBack;

        // Spaces holding a room, in this game. Pruned when asked; registered by PortableSpace.SetRoom and on load.
        private static readonly List<PortableSpace> holding = new List<PortableSpace>();
        private static Game game;

        public static void Register(PortableSpace space)
        {
            if (game != Current.Game) { holding.Clear(); game = Current.Game; }
            if (!holding.Contains(space)) holding.Add(space);
        }

        // Prunes, then true if any space holds a room. Check before reading Holding.
        public static bool AnyHolding
        {
            get
            {
                if (holding.Count == 0) return false;
                if (game != Current.Game) { holding.Clear(); game = Current.Game; return false; }
                for (int i = holding.Count - 1; i >= 0; i--)
                {
                    var s = holding[i];
                    // A space whose trait isn't linked yet (mid-load) is kept and skipped
                    if (s.trait == null) continue;
                    var item = s.Item;
                    if (s.state != PortableSpaceState.Holding || item == null || item.Destroyed) holding.RemoveAt(i);
                }
                return holding.Count > 0;
            }
        }

        // Read it straight after AnyHolding. Copy it before calling vanilla code that may list a caravan's items.
        internal static List<PortableSpace> Holding => holding;

        // The space a thing sits in: loose in the room, or inside one of the room's buildings
        public static PortableSpace SpaceHolding(Thing t)
        {
            IThingHolder o = t?.holdingOwner?.Owner;
            if (o is PortableSpace s) return s;
            if (o is Thing b) return b.holdingOwner?.Owner as PortableSpace;
            return null;
        }

        public static bool CarriedIn(PortableSpace space, Caravan caravan)
        {
            if (space.trait == null) return false;
            Pawn carrier = space.Carrier;
            return carrier != null && caravan.ContainsPawn(carrier);
        }

        // What a caravan can use from the room: loose items, and what the room's storage buildings hold
        public static void AppendItems(PortableSpace space, List<Thing> list)
        {
            var c = space.Contents;
            for (int i = 0; i < c.Count; i++)
            {
                Thing t = c[i];
                if (Usable(t)) { list.Add(t); continue; }
                // Storage only (IHaulDestination): a gestator's or encoder's ingredients aren't the caravan's to take
                if (!(t is Building) || t is Frame || !(t is IHaulDestination) || !(t is IThingHolder h)) continue;
                var held = h.GetDirectlyHeldThings();
                if (held == null) continue;
                for (int j = 0; j < held.Count; j++)
                    if (held[j].holdingOwner?.Owner == h && Usable(held[j])) list.Add(held[j]);
            }
        }

        private static bool Usable(Thing t) => t.def.category == ThingCategory.Item && !(t is Corpse) && !(t is Pawn);

        // Whether a room travels with the side a mass list describes: its entad is in the list, or the pawn wearing
        // or wielding it is (or carrying it, when that pawn's pack counts on this list)
        public static bool TravelsWith(PortableSpace space, List<ThingCount> list, IgnorePawnsInventoryMode mode)
        {
            Thing item = space.Item;
            if (item == null) return false;
            Pawn carrier = space.Carrier;
            bool inPack = item.ParentHolder is Pawn_InventoryTracker;
            for (int i = 0; i < list.Count; i++)
            {
                var tc = list[i];
                if (tc.Count <= 0) continue;
                if (tc.Thing == item) return true;
                if (carrier != null && tc.Thing == carrier && (!inPack || !InventoryCalculatorsUtility.ShouldIgnoreInventoryOf(carrier, mode))) return true;
            }
            return false;
        }

        // ---- Splitting ----

        internal static bool splitting;
        // How many of each room item the player sent to the new caravan
        private static readonly Dictionary<Thing, int> sentToNew = new Dictionary<Thing, int>();

        internal static void BeginSplit()
        {
            splitting = true;
            sentToNew.Clear();
        }

        internal static void EndSplit()
        {
            splitting = false;
            sentToNew.Clear();
        }

        internal static void SendToNew(Thing item, int count)
        {
            sentToNew.TryGetValue(item, out int n);
            sentToNew[item] = n + count;
        }

        // After the split: each room stays with its entad, and what the player put on the other side moves into
        // that side's packs
        internal static void FinishSplit(Caravan oldCaravan, Caravan newCaravan)
        {
            if (oldCaravan == null || newCaravan == null || !AnyHolding) return;
            bool moved = false;
            var items = new List<Thing>();
            foreach (var space in holding.ToList())
            {
                bool withNew = CarriedIn(space, newCaravan);
                if (!withNew && !CarriedIn(space, oldCaravan)) continue;
                items.Clear();
                AppendItems(space, items);
                foreach (Thing t in items)
                {
                    if (t.Destroyed) continue;
                    sentToNew.TryGetValue(t, out int toNew);
                    toNew = Mathf.Clamp(toNew, 0, t.stackCount);
                    int move = withNew ? t.stackCount - toNew : toNew;
                    if (move <= 0) continue;
                    CaravanInventoryUtility.GiveThing(withNew ? oldCaravan : newCaravan, t.SplitOff(move));
                    moved = true;
                }
            }
            if (!moved) return;
            oldCaravan.RecacheInventory();
            newCaravan.RecacheInventory();
        }
    }

    [HarmonyPatch(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.AllInventoryItems))]
    public static class Patch_CaravanInventory_AllInventoryItems_Space
    {
        public static void Postfix(Caravan caravan, List<Thing> __result)
        {
            if (__result == null || EntadSpaceCaravan.holdBack || !EntadSpaceCaravan.AnyHolding) return;
            var spaces = EntadSpaceCaravan.Holding;
            for (int i = 0; i < spaces.Count; i++)
                if (EntadSpaceCaravan.CarriedIn(spaces[i], caravan)) EntadSpaceCaravan.AppendItems(spaces[i], __result);
        }
    }

    // A room item's owner is the pawn carrying its entad. While a split runs that pawn may already have left the
    // old caravan, so the membership check is skipped then.
    [HarmonyPatch(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.GetOwnerOf))]
    public static class Patch_CaravanInventory_GetOwnerOf_Space
    {
        public static void Postfix(Caravan caravan, Thing item, ref Pawn __result)
        {
            if (__result != null) return;
            var space = EntadSpaceCaravan.SpaceHolding(item);
            if (space == null || space.trait == null) return;
            Pawn carrier = space.Carrier;
            if (carrier != null && (EntadSpaceCaravan.splitting || caravan.ContainsPawn(carrier))) __result = carrier;
        }
    }

    // Vanilla moves an item from its owner's pack, where a room item isn't. During a split the move is only noted
    // (EntadSpaceCaravan.FinishSplit settles it); otherwise the item leaves the room for the chosen pawn's pack.
    [HarmonyPatch(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.MoveInventoryToSomeoneElse))]
    public static class Patch_CaravanInventory_MoveInventory_Space
    {
        public static bool Prefix(Pawn itemOwner, Thing item, List<Pawn> candidates, List<Pawn> ignoreCandidates, int numToMove)
        {
            if (item == null || numToMove <= 0 || numToMove > item.stackCount) return true;
            if (EntadSpaceCaravan.SpaceHolding(item) == null) return true;
            if (EntadSpaceCaravan.splitting)
            {
                EntadSpaceCaravan.SendToNew(item, numToMove);
                return false;
            }
            Pawn to = CaravanInventoryUtility.FindPawnToMoveInventoryTo(item, candidates, ignoreCandidates, itemOwner);
            if (to == null) return false;
            ThingOwner from = item.holdingOwner;
            Thing piece = item.SplitOff(numToMove);
            if (to.inventory.innerContainer.TryAdd(piece) || piece.holdingOwner != null || piece.Destroyed) return false;
            // Part of a stack: back onto the stack it came from, which the room's record already holds
            if (piece != item && !item.Destroyed && item.TryAbsorbStack(piece, false)) return false;
            if (piece == item && from != null && from.TryAdd(piece, false))
            {
                // Back in the room after all: it is still a moved thing (SplitOff's removal cleared the flag)
                if (PortableSpace.BeingMoved != null) PortableSpace.BeingMoved(piece) = true;
                return false;
            }
            // Nowhere to keep it, as vanilla's GiveThing does: a thing with no holder would be dropped from the save
            Log.Error($"[Entad Framework] Could not move {piece} out of a portable space; it was lost.");
            piece.Destroy();
            return false;
        }
    }

    // The shuttle permit's Launch is a local function, compiled to a generated name, so it's found by name at
    // startup. If a game version renames it, Prepare skips the patch and room items ride in the shuttle as loose
    // items, which loses nothing.
    [HarmonyPatch]
    public static class Patch_CallShuttleLaunch_Space
    {
        private static System.Reflection.MethodBase Find()
        {
            var root = typeof(RoyalTitlePermitWorker_CallShuttle);
            foreach (var t in new[] { root }.Concat(root.GetNestedTypes(AccessTools.all)))
                foreach (var m in AccessTools.GetDeclaredMethods(t))
                    if (m.Name.Contains("CallShuttleToCaravan") && m.Name.Contains("g__Launch")) return m;
            return null;
        }

        private static bool warned;

        public static bool Prepare()
        {
            if (Find() != null) return true;
            if (!warned) Log.Warning("[Entad Framework] Shuttle permit launch not found: room items will board a called shuttle as loose items.");
            warned = true;
            return false;
        }
        public static System.Reflection.MethodBase TargetMethod() => Find();
        public static void Prefix() => EntadSpaceCaravan.holdBack = true;
        public static void Finalizer() => EntadSpaceCaravan.holdBack = false;
    }

    [HarmonyPatch(typeof(Dialog_SplitCaravan), "TrySplitCaravan")]
    public static class Patch_SplitCaravan_Space
    {
        private static readonly AccessTools.FieldRef<Dialog_SplitCaravan, Caravan> CaravanOf =
            AccessTools.FieldRefAccess<Dialog_SplitCaravan, Caravan>("caravan");
        private static readonly AccessTools.FieldRef<Dialog_SplitCaravan, List<TransferableOneWay>> TransferablesOf =
            AccessTools.FieldRefAccess<Dialog_SplitCaravan, List<TransferableOneWay>>("transferables");

        // __state: the pawns leaving, read before vanilla takes them out of the list. Null when no room is held.
        public static void Prefix(Dialog_SplitCaravan __instance, out List<Pawn> __state)
        {
            __state = null;
            if (!EntadSpaceCaravan.AnyHolding) return;
            __state = TransferableUtility.GetPawnsFromTransferables(TransferablesOf(__instance));
            EntadSpaceCaravan.BeginSplit();
        }

        public static void Postfix(Dialog_SplitCaravan __instance, bool __result, List<Pawn> __state)
        {
            if (__state == null || !__result) return;
            EntadSpaceCaravan.FinishSplit(CaravanOf(__instance), __state.FirstOrDefault()?.GetCaravan());
        }

        public static void Finalizer() => EntadSpaceCaravan.EndSplit();
    }

    // Beds set up in a carried room are caravan beds
    [HarmonyPatch(typeof(Caravan_BedsTracker), "GetUsableBeds")]
    public static class Patch_CaravanBeds_GetUsableBeds_Space
    {
        public static void Postfix(Caravan_BedsTracker __instance, List<Building_Bed> outBeds)
        {
            Caravan caravan = __instance.caravan;
            if (caravan == null || outBeds == null || !EntadSpaceCaravan.AnyHolding) return;
            var spaces = EntadSpaceCaravan.Holding;
            for (int i = 0; i < spaces.Count; i++)
            {
                if (!EntadSpaceCaravan.CarriedIn(spaces[i], caravan)) continue;
                var c = spaces[i].Contents;
                for (int j = 0; j < c.Count; j++)
                    if (c[j] is Building_Bed bed)
                        for (int k = 0; k < bed.SleepingSlotsCount; k++) outBeds.Add(bed);
            }
        }
    }

    // Room items weigh nothing on the side of a mass list their entad travels on
    [HarmonyPatch(typeof(CollectionsMassCalculator), nameof(CollectionsMassCalculator.MassUsage),
        new[] { typeof(List<ThingCount>), typeof(IgnorePawnsInventoryMode), typeof(bool), typeof(bool) })]
    public static class Patch_MassUsage_Space
    {
        public static void Postfix(List<ThingCount> thingCounts, IgnorePawnsInventoryMode ignoreInventory, ref float __result)
        {
            if (__result <= 0f || thingCounts == null || !EntadSpaceCaravan.AnyHolding) return;
            float weightless = 0f;
            for (int i = 0; i < thingCounts.Count; i++)
            {
                var tc = thingCounts[i];
                if (tc.Count <= 0 || tc.Thing == null || tc.Thing is Pawn) continue;
                var space = EntadSpaceCaravan.SpaceHolding(tc.Thing);
                if (space != null && EntadSpaceCaravan.TravelsWith(space, thingCounts, ignoreInventory))
                    weightless += tc.Thing.GetStatValue(StatDefOf.Mass) * tc.Count;
            }
            if (weightless > 0f) __result = Mathf.Max(0f, __result - weightless);
        }
    }
}
