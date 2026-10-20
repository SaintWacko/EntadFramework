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

        // With Ideology: Combat Command's soft ground glow, sized to the radius, while the aura is active and known.
        // It is a plain circle that ignores walls, and it replaces the selection ring while it is up. The ring (which
        // does stop at walls) shows instead without Ideology, with aura effects off in the settings, or while the
        // aura is idle.
        public bool showGlow = true;
        // Unset (alpha 0) uses ringColor
        public Color glowColor = new Color(0f, 0f, 0f, 0f);
        // The glow is additive, so the colour is scaled down by this. 0.125 is about Combat Command's brightness.
        public float glowStrength = 0.125f;
        // A different attached mote (a MoteAttached) to use as the glow. Defaults to Entad_Mote_AuraGlow.
        public ThingDef glowMote;

        // With Ideology: Combat Command's line from each pawn it affects back to the source, while it keeps
        // affecting them. Skipped when the hediff has its own HediffComp_Link, which draws the line instead.
        public bool showLinks = true;
        // Unset (alpha 0) uses ringColor
        public Color linkColor = new Color(0f, 0f, 0f, 0f);
        // A different line mote (a MoteDualAttached). Defaults to Entad_Mote_AuraLink.
        public ThingDef linkMote;

        // Null without Ideology (the default motes don't load), with the effect switched off here or in the settings
        public ThingDef GlowDef => showGlow && EntadSettings.AuraEffects ? glowMote ?? EntadAuraDefOf.Entad_Mote_AuraGlow : null;

        public ThingDef LinkDef => showLinks && EntadSettings.AuraEffects && !(hediff?.HasComp(typeof(HediffComp_Link)) ?? false)
            ? linkMote ?? EntadAuraDefOf.Entad_Mote_AuraLink : null;

        public Color LinkInstanceColor => linkColor.a > 0f ? linkColor : ringColor;

        public Color GlowInstanceColor
        {
            get
            {
                var c = glowColor.a > 0f ? glowColor : ringColor;
                return new Color(c.r * glowStrength, c.g * glowStrength, c.b * glowStrength, 1f);
            }
        }

        public bool AffectsSomeone => affectsSelf || affectsAllies || affectsNeutrals || affectsEnemies;

        // The info card's aura lines: who, how far and when, then what it does to them. A trait description is
        // flavour text, so this is where a player reads the rules.
        public IEnumerable<string> CardLines(Thing source, float range)
        {
            bool building = source.def.building != null;
            var groups = new List<string>();
            if (affectsSelf && !building) groups.Add("EF_AuraWho_Self".Translate());
            if (affectsAllies) groups.Add("EF_AuraWho_Allies".Translate());
            if (affectsNeutrals) groups.Add("EF_AuraWho_Neutrals".Translate());
            if (affectsEnemies) groups.Add("EF_AuraWho_Enemies".Translate());
            var kinds = new List<string>();
            if (affectsHumanlikes) kinds.Add("EF_AuraKind_People".Translate());
            if (affectsAnimals) kinds.Add("EF_AuraKind_Animals".Translate());
            if (affectsMechanoids) kinds.Add("EF_AuraKind_Mechanoids".Translate());
            if (affectsEntities && ModsConfig.AnomalyActive) kinds.Add("EF_AuraKind_Entities".Translate());

            var when = new List<string>();
            if (blockedByWalls) when.Add("EF_AuraWhen_InSight".Translate());
            if (!building && onlyInCombat) when.Add("EF_AuraWhen_Combat".Translate());
            if (!building && !whileAsleep) when.Add("EF_AuraWhen_NotAsleep".Translate());
            if (!building && whileDowned) when.Add("EF_AuraWhen_Downed".Translate());
            if (building && source.TryGetComp<CompPowerTrader>() != null) when.Add("EF_AuraWhen_Powered".Translate());
            if (building && (source.TryGetComp<CompEntad>()?.IsBound ?? false)) when.Add("EF_AuraWhen_BoundNear".Translate());

            string who = groups.ToCommaList(true);
            if (kinds.Count > 0) who = "EF_Card_AuraWho".Translate(who, kinds.ToCommaList(true));
            string line = "EF_Card_Aura".Translate(who, Mathf.RoundToInt(range));
            if (when.Count > 0) line += ", " + string.Join(", ", when);
            yield return line;
            // A buildup hediff's stages change with severity, so its first stage isn't what a pawn ends up with
            if (severityPerDay > 0f)
                yield return "EF_Card_AuraBuildup".Translate(hediff.label);
            else
                yield return EntadUtility.HediffLine(hediff);
        }

        public IEnumerable<string> ConfigErrors(string defName)
        {
            if (hediff == null) yield return $"{defName}: aura has no hediff";
            if (radius <= 0f || radius > GenRadial.MaxRadialPatternRadius)
                yield return $"{defName}: aura radius {radius} must be above 0 and at most {GenRadial.MaxRadialPatternRadius}";
            if (intervalTicks < 1) yield return $"{defName}: aura intervalTicks must be at least 1";
            if (pulseFleck != null && pulseIntervalTicks < 1) yield return $"{defName}: aura pulseIntervalTicks must be at least 1";
            if (glowMote != null && !typeof(MoteAttached).IsAssignableFrom(glowMote.thingClass))
                yield return $"{defName}: aura glowMote {glowMote.defName} must have a MoteAttached thingClass";
            if (linkMote != null && !typeof(MoteDualAttached).IsAssignableFrom(linkMote.thingClass))
                yield return $"{defName}: aura linkMote {linkMote.defName} must have a MoteDualAttached thingClass";
            if (!AffectsSomeone) yield return $"{defName}: aura affects nobody: set affectsAllies, affectsEnemies, affectsNeutrals or affectsSelf";
            if (!affectsHumanlikes && !affectsAnimals && !affectsMechanoids && !affectsEntities)
                yield return $"{defName}: aura affects no kind of pawn: leave affectsHumanlikes or affectsAnimals on, or set affectsMechanoids or affectsEntities";
            if (hediff != null)
            {
                HediffCompProperties_Disappears d = null;
                int disappears = 0;
                if (hediff.comps != null)
                    foreach (var c in hediff.comps) if (c is HediffCompProperties_Disappears dc) { d = d ?? dc; disappears++; }
                // Give refreshes only the first Disappears comp (TryGetComp), so a second one, e.g. a child of
                // EntadAuraHediffBase that added its own instead of replacing the inherited list, runs out in range
                if (disappears > 1)
                    yield return $"{defName}: aura hediff {hediff.defName} has {disappears} HediffCompProperties_Disappears; only the first is refreshed. Use <comps Inherit=\"False\"> to replace an inherited one";
                // A buildup aura only adjusts severity and never resets the timer, so the hediff would vanish mid-buildup
                if (severityPerDay > 0f)
                {
                    if (d != null)
                        yield return $"{defName}: buildup aura hediff {hediff.defName} has a HediffCompProperties_Disappears, which removes it however long the pawn stays in range. Use EntadHediffBase, not EntadAuraHediffBase, and let severity fall off instead";
                }
                else if (d == null)
                    yield return $"{defName}: aura hediff {hediff.defName} has no HediffCompProperties_Disappears, so it never wears off after leaving the aura (or set severityPerDay for a buildup)";
                // +15: off-camera pawns tick in batches, so a refresh can be a little late
                else if (d.disappearsAfterTicks.min <= intervalTicks + 15)
                    yield return $"{defName}: aura hediff {hediff.defName} can wear off ({d.disappearsAfterTicks.min} ticks) before the aura refreshes it ({intervalTicks} ticks), so it will flicker";
            }
        }
    }

    [DefOf]
    public static class EntadAuraDefOf
    {
        // Combat Command's ground glow and link line, recolourable. Ideology's textures, so null without it.
        [MayRequireIdeology] public static ThingDef Entad_Mote_AuraGlow;
        [MayRequireIdeology] public static ThingDef Entad_Mote_AuraLink;

        static EntadAuraDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(EntadAuraDefOf)); }
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

        // Glow and link motes, one entry per aura trait that has been active recently with visuals on. Motes need
        // maintaining every tick or they fade, so these are looked at every tick; an entry drops out a little after
        // its aura goes idle, and no entry is ever made without Ideology or with aura effects off.
        private static readonly List<AuraVisual> visuals = new List<AuraVisual>();

        public EntadAuras(Game game) { sources.Clear(); visuals.Clear(); rings.Clear(); }

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
            if (sources.Count == 0 && visuals.Count == 0) return;
            int now = Find.TickManager.TicksGame;
            for (int i = visuals.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = visuals[i].Tick(now); }
                catch (System.Exception e)
                {
                    // One bad aura (a glowMote that isn't a mote) mustn't stop the others
                    Log.ErrorOnce($"[Entad Framework] aura visuals for {visuals[i].trait?.def?.defName} failed and were turned off: {e}", (visuals[i].trait?.def?.shortHash ?? 0) ^ 0x6B21);
                    keep = false;
                }
                if (!keep) { visuals[i].DestroyAll(); visuals.RemoveAt(i); }
            }
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
            AuraVisual vis = null;
            if (aura.GlowDef != null || aura.LinkDef != null)
            {
                vis = VisualFor(trait);
                vis.lastActive = Find.TickManager.TicksGame;
                vis.activeSource = source;
            }
            bool lines = vis != null && aura.LinkDef != null;
            bool any = false;
            // Backwards and from a copy: a buildup can kill, which changes the map's pawn list
            for (int i = tmpHit.Count - 1; i >= 0; i--)
            {
                var p = tmpHit[i];
                if (p.Dead || !Give(p, aura)) continue;
                any = true;
                if (lines && p != source) vis.linkHits[p] = vis.lastActive;
            }
            tmpHit.Clear();
            if (any && !trait.IsRevealed(EntadEffectKind.Aura) && !center.Fogged(map))
                trait.Reveal(EntadEffectKind.Aura, announce: source.Faction == Faction.OfPlayer);
        }

        // Range and sight are measured from the source's whole footprint, so a 3x3 bed reaches as far on every side
        // and its own cells don't block it. A pawn's footprint is its one cell.
        public static bool InRange(CellRect rect, IntVec3 c, float radius) => rect.ClosestCellTo(c).InHorDistOf(c, radius);

        private static bool Sees(CellRect rect, IntVec3 c, Map map) =>
            GenSight.LineOfSight(rect.ClosestCellTo(c), c, map, rect, CellRect.SingleCell(c));

        // The ring's cells, kept per trait between frames. Working them out costs a sight line per cell in range (about
        // 700 for a radius of 14), so they are redone only when the source moves, the radius or map changes, or every
        // RingRefreshFrames frames so a wall built or a door shut shows within a third of a second.
        private class RingCache
        {
            public readonly List<IntVec3> cells = new List<IntVec3>();
            public CellRect rect;
            public float radius;
            public Map map;
            public int builtFrame = -9999;
            public int drawnFrame;
        }

        private const int RingRefreshFrames = 20;
        private static readonly Dictionary<AppliedEntadTrait, RingCache> rings = new Dictionary<AppliedEntadTrait, RingCache>();
        private static readonly List<AppliedEntadTrait> tmpStaleRings = new List<AppliedEntadTrait>();
        private static int ringsDrawnThisFrame;

        // The cells in range (and in sight, when walls block it), outlined. Only while a source is selected.
        private static void DrawRing(AppliedEntadTrait trait, Thing source, float radius, EntadAura aura, Map map)
        {
            int frame = Time.frameCount;
            CellRect rect = source.OccupiedRect();
            if (!rings.TryGetValue(trait, out var ring)) rings[trait] = ring = new RingCache();
            if (ring.map != map || ring.radius != radius || ring.rect != rect || frame - ring.builtFrame >= RingRefreshFrames)
            {
                ring.cells.Clear();
                foreach (IntVec3 c in rect.ExpandedBy(Mathf.CeilToInt(radius)).ClipInsideMap(map))
                    if (InRange(rect, c, radius) && (!aura.blockedByWalls || rect.Contains(c) || Sees(rect, c, map))) ring.cells.Add(c);
                ring.map = map;
                ring.radius = radius;
                ring.rect = rect;
                ring.builtFrame = frame;
            }
            ring.drawnFrame = frame;
            ringsDrawnThisFrame++;
            GenDraw.DrawFieldEdges(ring.cells, aura.ringColor);
        }

        // Forgets rings not drawn this frame (source deselected, trait removed), so the cache holds only what is on
        // screen and keeps no removed trait or old map alive. Does nothing in the usual frame where every cached ring
        // was drawn.
        private static void PruneRings()
        {
            if (rings.Count == ringsDrawnThisFrame) return;
            int frame = Time.frameCount;
            foreach (var kv in rings) if (kv.Value.drawnFrame != frame) tmpStaleRings.Add(kv.Key);
            for (int i = 0; i < tmpStaleRings.Count; i++) rings.Remove(tmpStaleRings[i]);
            tmpStaleRings.Clear();
        }

        private static AuraVisual VisualFor(AppliedEntadTrait trait)
        {
            for (int i = 0; i < visuals.Count; i++) if (visuals[i].trait == trait) return visuals[i];
            var v = new AuraVisual { trait = trait };
            visuals.Add(v);
            return v;
        }

        // While the glow is up it shows the range instead of the ring
        private static bool GlowUp(AppliedEntadTrait trait)
        {
            for (int i = 0; i < visuals.Count; i++)
                if (visuals[i].trait == trait) return visuals[i].glow != null && !visuals[i].glow.Destroyed;
            return false;
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

        // Ported from Bastle Framework's BastleTrait_Aura.GlowTick and LinksTick. The motes keep living while they
        // are maintained every tick and fade out on their own within a tick or two when they aren't.
        private class AuraVisual
        {
            public AppliedEntadTrait trait;
            public int lastActive = -999999;
            // What the last scan worked from. Visuals show only while the aura can still work from the same thing:
            // a dropped weapon, gear put in a pack or a building that lost power stops them at once.
            public Thing activeSource;
            public Mote glow;
            private Thing glowOn;
            public readonly Dictionary<Pawn, int> linkHits = new Dictionary<Pawn, int>();
            private readonly Dictionary<Pawn, Mote> links = new Dictionary<Pawn, Mote>();
            private static readonly List<Pawn> tmpPawns = new List<Pawn>();

            // False: drop this entry (its trait is gone, or the aura has been idle for a while)
            public bool Tick(int now)
            {
                var comp = trait.owner;
                var aura = trait.def.aura;
                if (comp?.parent == null || comp.parent.Destroyed || aura == null || !comp.activeTraits.Contains(trait)) return false;
                // A scan can be up to 15 ticks late off camera, so the aura counts as active until the next one is due
                int keep = aura.intervalTicks + 15;
                Thing source = SourceOf(comp);
                if (source != glowOn) { DestroyAll(); glowOn = source; }
                bool show = now - lastActive <= keep && source != null && source == activeSource && source.Spawned && source.Map == Find.CurrentMap
                    && trait.IsRevealed(EntadEffectKind.Aura) && !source.Position.Fogged(source.Map);
                if (!show)
                {
                    DestroyAll();
                    return now - lastActive <= keep * 2;
                }
                var gd = aura.GlowDef;
                if (gd == null) DestroyGlow();
                else
                {
                    if (glow == null || glow.Destroyed)
                    {
                        // Entad_Mote_AuraGlow is 2 cells across at scale 1. Range is measured from the footprint, so a
                        // bigger building's glow is wider by the cells beyond its centre.
                        IntVec2 size = source.def.size;
                        float extra = source is Pawn ? 0f : (Mathf.Max(size.x, size.z) - 1) * 0.5f;
                        glow = MoteMaker.MakeAttachedOverlay(source, gd, Vector3.zero, Radius(trait) + extra + 0.1f);
                        glow.instanceColor = aura.GlowInstanceColor;
                    }
                    glow.Maintain();
                }
                var ld = aura.LinkDef;
                if (ld == null) DestroyLinks();
                else LinksTick(source, ld, aura, now, keep);
                return true;
            }

            private void LinksTick(Thing source, ThingDef ld, EntadAura aura, int now, int keep)
            {
                if (linkHits.Count == 0) return;
                Map map = source.Map;
                CellRect rect = source.OccupiedRect();
                float radius = Radius(trait);
                tmpPawns.Clear();
                tmpPawns.AddRange(linkHits.Keys);
                for (int i = 0; i < tmpPawns.Count; i++)
                {
                    var p = tmpPawns[i];
                    links.TryGetValue(p, out var m);
                    // A line lasts until the next scan could have renewed it, and drops as soon as the pawn leaves range
                    if (now - linkHits[p] > keep || p.Dead || !p.Spawned || p.Map != map
                        || !InRange(rect, p.Position, radius) || p.Position.Fogged(map))
                    {
                        if (m != null && !m.Destroyed) m.Destroy();
                        links.Remove(p);
                        linkHits.Remove(p);
                        continue;
                    }
                    if (m == null || m.Destroyed)
                    {
                        m = MoteMaker.MakeInteractionOverlay(ld, p, source);
                        m.instanceColor = aura.LinkInstanceColor;
                        links[p] = m;
                    }
                    m.Maintain();
                }
                tmpPawns.Clear();
            }

            private void DestroyGlow()
            {
                if (glow != null && !glow.Destroyed) glow.Destroy();
                glow = null;
            }

            private void DestroyLinks()
            {
                foreach (var m in links.Values) if (m != null && !m.Destroyed) m.Destroy();
                links.Clear();
                linkHits.Clear();
            }

            public void DestroyAll() { DestroyGlow(); DestroyLinks(); }
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

        // The range ring of each known aura on the selected pawns' gear and on selected furniture, unless its glow is up. Per frame, but
        // returns at once when no aura exists, and only draws for sources that are selected.
        public override void GameComponentUpdate()
        {
            ringsDrawnThisFrame = 0;
            DrawRings();
            PruneRings();
        }

        private static void DrawRings()
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
                    if (aura == null || !aura.showRing || !trait.IsRevealed(EntadEffectKind.Aura) || GlowUp(trait)) continue;
                    DrawRing(trait, source, Radius(trait), aura, map);
                }
            }
        }
    }
}
