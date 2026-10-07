using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using Verse.Sound;

namespace EntadFramework
{
    // Entad appraisers: a member of a visiting trade caravan, or someone at a faction settlement, who identifies
    // entads for silver. Identifying reveals every hidden trait of one item; the price is per item. Each appraiser
    // has a daily maximum (rolled once) and a number for today (rolled at local midnight, between the settings'
    // minimum and their maximum). Nothing ticks: today's number is rerolled the first time it's asked for on a new day.

    // Per-faction override of the settings' chances, for content mods (a temple faction that always has one).
    // A negative value (the default) means "use the settings".
    public class EntadFactionExtension : DefModExtension
    {
        public float appraiserCaravanChance = -1f;
        public float appraiserSettlementChance = -1f;
    }

    public class AppraiserState : IExposable
    {
        public int max;          // 0: no appraiser (a settlement remembers a failed roll, so revisiting can't reroll it)
        public int left;
        public int day = -1;

        public bool Exists => max > 0;

        public static AppraiserState Roll(float chance) =>
            Rand.Chance(chance) ? new AppraiserState { max = Mathf.Max(1, EntadSettings.AppraiserMax.RandomInRange) } : new AppraiserState();

        public int LeftToday(PlanetTile tile)
        {
            if (!Exists) return 0;
            int today = LocalDay(tile);
            if (today != day)
            {
                day = today;
                left = Rand.RangeInclusive(Mathf.Min(EntadSettings.AppraiserDailyMin, max), max);
            }
            return left;
        }

        public void Use() { if (left > 0) left--; }

