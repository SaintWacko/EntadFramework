using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace EntadFramework
{
    // Persona weapons, done the way vanilla does them but without CompBladelinkWeapon (an entad is a plain weapon
    // def with no persona comp). A weapon with a persona trait bonds to the first pawn it's equipped by, raiders
    // included; that pawn's vanilla bondedWeapon slot points at it, so vanilla's "one bonded weapon per pawn" rule,
    // its bonded-weapon mood texts ({WEAPON}) and its "already bonded" menus all work. Nobody else can equip it, and
    // the bond ends when the pawn dies or the weapon is destroyed.
    //
    // This is separate from binding (EntadBinding.cs): binding is permanent and follows a bloodline, the bond is one
    // pawn for one life. A persona weapon bound to a bloodline only bonds with someone its traits work for.
    public partial class CompEntad
    {
        private Pawn bondedPawn;
        private string bondedPawnLabel;
        private int lastKillTick = -1;

        public Pawn BondedPawn => bondedPawn;

        // Has a persona trait and no freewielder
        public bool IsPersona
        {
            get
            {
                bool persona = false;
                for (int i = 0; i < activeTraits.Count; i++)
                {
                    if (activeTraits[i].def.neverBond) return false;
                    if (activeTraits[i].def.persona) persona = true;
                }
                return persona;
            }
        }

        public int TicksSinceLastKill => lastKillTick < 0 ? 0 : Find.TickManager.TicksAbs - lastKillTick;

        // Vanilla calls this on equip, before Pawn_EquipmentTracker's own postfixes (EntadAbilities) run
        public override void Notify_Equipped(Pawn pawn)
        {
            base.Notify_Equipped(pawn);
            if (parent is Apparel) lastWearer = pawn;
            if (bondedPawn == null && pawn?.equipment != null && pawn.equipment.bondedWeapon == null && IsPersona && ActiveFor(pawn))
                Bond(pawn);
        }

        public override void Notify_KilledPawn(Pawn pawn)
        {
            base.Notify_KilledPawn(pawn);
            if (bondedPawn != null) lastKillTick = Find.TickManager.TicksAbs;
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            Unbond();
            SpillSpaces(previousMap);
        }

        public void Bond(Pawn pawn)
        {
            if (pawn?.equipment == null || bondedPawn == pawn) return;
            Unbond();
            bondedPawn = pawn;
            bondedPawnLabel = pawn.Name?.ToStringFull ?? pawn.LabelShort;
            pawn.equipment.bondedWeapon = parent;
            lastKillTick = Find.TickManager.TicksAbs;
            if (pawn.IsColonistPlayerControlled)
                Find.LetterStack.ReceiveLetter("LetterBladelinkWeaponBondedLabel".Translate(pawn.Named("PAWN"), parent.Named("WEAPON")),
                    "LetterBladelinkWeaponBonded".Translate(pawn.Named("PAWN"), parent.Named("WEAPON")), LetterDefOf.PositiveEvent, new LookTargets(pawn));
            foreach (var m in activeTraits)
            {
                AddBondedHediffs(m.def);
                // A persona tells its wielder what it can do: every bond-related trait is known from now on
                if ((m.def.EffectKinds & EntadEffectKind.Bond) != 0) m.Reveal(EntadEffectKind.All);
            }
        }

        public void Unbond()
        {
            if (bondedPawn == null) return;
            Pawn pawn = bondedPawn;
            bondedPawn = null;
            bondedPawnLabel = null;
            lastKillTick = -1;
            if (pawn.equipment != null && pawn.equipment.bondedWeapon == parent) pawn.equipment.bondedWeapon = null;
            foreach (var m in activeTraits) RemoveBondedHediffs(pawn, m.def, null);
        }

        private void AddBondedHediffs(EntadTraitDef def)
        {
            if (bondedPawn?.health == null || def.bondedHediffs.NullOrEmpty() || bondedPawn.Dead) return;
            foreach (var h in def.bondedHediffs)
                if (bondedPawn.health.hediffSet.GetFirstHediffOfDef(h) == null)
                    bondedPawn.health.AddHediff(h, bondedPawn.health.hediffSet.GetBrain());
        }

        // Leaves a hediff another of this item's traits still gives
        private void RemoveBondedHediffs(Pawn pawn, EntadTraitDef def, AppliedEntadTrait removed)
        {
            if (pawn?.health == null || def.bondedHediffs.NullOrEmpty()) return;
            foreach (var h in def.bondedHediffs)
            {
                if (removed != null && activeTraits.Any(m => m != removed && m.def.bondedHediffs != null && m.def.bondedHediffs.Contains(h))) continue;
                var existing = pawn.health.hediffSet.GetFirstHediffOfDef(h);
                if (existing != null) pawn.health.RemoveHediff(existing);
            }
        }

        // A persona weapon's traits start revealed unless it's Secretive; the persona traits themselves always are.
        // Runs on every add, so the order traits are rolled in doesn't matter: adding Persona reveals the traits
        // already there, adding Secretive hides them again. Silent, as it's meant for generation; Secretive added to a
        // weapon in play (API, dev tools) hides what the player had learned too, since a reveal doesn't record why.
        private void SyncPersonaReveal(AppliedEntadTrait added)
        {
            if (!EntadSettings.HideTraits) return;
            bool persona = false, secretive = false;
            for (int i = 0; i < activeTraits.Count; i++)
            {
                persona |= activeTraits[i].def.persona;
                secretive |= activeTraits[i].def.secretive;
            }
            bool changed = false;
            if (!persona)
            {
                // Persona-related traits (added by hand to a weapon without one) still show their names
                foreach (var m in activeTraits)
                    if ((m.def.neverBond || m.def.secretive) && m.revealedKinds != EntadEffectKind.All) { m.revealedKinds = EntadEffectKind.All; changed = true; }
                if (changed && added != null) RevealedChanged(added, EntadEffectKind.All, announce: false);
                return;
            }
            foreach (var m in activeTraits)
            {
                // Bond traits stay known while bonded: the bond is what told the wielder about them
                bool always = m.def.persona || m.def.neverBond || m.def.secretive
                    || (bondedPawn != null && (m.def.EffectKinds & EntadEffectKind.Bond) != 0);
                EntadEffectKind want;
                if (always || !secretive) want = EntadEffectKind.All;
                else if (added != null && added.def.secretive && m != added) want = EntadEffectKind.None;
                else continue;
                if (m.revealedKinds == want) continue;
                m.revealedKinds = want;
                changed = true;
            }
            if (changed && added != null) RevealedChanged(added, EntadEffectKind.All, announce: false);
        }

        private void PersonaTraitAdded(AppliedEntadTrait m)
        {
            SyncPersonaReveal(m);
            if (bondedPawn == null)
            {
                // Persona added to a weapon already in someone's hands: bond now, as if it had just been equipped
                if (m.def.persona && parent.ParentHolder is Pawn_EquipmentTracker eqt) Notify_Equipped(eqt.pawn);
                return;
            }
            if (!IsPersona) { Unbond(); return; }   // freewielder added
            AddBondedHediffs(m.def);
            if ((m.def.EffectKinds & EntadEffectKind.Bond) != 0) m.Reveal(EntadEffectKind.All);
        }

        private void PersonaTraitRemoved(AppliedEntadTrait m)
        {
            // Secretive gone: the persona tells all
            if (m.def.secretive) SyncPersonaReveal(m);
            if (bondedPawn == null) return;
            if (!IsPersona) { Unbond(); return; }   // the persona itself removed
            RemoveBondedHediffs(bondedPawn, m.def, m);
        }

        private void ExposePersona()
        {
            if (Scribe.mode == LoadSaveMode.Saving && bondedPawn != null && bondedPawn.Discarded) bondedPawn = null;
            Scribe_References.Look(ref bondedPawn, "bondedPawn", saveDestroyedThings: true);
            Scribe_Values.Look(ref bondedPawnLabel, "bondedPawnLabel");
            Scribe_Values.Look(ref lastKillTick, "lastKillTick", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && bondedPawn != null)
            {
                // The pawn's side is saved by vanilla; if the two disagree, the pawn's side wins (as vanilla does)
                var eq = bondedPawn.equipment;
                if (bondedPawn.Dead || eq == null) { bondedPawn = null; bondedPawnLabel = null; }
                else if (eq.bondedWeapon == null) eq.bondedWeapon = parent;
                else if (eq.bondedWeapon != parent) { bondedPawn = null; bondedPawnLabel = null; }
            }
        }

        public string PersonaInspectLine
        {
            get
            {
                if (!IsPersona) return null;
                return bondedPawn != null ? "BondedWith".Translate(bondedPawnLabel.ApplyTag(TagType.Name)).Resolve() : "NotBonded".Translate().Resolve();
            }
        }

        public IEnumerable<string> PersonaCardLines(AppliedEntadTrait m)
        {
            var d = m.def;
            if (d.persona) yield return "EF_Card_Persona".Translate();
            if (d.neverBond) yield return "EF_Card_NeverBond".Translate();
            if (d.secretive) yield return "EF_Card_Secretive".Translate();
            if (!d.bondedHediffs.NullOrEmpty())
                yield return "EF_Card_BondedHediffs".Translate(string.Join(", ", d.bondedHediffs.Select(h => h.LabelCap.ToString())));
            if (d.bondedThought != null)
                yield return "EF_Card_BondedThought".Translate(EntadPersona.ThoughtLabel(d.bondedThought, parent), EntadTraitDef.MoodEffectOf(d.bondedThought).ToString("+0.#;-0.#"));
            if (d.otherWeaponThought != null)
                yield return "EF_Card_OtherWeaponThought".Translate(EntadPersona.ThoughtLabel(d.otherWeaponThought, parent), EntadTraitDef.MoodEffectOf(d.otherWeaponThought).ToString("+0.#;-0.#"));
            if (d.killThirstThought != null)
                yield return "EF_Card_KillThirst".Translate(d.killThirstDays.ToString("0.#"), EntadPersona.ThoughtLabel(d.killThirstThought, parent),
                    EntadTraitDef.MoodEffectOf(d.killThirstThought).ToString("+0.#;-0.#"));
        }
    }

    public static class EntadPersona
    {
        private static bool? anyPersona;

        // Every persona patch returns at once in a game with no persona traits loaded
        public static bool AnyPersona => anyPersona ?? (anyPersona = DefDatabase<EntadTraitDef>.AllDefsListForReading.Any(d => d.persona)).Value;

        public static CompEntad PersonaComp(Thing thing)
        {
            if (!AnyPersona || thing == null) return null;
            var comp = thing.TryGetComp<CompEntad>();
            return comp != null && comp.IsPersona ? comp : null;
        }

        // The pawn's bonded weapon, when it's one of ours
        public static CompEntad BondedComp(Pawn p)
        {
            if (!AnyPersona) return null;
            var w = p?.equipment?.bondedWeapon;
            if (w == null || w.Destroyed) return null;
            var comp = w.TryGetComp<CompEntad>();
            return comp != null && comp.BondedPawn == p ? comp : null;
        }

        // A weapon-trait thought's first stage label with {WEAPON} filled in
        public static string ThoughtLabel(ThoughtDef t, Thing weapon)
        {
            string label = t.stages?.FirstOrDefault(s => s != null)?.label;
            if (label.NullOrEmpty()) return t.defName;
            return label.Formatted(weapon.LabelNoCount.Named("WEAPON")).CapitalizeFirst().Resolve();
        }
    }

    // Nobody but the bonded pawn may equip it, and a pawn with a bonded weapon can't bond another
    [HarmonyPatch(typeof(EquipmentUtility), nameof(EquipmentUtility.CanEquip), new[] { typeof(Thing), typeof(Pawn), typeof(string), typeof(bool) },
        new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Normal })]
    public static class Patch_EquipmentUtility_CanEquip_Persona
    {
        public static void Postfix(Thing thing, Pawn pawn, ref string cantReason, ref bool __result)
        {
            if (!__result) return;
            var comp = EntadPersona.PersonaComp(thing);
            if (comp == null) return;
            // ("Already bonded to another weapon" needs nothing here: vanilla's own check calls AlreadyBondedToWeapon,
            // which is patched below)
            if (comp.BondedPawn != null && comp.BondedPawn != pawn)
            {
                cantReason = "BladelinkBondedToSomeoneElse".Translate();
                __result = false;
            }
        }
    }

    // Drives the "(existing bond)" float menu entry and its dialog, and the outfit stand's check
    [HarmonyPatch(typeof(EquipmentUtility), nameof(EquipmentUtility.AlreadyBondedToWeapon))]
    public static class Patch_EquipmentUtility_AlreadyBonded_Persona
    {
        public static void Postfix(Thing thing, Pawn pawn, ref bool __result)
        {
            if (__result || pawn?.equipment?.bondedWeapon == null || pawn.equipment.bondedWeapon == thing) return;
            var comp = EntadPersona.PersonaComp(thing);
            // Only when it would bond with them: a bloodline-bound persona whose traits don't work for this pawn
            // never bonds, so it's an ordinary weapon to them
            if (comp != null && comp.BondedPawn == null && comp.ActiveFor(pawn)) __result = true;
        }
    }

    [HarmonyPatch(typeof(EquipmentUtility), nameof(EquipmentUtility.GetPersonaWeaponConfirmationText))]
    public static class Patch_EquipmentUtility_PersonaConfirmation
    {
        public static void Postfix(Thing item, Pawn p, ref string __result)
        {
            if (__result != null) return;
            var comp = EntadPersona.PersonaComp(item);
            if (comp == null || comp.BondedPawn != null || !comp.ActiveFor(p)) return;
            __result = "EF_Persona_EquipWarning".Translate(item.LabelNoCount) + "\n\n" + "EF_Persona_EquipConfirm".Translate();
        }
    }

    [HarmonyPatch(typeof(EquipmentUtility), nameof(EquipmentUtility.IsBondedTo))]
    public static class Patch_EquipmentUtility_IsBondedTo_Persona
    {
        public static void Postfix(Thing thing, Pawn pawn, ref bool __result)
        {
            if (__result || !EntadPersona.AnyPersona || pawn == null) return;
            if (thing?.TryGetComp<CompEntad>()?.BondedPawn == pawn) __result = true;
        }
    }

    // Jealousy: the bonded pawn wields another primary weapon
    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_EquipmentAdded))]
    public static class Patch_EquipmentAdded_PersonaJealousy
    {
        public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps eq)
        {
            Pawn pawn = __instance.pawn;
            if (eq?.def.equipmentType != EquipmentType.Primary || pawn?.needs?.mood == null) return;
            var comp = EntadPersona.BondedComp(pawn);
            if (comp == null || comp.parent == eq) return;
            foreach (var m in comp.activeTraits)
            {
                if (m.def.otherWeaponThought == null) continue;
                var t = (Thought_WeaponTrait)ThoughtMaker.MakeThought(m.def.otherWeaponThought);
                t.weapon = comp.parent;
                pawn.needs.mood.thoughts.memories.TryGainMemory(t);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_EquipmentTracker), nameof(Pawn_EquipmentTracker.Notify_PawnDied))]
    public static class Patch_EquipmentTracker_PawnDied_Persona
    {
        public static void Postfix(Pawn_EquipmentTracker __instance)
        {
            EntadPersona.BondedComp(__instance.pawn)?.Unbond();
        }
    }

    // Vanilla's bonded-thought worker only looks at CompBladelinkWeapon traits
    [HarmonyPatch(typeof(ThoughtWorker_WeaponTraitBonded), "CurrentStateInternal")]
    public static class Patch_ThoughtWorker_WeaponTraitBonded_Entad
    {
        public static void Postfix(ThoughtWorker_WeaponTraitBonded __instance, Pawn p, ref ThoughtState __result)
        {
            if (__result.Active) return;
            var comp = EntadPersona.BondedComp(p);
            if (comp == null) return;
            for (int i = 0; i < comp.activeTraits.Count; i++)
                if (comp.activeTraits[i].def.bondedThought == __instance.def) { __result = true; return; }
        }
    }

    [HarmonyPatch(typeof(ThoughtWorker_WeaponTraitKillNeed), "CurrentStateInternal")]
    public static class Patch_ThoughtWorker_WeaponTraitKillNeed_Entad
    {
        public static void Postfix(ThoughtWorker_WeaponTraitKillNeed __instance, Pawn p, ref ThoughtState __result)
        {
            if (__result.Active) return;
            var comp = EntadPersona.BondedComp(p);
            if (comp == null) return;
            for (int i = 0; i < comp.activeTraits.Count; i++)
            {
                var d = comp.activeTraits[i].def;
                if (d.killThirstThought == __instance.def && comp.TicksSinceLastKill > d.killThirstDays * GenDate.TicksPerDay) { __result = true; return; }
            }
        }
    }
}
