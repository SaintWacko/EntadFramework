using System.Collections.Generic;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace EntadFramework
{
    // An aura on an entad trait (EntadTraitDef.aura), ported from Bastle Framework's BastleTrait_Aura. Every
    // intervalTicks the source gives a hediff to the pawns around it that pass the filters below, or refreshes the
    // HediffComp_Disappears on one they already have, so the effect fades soon after a pawn walks out of range.
    // With severityPerDay it builds up instead, like toxic fallout.
    //
    // The source is the pawn wearing or wielding the item, or the furniture itself:
    // - Gear works only while carried by a pawn the item is active for (EntadBinding), and allegiance is the
    //   carrier's.
    // - Furniture works only while spawned and, if it uses power, powered. A bound furniture aura reaches everyone
    //   in range, but only while one of its bound pawns (or their descendants) is in range too. Allegiance is the
    //   building's faction.
    public class EntadAura
    {
        public HediffDef hediff;

        // Tiles. A trait that scales with rarity covers an area that grows with the rarity multiplier, so the
        // radius grows with its square root (x1.5 at a rarity whose multiplier is 2.25 times the trait's own).
        public float radius = 5.9f;

        // How often it reaches out, in ticks (60 is a second). Each source scans the map's pawns once per interval.
        public int intervalTicks = 60;

        // Walls, closed doors and other sight-blocking buildings stop it. The selection ring is drawn to match.
        public bool blockedByWalls = true;

        // Above 0: add this much severity per day of exposure instead of giving and refreshing the hediff
        public float severityPerDay;

        // Buildup only: each stat's (1 - value) on the target multiplies the severity, as ToxicResistance does
        public List<StatDef> resistStats;

        // Who. Allies are the source's faction and factions allied to it; enemies are anyone hostile to it;
        // neutrals are everyone else. Self is the carrier of an aura on gear (furniture has no self).
        public bool affectsSelf;
        public bool affectsAllies;
        public bool affectsNeutrals;
        public bool affectsEnemies;
        public bool affectsHumanlikes = true;
        public bool affectsAnimals = true;
        public bool affectsMechanoids;
        public bool affectsEntities;

        // When (gear only: furniture never sleeps or fights)
        public bool whileDowned;
        public bool whileAsleep = true;
        // Only while the carrier is drafted, in an aggressive mental state, holding an enemy target, or within
        // 20 seconds of fighting. Gear only.
        public bool onlyInCombat;

        // Visuals. The range ring shows while the carrier or the furniture is selected, once the aura is known.
        public bool showRing = true;
        public Color ringColor = Color.white;
        // A fleck spawned on the source every pulseIntervalTicks while it is active and known (e.g. PsycastAreaEffect)
        public FleckDef pulseFleck;
        public int pulseIntervalTicks = 150;
        // Below 0: sized from the radius
        public float pulseScale = -1f;

        public bool AffectsSomeone => affectsSelf || affectsAllies || affectsNeutrals || affectsEnemies;

        public IEnumerable<string> ConfigErrors(string defName)
        {
            if (hediff == null) yield return $"{defName}: aura has no hediff";
            if (radius <= 0f || radius > GenRadial.MaxRadialPatternRadius)
                yield return $"{defName}: aura radius {radius} must be above 0 and at most {GenRadial.MaxRadialPatternRadius}";
            if (intervalTicks < 1) yield return $"{defName}: aura intervalTicks must be at least 1";
            if (pulseFleck != null && pulseIntervalTicks < 1) yield return $"{defName}: aura pulseIntervalTicks must be at least 1";
            if (!AffectsSomeone) yield return $"{defName}: aura affects nobody: set affectsAllies, affectsEnemies, affectsNeutrals or affectsSelf";
            if (hediff != null && severityPerDay <= 0f)
            {
                HediffCompProperties_Disappears d = null;
                if (hediff.comps != null)
                    foreach (var c in hediff.comps) if (c is HediffCompProperties_Disappears dc) { d = dc; break; }
                if (d == null)
                    yield return $"{defName}: aura hediff {hediff.defName} has no HediffCompProperties_Disappears, so it never wears off after leaving the aura (or set severityPerDay for a buildup)";
                // +15: off-camera pawns tick in batches, so a refresh can be a little late
                else if (d.disappearsAfterTicks.min <= intervalTicks + 15)
                    yield return $"{defName}: aura hediff {hediff.defName} can wear off ({d.disappearsAfterTicks.min} ticks) before the aura refreshes it ({intervalTicks} ticks), so it will flicker";
            }
        }
    }

    // Runs every aura in the game. Only items holding an aura trait are registered (on trait changes and on load),
    // so a game without one costs an empty-list check per tick. Each registered item is looked at once per its
    // interval, staggered by its thing ID, and then only does work when its source is on a map: one pass over the
    // map's spawned pawns with a distance check, and a line of sight check for those in range.
    public class EntadAuras : GameComponent
    {
        private const int CombatMemoryTicks = 1200;

        // Not saved: CompEntad registers itself after loading. Static so CompEntad can reach it while a save is
        // still loading; the constructor runs for every new or loaded game, before any of its things load.
        private static readonly List<CompEntad> sources = new List<CompEntad>();
        private static readonly List<Pawn> tmpHit = new List<Pawn>();

        public EntadAuras(Game game) { sources.Clear(); }

        public static void Refresh(CompEntad comp)
        {
            bool has = false;
            for (int i = 0; i < comp.activeTraits.Count && !has; i++) has = comp.activeTraits[i].def.aura != null;
            if (has) { if (!sources.Contains(comp)) sources.Add(comp); }
            else sources.Remove(comp);
        }

        public static float Radius(AppliedEntadTrait trait)
        {
            float r = trait.def.aura.radius;
            if (trait.def.scalesWithRarity) r *= Mathf.Sqrt(trait.def.RarityScale(trait.Rarity));
            return Mathf.Min(r, GenRadial.MaxRadialPatternRadius);
        }

        // The pawn carrying the item or the furniture, when the aura can work there right now
        private static Thing SourceOf(CompEntad comp)
        {
            Thing item = comp.parent;
            if (item.Spawned)
            {
                if (item.def.building == null) return null;
                var power = item.TryGetComp<CompPowerTrader>();
                return power == null || power.PowerOn ? item : null;
            }
            Pawn holder = comp.Holder;
            return holder != null && holder.Spawned && comp.ActiveFor(holder) ? holder : null;
        }

        public override void GameComponentTick()
        {
            if (sources.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                var comp = sources[i];
                if (comp?.parent == null || comp.parent.Destroyed) { sources.RemoveAt(i); continue; }
                Thing source = null;
                for (int t = 0; t < comp.activeTraits.Count; t++)
                {
                    var trait = comp.activeTraits[t];
                    var aura = trait.def.aura;
                    if (aura == null) continue;
                    int phase = now + comp.parent.thingIDNumber;
                    bool doEffect = phase % aura.intervalTicks == 0;
                    bool doPulse = aura.pulseFleck != null && phase % aura.pulseIntervalTicks == 0;
                    if (!doEffect && !doPulse) continue;
                    if (source == null) source = SourceOf(comp);
                    if (source == null) break;
                    if (source is Pawn p && !PawnActive(p, aura)) continue;
                    try
                    {
                        if (doEffect) Apply(comp, trait, source);
                        if (doPulse) Pulse(trait, source);
                    }
                    catch (System.Exception e)
                    {
                        // One broken aura (a modded hediff that throws) mustn't stop every other one
                        Log.ErrorOnce($"[Entad Framework] aura {trait.def.defName} on {comp.parent} failed: {e}", trait.def.shortHash ^ 0x5A17);
                    }
                }
            }
        }

        private static bool PawnActive(Pawn pawn, EntadAura aura)
        {
            if (pawn.Dead || (!aura.whileDowned && pawn.Downed) || (!aura.whileAsleep && !pawn.Awake())) return false;
            return !aura.onlyInCombat || InCombat(pawn);
        }

        private static bool InCombat(Pawn p)
        {
            if (p.Drafted || p.InAggroMentalState) return true;
            var m = p.mindState;
            if (m == null) return false;
            int now = Find.TickManager.TicksGame;
            return m.enemyTarget != null || now - m.lastEngageTargetTick < CombatMemoryTicks || now - m.lastAttackTargetTick < CombatMemoryTicks;
        }

        private static void Apply(CompEntad comp, AppliedEntadTrait trait, Thing source)
        {
            var aura = trait.def.aura;
            if (aura.hediff == null) return;
            Map map = source.Map;
            IntVec3 center = source.Position;
            CellRect rect = source.OccupiedRect();
            float radius = Radius(trait);
            // Bound furniture works for everyone, but only while someone it is bound to is in range
            bool needBound = source == comp.parent && comp.IsBound;
            bool boundHere = false;
            tmpHit.Clear();
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var p = pawns[i];
                if (p.Dead || !InRange(rect, p.Position, radius)) continue;
                bool affects = Affects(source, p, aura);
                bool check = affects || (needBound && !boundHere);
                if (!check) continue;
                if (aura.blockedByWalls && p != source && !Sees(rect, p.Position, map)) continue;
                if (needBound && !boundHere && comp.ActiveFor(p)) boundHere = true;
                if (affects) tmpHit.Add(p);
            }
            if (needBound && !boundHere) { tmpHit.Clear(); return; }
            bool any = false;
            // Backwards and from a copy: a buildup can kill, which changes the map's pawn list
            for (int i = tmpHit.Count - 1; i >= 0; i--)
                if (!tmpHit[i].Dead && Give(tmpHit[i], aura)) any = true;
            tmpHit.Clear();
            if (any && !trait.IsRevealed(EntadEffectKind.Aura) && !center.Fogged(map))
                trait.Reveal(EntadEffectKind.Aura, announce: source.Faction == Faction.OfPlayer);
        }

        // Range and sight are measured from the source's whole footprint, so a 3x3 bed reaches as far on every side
        // and its own cells don't block it. A pawn's footprint is its one cell.
        private static bool InRange(CellRect rect, IntVec3 c, float radius) => rect.ClosestCellTo(c).InHorDistOf(c, radius);

        private static bool Sees(CellRect rect, IntVec3 c, Map map) =>
            GenSight.LineOfSight(rect.ClosestCellTo(c), c, map, rect, CellRect.SingleCell(c));

        private static readonly List<IntVec3> ringCells = new List<IntVec3>();

        // The cells in range (and in sight, when walls block it), outlined. Only while a source is selected.
        private static void DrawRing(Thing source, float radius, EntadAura aura, Map map)
        {
            CellRect rect = source.OccupiedRect();
            ringCells.Clear();
            foreach (IntVec3 c in rect.ExpandedBy(Mathf.CeilToInt(radius)).ClipInsideMap(map))
                if (InRange(rect, c, radius) && (!aura.blockedByWalls || rect.Contains(c) || Sees(rect, c, map))) ringCells.Add(c);
            GenDraw.DrawFieldEdges(ringCells, aura.ringColor);
            ringCells.Clear();
        }

        public static bool Affects(Thing source, Pawn p, EntadAura aura)
        {
            var r = p.RaceProps;
            bool raceOk = r.IsAnomalyEntity ? aura.affectsEntities
                : r.IsMechanoid ? aura.affectsMechanoids
                : r.Humanlike ? aura.affectsHumanlikes
                : aura.affectsAnimals;
            if (!raceOk) return false;
            if (p == source) return aura.affectsSelf;
            if (p.HostileTo(source)) return aura.affectsEnemies;
            Faction f = source.Faction;
            bool ally = f != null && (p.Faction == f || (p.Faction != null && p.Faction.RelationKindWith(f) == FactionRelationKind.Ally));
            return ally ? aura.affectsAllies : aura.affectsNeutrals;
        }

        private static bool Give(Pawn p, EntadAura aura)
        {
            if (p.health == null) return false;
            if (aura.severityPerDay > 0f)
            {
                float amount = aura.severityPerDay * aura.intervalTicks / GenDate.TicksPerDay;
                if (aura.resistStats != null)
                    for (int i = 0; i < aura.resistStats.Count; i++)
                        amount *= Mathf.Max(0f, 1f - p.GetStatValue(aura.resistStats[i]));
                if (amount <= 0f) return false;
                HealthUtility.AdjustSeverity(p, aura.hediff, amount);
                return true;
            }
            var h = p.health.hediffSet.GetFirstHediffOfDef(aura.hediff);
            if (h == null) p.health.AddHediff(aura.hediff);
            else h.TryGetComp<HediffComp_Disappears>()?.ResetElapsedTicks();
            return true;
        }

        private static void Pulse(AppliedEntadTrait trait, Thing source)
        {
            var aura = trait.def.aura;
            Map map = source.Map;
            if (map != Find.CurrentMap || !trait.IsRevealed(EntadEffectKind.Aura) || source.Position.Fogged(map)) return;
            // PsycastAreaEffect is 2.3 tiles across at scale 1 and grows as it fades
            float scale = aura.pulseScale > 0f ? aura.pulseScale : Radius(trait) * 0.7f;
            FleckMaker.Static(source.DrawPos, map, aura.pulseFleck, scale);
        }

        // The range ring of each known aura on the selected pawns' gear and on selected furniture. Per frame, but
        // returns at once when no aura exists, and only draws for sources that are selected.
        public override void GameComponentUpdate()
        {
            if (sources.Count == 0 || WorldRendererUtility.WorldRendered) return;
            Map map = Find.CurrentMap;
            if (map == null) return;
            for (int i = 0; i < sources.Count; i++)
            {
                var comp = sources[i];
                if (comp?.parent == null || comp.parent.Destroyed) continue;
                Thing source = comp.parent.Spawned ? comp.parent : comp.Holder;
                if (source == null || !source.Spawned || source.Map != map || !Find.Selector.IsSelected(source)) continue;
                for (int t = 0; t < comp.activeTraits.Count; t++)
                {
                    var trait = comp.activeTraits[t];
                    var aura = trait.def.aura;
                    if (aura == null || !aura.showRing || !trait.IsRevealed(EntadEffectKind.Aura)) continue;
                    DrawRing(source, Radius(trait), aura, map);
                }
            }
        }
    }
}
