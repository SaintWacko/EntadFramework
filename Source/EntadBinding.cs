using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace EntadFramework
{
    // Binding: an entad bound to one or more pawns works as an ordinary item for anyone, but its traits only
    // function (and only reveal themselves) for a bound pawn or a descendant of one.
    //
    // What "function" covers: everything that has a user. Wearer stat offsets, the item's own stats while held
    // (weapon cooldown, accuracy...), damage effects, granted abilities, equipped and furniture moods, table meal
    // effects and furniture abilities. Item-level effects apply to everyone: durability, market value and building
    // properties (heat, light, fuel, power), and the stats of an item nobody holds.
    public partial class CompEntad
    {
        // Null or empty means unbound: the traits work for everyone, as they always have
        private List<Pawn> boundPawns;

        public bool IsBound => boundPawns != null && boundPawns.Count > 0;

        public IReadOnlyList<Pawn> BoundPawns => (IReadOnlyList<Pawn>)boundPawns ?? System.Array.Empty<Pawn>();

        // Per pawn bloodline answers. Parent relations don't change after birth, and a newborn is a new pawn with
        // no entry, so nothing needs to invalidate this except a change to the bound list. Not saved.
        private Dictionary<int, bool> lineage;

        // The last pawn asked about and the answer: the stat hooks ask about the same holder over and over
        private Pawn lastAsked;
        private bool lastAnswer;

        // Whether the traits work for this pawn. A null pawn means nobody in particular is using the item (stats
        // of an item on the ground, a turret's gun): the traits apply. Unbound items return before any lookup.
        public bool ActiveFor(Pawn pawn)
        {
            if (boundPawns == null || boundPawns.Count == 0 || pawn == null) return true;
            if (pawn == lastAsked) return lastAnswer;
            if (lineage == null) lineage = new Dictionary<int, bool>();
            if (!lineage.TryGetValue(pawn.thingIDNumber, out bool ok))
                lineage[pawn.thingIDNumber] = ok = InBloodline(pawn);
            lastAsked = pawn;
            lastAnswer = ok;
            return ok;
        }

        public bool ActiveForHolder => !IsBound || ActiveFor(Holder);

        // The pawn, or any ancestor through parent relations, is bound. Breadth first with a visited set, so a
        // relation loop made by another mod or the dev tools can't hang it; the depth cap keeps it bounded anyway.
        private bool InBloodline(Pawn pawn)
        {
            var visited = new HashSet<Pawn>();
            var frontier = new List<Pawn> { pawn };
            for (int depth = 0; depth < 16 && frontier.Count > 0; depth++)
            {
                var next = new List<Pawn>();
                foreach (var p in frontier)
                {
                    if (p == null || !visited.Add(p)) continue;
                    if (boundPawns.Contains(p)) return true;
                    var rels = p.relations?.DirectRelations;
                    if (rels == null) continue;
                    for (int i = 0; i < rels.Count; i++)
                        if (rels[i].def == PawnRelationDefOf.Parent && rels[i].otherPawn != null) next.Add(rels[i].otherPawn);
                }
                frontier = next;
            }
            return false;
        }

        // Adds pawns to the bound list. Existing bound pawns stay bound: there is no way to lose a binding in play.
        public void Bind(IEnumerable<Pawn> pawns)
        {
            if (pawns == null) return;
            boundPawns = boundPawns ?? new List<Pawn>();
            bool changed = false;
            foreach (var p in pawns)
            {
                if (p == null || boundPawns.Contains(p)) continue;
                boundPawns.Add(p);
                EntadBindingRegistry.Keep(p);
                changed = true;
            }
            if (changed) BindingChanged();
        }

        public void Unbind()
        {
            if (!IsBound) return;
            boundPawns = null;
            BindingChanged();
        }

        // Binds to the pawn using the item when one of its traits binds on first use and nobody is bound yet.
        // Called on equip (weapons, apparel) and on own use of furniture, before the traits are checked for that pawn.
        public void TryBindOnUse(Pawn pawn)
        {
            // Not while the pawn is being generated: a raider or trader handed an entad at generation would bind it,
            // and the colonist who loots it would get an ordinary item. Binding is for a pawn choosing to use it.
            if (pawn == null || IsBound || !BindsOnFirstUse || PawnGenerator.IsBeingGenerated(pawn)) return;
            Bind(new[] { pawn });
        }

        public bool BindsOnFirstUse
        {
            get
            {
                for (int i = 0; i < activeTraits.Count; i++) if (activeTraits[i].def.bindOnFirstUse) return true;
                return false;
            }
        }

        private void BindingChanged()
        {
            lineage = null;
            lastAsked = null;
            ClearStatCaches();
            wearerOffsets = null;
            Pawn holder = Holder;
            if (holder != null)
            {
                // Abilities and equipped moods follow whether the traits now work for the holder
                EntadAbilities.Revoke(holder, parent);
                EntadAbilities.Grant(holder, this);
                EntadMoods.SyncEquipped(holder);
            }
        }

        // The holder changed: the item's own stats depend on whether the traits work for the new holder
        internal void HolderChanged()
        {
            if (!IsBound) return;
            lastAsked = null;
            ClearStatCaches();
        }

        private void ExposeBinding()
        {
            // References to dead pawns too: an ancestor's death must not break the bloodline. Their world pawns
            // are kept from garbage collection by EntadBindingRegistry.
            // A pawn discarded some other way is never written to the save, so its reference couldn't resolve on load
            if (Scribe.mode == LoadSaveMode.Saving && boundPawns != null) boundPawns.RemoveAll(p => p == null || p.Discarded);
            Scribe_Collections.Look(ref boundPawns, "boundPawns", true, LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && boundPawns != null)
            {
                boundPawns.RemoveAll(p => p == null);
                if (boundPawns.Count == 0) boundPawns = null;
                else foreach (var p in boundPawns) EntadBindingRegistry.Keep(p);
            }
        }

        // "Bound to: A, B" for the inspect pane and the info card
        public string BoundNames => string.Join(", ", boundPawns.Select(p => p.LabelShort));
    }

    // Every pawn any entad has been bound to. World pawn garbage collection discards dead pawns nobody refers to,
    // and their parent relations go with them, which would cut every descendant off from the bloodline. Pawns in
    // this set are reported as critical, so they stay. Rebuilt on load from the items themselves, so nothing extra
    // is saved and a destroyed item's pawns are simply not re-added.
    public static class EntadBindingRegistry
    {
        private static readonly HashSet<Pawn> kept = new HashSet<Pawn>();

        public static void Keep(Pawn pawn)
        {
            if (pawn != null) kept.Add(pawn);
        }

        public static bool IsKept(Pawn pawn) => kept.Count > 0 && kept.Contains(pawn);

        // Bound pawns and every descendant of one. A dead descendant in the middle of the line (a bound pawn's son
        // who died off-map, say) is what links the grandchildren to the binding; vanilla only keeps relatives one
        // step from colonists, so without this the middle of the line could be discarded and the chain cut.
        // Walks parents upward with a visited set and a depth cap. Only called from world pawn GC passes.
        public static bool IsKeptOrDescendant(Pawn pawn)
        {
            if (kept.Count == 0 || pawn == null) return false;
            if (kept.Contains(pawn)) return true;
            if (!pawn.RaceProps.Humanlike) return false;
            var visited = new HashSet<Pawn>();
            var frontier = new List<Pawn> { pawn };
            for (int depth = 0; depth < 16 && frontier.Count > 0; depth++)
            {
                var next = new List<Pawn>();
                foreach (var p in frontier)
                {
                    if (p == null || !visited.Add(p)) continue;
                    if (kept.Contains(p)) return true;
                    var rels = p.relations?.DirectRelations;
                    if (rels == null) continue;
                    for (int i = 0; i < rels.Count; i++)
                        if (rels[i].def == PawnRelationDefOf.Parent && rels[i].otherPawn != null) next.Add(rels[i].otherPawn);
                }
                frontier = next;
            }
            return false;
        }

        public static void Clear() => kept.Clear();
    }

    // A new game or a loaded one starts with an empty set; items loading afterwards re-register their pawns
    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    public static class Patch_Game_LoadGame_ClearBindings
    {
        public static void Prefix() => EntadBindingRegistry.Clear();
    }

    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class Patch_Game_InitNewGame_ClearBindings
    {
        public static void Prefix() => EntadBindingRegistry.Clear();
    }

    // Runs only during world pawn garbage collection passes, never per tick
    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class Patch_WorldPawnGC_KeepBound
    {
        public static void Postfix(Pawn pawn, ref string __result)
        {
            if (__result == null && !pawn.Discarded && EntadBindingRegistry.IsKeptOrDescendant(pawn)) __result = "EntadBound";
        }
    }
}