        // Days since the game began, counted from midnight at this tile's longitude
        public static int LocalDay(PlanetTile tile)
        {
            float longitude = tile.Valid ? Find.WorldGrid.LongLatOf(tile).x : 0f;
            return (int)((Find.TickManager.TicksAbs + GenDate.LocalTicksOffsetFromLongitude(longitude)) / GenDate.TicksPerDay);
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref max, "max");
            Scribe_Values.Look(ref left, "left");
            Scribe_Values.Look(ref day, "day", -1);
        }
    }

    // Marks a caravan member as an appraiser and carries their counts, so they leave and save with the pawn
    public class Hediff_EntadAppraiser : Hediff
    {
        public AppraiserState state = new AppraiserState();

        public override void PostAdd(DamageInfo? dinfo)
        {
            base.PostAdd(dinfo);
            EntadAppraisal.Register(pawn);
        }

        public override void PostRemoved()
        {
            base.PostRemoved();
            EntadAppraisal.Unregister(pawn);
        }

        public override string TipStringExtra => "EF_Appraiser_LeftToday".Translate(state.LeftToday(pawn.Tile), state.max);

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Deep.Look(ref state, "state");
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (state == null) state = AppraiserState.Roll(1f);
                EntadAppraisal.Register(pawn);
            }
        }
    }

    // Settlement appraisers, by settlement ID. Rolled the first time a caravan visiting the settlement asks.
    public class EntadAppraisalWorld : WorldComponent
    {
        private Dictionary<int, AppraiserState> settlements = new Dictionary<int, AppraiserState>();
        private List<int> keys;
        private List<AppraiserState> values;

        public EntadAppraisalWorld(World world) : base(world) { }

        public AppraiserState For(Settlement s)
        {
            if (!settlements.TryGetValue(s.ID, out var state))
                settlements[s.ID] = state = AppraiserState.Roll(EntadAppraisal.SettlementChance(s.Faction));
            return state;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref settlements, "settlements", LookMode.Value, LookMode.Deep, ref keys, ref values);
            if (settlements == null) settlements = new Dictionary<int, AppraiserState>();
        }
    }

    [DefOf]
    public static class EntadAppraisalDefOf
    {
        public static HediffDef Entad_Appraiser;
        public static JobDef Entad_Appraise;

        static EntadAppraisalDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadAppraisalDefOf)); }
    }

    [StaticConstructorOnStartup]
    public static class EntadAppraisal
    {
        public static readonly Texture2D CommandIcon = ContentFinder<Texture2D>.Get("UI/Commands/Trade");

        // Appraisers that may be on a map. The question-mark check runs for every pawn label drawn each frame, so it
        // starts with an empty check and a set lookup before touching hediffs.
        private static readonly HashSet<Pawn> appraisers = new HashSet<Pawn>();

        public static void Register(Pawn p)
        {
            if (p == null) return;
            appraisers.RemoveWhere(x => x.Discarded || x.Dead);
            appraisers.Add(p);
        }

        public static void Unregister(Pawn p) => appraisers.Remove(p);

        public static void Reset() => appraisers.Clear();

        public static float CaravanChance(Faction f)
        {
            float c = f?.def.GetModExtension<EntadFactionExtension>()?.appraiserCaravanChance ?? -1f;
            return c >= 0f ? c : EntadSettings.CaravanAppraiserChance;
        }

        public static float SettlementChance(Faction f)
        {
            float c = f?.def.GetModExtension<EntadFactionExtension>()?.appraiserSettlementChance ?? -1f;
            return c >= 0f ? c : EntadSettings.SettlementAppraiserChance;
        }

        // Per item: a share of the player's wealth (all home maps and caravans, the figure raids are sized from), never
        // below the minimum
        public static int Price => Mathf.Max(EntadSettings.IdentifyMinPrice, Mathf.RoundToInt(WealthUtility.PlayerWealth * EntadSettings.IdentifyWealthFraction));

        // A caravan appraiser who can be talked to right now: on the map, able, friendly, and with a caravan that's
        // still trading (once its trader can't trade, the caravan is leaving or has been dismissed)
        public static bool CanAppraiseNow(Pawn p, out Hediff_EntadAppraiser hediff)
        {
            hediff = null;
            if (appraisers.Count == 0 || p == null || !appraisers.Contains(p)) return false;
            if (!p.Spawned || p.Dead || p.Downed || p.InMentalState || p.Faction == null || p.Faction.IsPlayer || p.Faction.HostileTo(Faction.OfPlayer)) return false;
            var lord = p.GetLord();
            if (!(lord?.LordJob is LordJob_TradeWithColony)) return false;
            bool trading = false;
            foreach (var o in lord.ownedPawns)
                if (o.TraderKind != null && o.CanTradeNow) { trading = true; break; }
            if (!trading) return false;
            hediff = p.health.hediffSet.GetFirstHediff<Hediff_EntadAppraiser>();
            return hediff != null;
        }

        // Settlement appraisal: only for the settlement this caravan is visiting, while trade there is possible
        public static Command SettlementCommand(Caravan caravan, Settlement s)
        {
            if (s.Faction == null || s.Faction.IsPlayer || s.Faction.HostileTo(Faction.OfPlayer) || !s.CanTradeNow) return null;
            if (CaravanVisitUtility.SettlementVisitedNow(caravan) != s) return null;
            var state = Find.World.GetComponent<EntadAppraisalWorld>()?.For(s);
            if (state == null || !state.Exists) return null;
            return new Command_Action
            {
                defaultLabel = "EF_Appraise_Command".Translate(),
                defaultDesc = "EF_Appraise_CommandDesc".Translate(s.LabelCap),
                icon = CommandIcon,
                action = () => Find.WindowStack.Add(new Dialog_EntadAppraisal(caravan, s, state))
            };
        }

        private static Thing Unwrap(Thing t) => t is MinifiedThing m ? m.InnerThing : t;

        private static void AddIfUnidentified(Thing t, List<Thing> into)
        {
            t = Unwrap(t);
            if (t == null || into.Contains(t)) return;
            var comp = t.TryGetComp<CompEntad>();
            if (comp != null && !comp.activeTraits.NullOrEmpty() && comp.HasHidden) into.Add(t);
        }

        private static void AddGear(Pawn p, List<Thing> into)
        {
            if (p.equipment != null) foreach (var t in p.equipment.AllEquipmentListForReading) AddIfUnidentified(t, into);
            if (p.apparel != null) foreach (var t in p.apparel.WornApparel) AddIfUnidentified(t, into);
            if (p.inventory != null) foreach (var t in p.inventory.innerContainer) AddIfUnidentified(t, into);
        }

        // What vanilla would let you sell to a visiting caravan (items in the home area or in storage), plus what
        // the colony's pawns on the map carry, plus the colony's installed buildings
        public static List<Thing> CandidatesOnMap(Map map)
        {
            var list = new List<Thing>();
            foreach (var t in map.listerThings.AllThings)
            {
                if (!(t is MinifiedThing) && !EntadCompInjector.MayHaveComp(t.def)) continue;
                if (t.Position.Fogged(map)) continue;
                if (t.def.category == ThingCategory.Item)
                {
                    if (map.areaManager.Home[t.Position] || t.IsInAnyStorage()) AddIfUnidentified(t, list);
                }
                else if (t.def.category == ThingCategory.Building && t.Faction == Faction.OfPlayer) AddIfUnidentified(t, list);
            }
            foreach (var p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer)) AddGear(p, list);
            return list;
        }

        public static List<Thing> CandidatesInCaravan(Caravan caravan)
        {
            var list = new List<Thing>();
            foreach (var p in caravan.PawnsListForReading) AddGear(p, list);
            foreach (var t in CaravanInventoryUtility.AllInventoryItems(caravan)) AddIfUnidentified(t, list);
            return list;
        }

        public static List<Thing> SilverOnMap(Map map)
        {
            var list = new List<Thing>();
            foreach (var t in map.listerThings.ThingsOfDef(ThingDefOf.Silver))
                if (!t.Position.Fogged(map) && (map.areaManager.Home[t.Position] || t.IsInAnyStorage())) list.Add(t);
            return list;
        }

        public static List<Thing> SilverInCaravan(Caravan caravan) =>
            CaravanInventoryUtility.AllInventoryItems(caravan).Where(t => t.def == ThingDefOf.Silver).ToList();

        // Takes the silver from the given stacks. A caravan appraiser keeps it (it leaves with them); at a
        // settlement it's simply gone.
        public static void Pay(List<Thing> silver, int amount, Pawn receiver)
        {
            int remaining = amount;
            foreach (var stack in silver.ToList())
            {
                if (remaining <= 0) break;
                if (stack.Destroyed) continue;
                int take = Mathf.Min(stack.stackCount, remaining);
                var part = stack.SplitOff(take);
                remaining -= take;
                if (receiver?.inventory == null || !receiver.inventory.innerContainer.TryAdd(part)) part.Destroy();
            }
        }

        public static void Identify(Thing item, CompEntad comp)
        {
            var labels = comp.activeTraits.Where(m => m.AnyHidden).Select(m => m.def.label).ToList();
            comp.RevealAll();
            Messages.Message("EF_Appraise_Done".Translate(item.LabelNoCount, labels.ToCommaList(true)),
                item.SpawnedOrAnyParentSpawned ? new LookTargets(item) : LookTargets.Invalid, MessageTypeDefOf.PositiveEvent, false);
        }
    }

    public class Dialog_EntadAppraisal : Window
    {
        private readonly Map map;
        private readonly Caravan caravan;
        private readonly Pawn appraiser;
        private readonly AppraiserState state;
        private readonly PlanetTile tile;
        private readonly string title;
        private List<Thing> items;
        private List<Thing> silver;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(620f, 560f);

        private Dialog_EntadAppraisal()
        {
            forcePause = true;
            absorbInputAroundWindow = true;
            doCloseButton = true;
            doCloseX = true;
            closeOnClickedOutside = true;
        }

        public Dialog_EntadAppraisal(Pawn appraiser, Hediff_EntadAppraiser hediff) : this()
        {
            this.appraiser = appraiser;
            map = appraiser.Map;
            state = hediff.state;
            tile = map.Tile;
            title = "EF_Appraise_Title".Translate(appraiser.LabelShort);
            Refresh();
        }

        public Dialog_EntadAppraisal(Caravan caravan, Settlement settlement, AppraiserState state) : this()
        {
            this.caravan = caravan;
            this.state = state;
            tile = settlement.Tile;
            title = "EF_Appraise_TitleSettlement".Translate(settlement.LabelCap);
            Refresh();
        }

        private void Refresh()
        {
            items = map != null ? EntadAppraisal.CandidatesOnMap(map) : EntadAppraisal.CandidatesInCaravan(caravan);
            silver = map != null ? EntadAppraisal.SilverOnMap(map) : EntadAppraisal.SilverInCaravan(caravan);
        }

        public override void DoWindowContents(Rect inRect)
        {
            int left = state.LeftToday(tile);
            int price = EntadAppraisal.Price;
            int available = 0;
            foreach (var s in silver) if (!s.Destroyed) available += s.stackCount;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), title);
            Text.Font = GameFont.Small;
            float y = inRect.y + 38f;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "EF_Appraise_LeftToday".Translate(left));
            y += 24f;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "EF_Appraise_PriceLine".Translate(((float)price).ToStringMoney(), ((float)available).ToStringMoney()));
            y += 30f;

            var outRect = new Rect(inRect.x, y, inRect.width, inRect.height - y + inRect.y - CloseButSize.y - 10f);
            if (items.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(outRect, "EF_Appraise_None".Translate());
                GUI.color = Color.white;
                return;
            }

            const float rowH = 34f;
            var view = new Rect(0f, 0f, outRect.width - 16f, items.Count * rowH);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            Thing identified = null;
            for (int i = 0; i < items.Count; i++)
            {
                var t = items[i];
                var row = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 3f, 28f, 28f), t);
                var buttonRect = new Rect(row.xMax - 150f, row.y + 3f, 150f, 28f);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 36f, row.y, buttonRect.x - row.x - 40f, rowH), t.LabelCap.Truncate(buttonRect.x - row.x - 40f));
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.InfoCardButton(buttonRect.x - 28f, row.y + 5f, t);
                bool can = left > 0 && available >= price;
                if (Widgets.ButtonText(buttonRect, "EF_Appraise_Button".Translate(((float)price).ToStringMoney()), active: can) && can)
                    identified = t;
                if (!can) TooltipHandler.TipRegion(buttonRect, left <= 0 ? "EF_Appraise_NoneLeft".Translate() : "EF_Appraise_NoSilver".Translate());
            }
            Widgets.EndScrollView();

            if (identified != null)
            {
                var comp = identified.TryGetComp<CompEntad>();
                if (comp != null && comp.HasHidden)
                {
                    EntadAppraisal.Pay(silver, price, appraiser);
                    state.Use();
                    EntadAppraisal.Identify(identified, comp);
                    SoundDefOf.ExecuteTrade.PlayOneShotOnCamera();
                }
                Refresh();
            }
        }
    }

    // Right-click an appraiser with a colonist, like trading with a caravan's trader
    public class FloatMenuOptionProvider_EntadAppraise : FloatMenuOptionProvider
    {
        protected override bool Drafted => true;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;

        protected override FloatMenuOption GetSingleOptionFor(Pawn clickedPawn, FloatMenuContext context)
        {
            if (!EntadAppraisal.CanAppraiseNow(clickedPawn, out _)) return null;
            var me = context.FirstSelectedPawn;
            string label = "EF_Appraise_Option".Translate(clickedPawn.LabelShort);
            if (!me.CanReach(clickedPawn, PathEndMode.Touch, Danger.Deadly))
                return new FloatMenuOption(label + ": " + "NoPath".Translate().CapitalizeFirst(), null);
            if (!me.health.capacities.CapableOf(PawnCapacityDefOf.Talking))
                return new FloatMenuOption("EF_Appraise_CannotTalk".Translate(label), null);
            return FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(label, () =>
            {
                var job = JobMaker.MakeJob(EntadAppraisalDefOf.Entad_Appraise, clickedPawn);
                job.playerForced = true;
                me.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }, MenuOptionPriority.InitiateSocial, null, clickedPawn), me, clickedPawn);
        }
    }

    // Walk up to the appraiser, then open the window (JobDriver_TradeWithPawn's shape)
    public class JobDriver_EntadAppraise : JobDriver
    {
        private Pawn Appraiser => (Pawn)TargetThingA;

        public override bool TryMakePreToilReservations(bool errorOnFailed) => pawn.Reserve(Appraiser, job, 1, -1, null, errorOnFailed);

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedOrNull(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch).FailOn(() => !EntadAppraisal.CanAppraiseNow(Appraiser, out _));
            var talk = ToilMaker.MakeToil("EntadAppraise");
            talk.initAction = () =>
            {
                if (EntadAppraisal.CanAppraiseNow(Appraiser, out var hediff))
                    Find.WindowStack.Add(new Dialog_EntadAppraisal(Appraiser, hediff));
            };
            yield return talk;
        }
    }

    // A trade caravan may bring an appraiser: one of its guards (the trader if it has none), so the caravan's size
    // and strength don't change
    [HarmonyPatch(typeof(PawnGroupKindWorker_Trader), "GeneratePawns")]
    public static class Patch_TraderGeneratePawns_EntadAppraiser
    {
        public static void Postfix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, List<Pawn> outPawns)
        {
            if (parms?.faction == null || parms.faction.IsPlayer || outPawns.NullOrEmpty()) return;
            if (!Rand.Chance(EntadAppraisal.CaravanChance(parms.faction))) return;
            bool Eligible(Pawn p) => p.RaceProps.Humanlike && p.Faction == parms.faction && !p.IsPrisoner && !p.IsSlave && !p.Dead;
            var guards = outPawns.Where(p => Eligible(p) && p.TraderKind == null
                && groupMaker?.guards != null && groupMaker.guards.Any(g => g.kind == p.kindDef)).ToList();
            Pawn chosen = guards.Count > 0 ? guards.RandomElement() : outPawns.FirstOrDefault(p => Eligible(p) && p.TraderKind != null);
            if (chosen == null) return;
            var hediff = (Hediff_EntadAppraiser)HediffMaker.MakeHediff(EntadAppraisalDefOf.Entad_Appraiser, chosen);
            hediff.state = AppraiserState.Roll(1f);
            chosen.health.AddHediff(hediff);
        }
    }

    // The question mark over the head, like a caravan's trader
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ShouldShowQuestionMark))]
    public static class Patch_ShouldShowQuestionMark_EntadAppraiser
    {
        public static void Postfix(Pawn __instance, ref bool __result)
        {
            if (!__result && EntadAppraisal.CanAppraiseNow(__instance, out _)) __result = true;
        }
    }

    [HarmonyPatch(typeof(Settlement), nameof(Settlement.GetCaravanGizmos))]
    public static class Patch_SettlementGizmos_EntadAppraiser
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Settlement __instance, Caravan caravan)
        {
            foreach (var g in __result) yield return g;
            var cmd = EntadAppraisal.SettlementCommand(caravan, __instance);
            if (cmd != null) yield return cmd;
        }
    }
}
