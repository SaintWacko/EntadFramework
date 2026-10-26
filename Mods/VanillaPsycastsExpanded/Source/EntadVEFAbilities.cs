using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using VEF.Abilities;
using Verse;
using Ability = VEF.Abilities.Ability;
using AbilityDef = VEF.Abilities.AbilityDef;
using Command_Ability = VEF.Abilities.Command_Ability;

namespace EntadFramework.VPECompat
{
    // Vanilla Expanded Framework abilities (Vanilla Psycasts Expanded's psycasts) granted by entad gear. Loaded only
    // while VPE is active (LoadFolders.xml), so the main assembly never names a VEF type.
    //
    // Each trait listing a VEF ability in externalAbilities gets its own copy of the def without VPE's psycast
    // extension (psyfocus, neural heat, path locks, psysets) or a required hediff, so anyone can cast it, limited by
    // the trait's charges and cooldown instead. The Ability objects live on the item, as VEF's own
    // CompAbilitiesApparel keeps them, and never in the pawn's CompAbilities.learnedAbilities: VEF has no way to
    // remove one from there, and a copy saved on the pawn would outlive the item, or Entad. The item saves them
    // (EntadCompat.Exposing), so what refers to an ability by load ID (a ball lightning still floating, a power leap
    // in flight, the pawn's CompAbilities.currentlyCasting) still finds it after a load. Charges and the cooldown are
    // kept on the trait, as for vanilla abilities.
    //
    // Not in learnedAbilities also means VEF's own NPC autocast (its Pawn.TryGetAttackVerb patch) never sees them:
    // NPCs carry the traits but don't cast. The copies have chance 0, so the player can't switch on an autocast
    // that would never run.
    [StaticConstructorOnStartup]
    public static class EntadVEFAbilities
    {
        public const string WeightKey = "VPE.TraitWeight";
        public const string KeepVanillaKey = "VPE.KeepVanillaPsycasts";
        private const string PsycastExtension = "VanillaPsycastsExpanded.AbilityExtension_Psycast";

        // Royalty's psycasts as entad traits (Defs/EntadTraits_Psycasts.xml): VPE has its own versions of them
        private static readonly string[] VanillaPsycastTraits = { "Entad_ChaosSkip", "Entad_Farskip", "Entad_StunCharge" };

        internal class Slot
        {
            public AppliedEntadTrait trait;
            public int index;
            public int maxCharges;
            public int cooldown;
            public Pawn tickingFor;

            public int Charges => trait.ChargesLeft(index, maxCharges, cooldown);
        }

        private struct Owner
        {
            public EntadTraitDef trait;
            public int index;
        }

        private static readonly Dictionary<EntadTraitDef, AbilityDef[]> copies = new Dictionary<EntadTraitDef, AbilityDef[]>();
        private static readonly Dictionary<AbilityDef, Owner> ownerOf = new Dictionary<AbilityDef, Owner>();
        private static readonly HashSet<EntadTraitDef> vanillaPsycastTraits = new HashSet<EntadTraitDef>();

        private static readonly ConditionalWeakTable<CompEntad, List<Ability>> held = new ConditionalWeakTable<CompEntad, List<Ability>>();
        private static readonly ConditionalWeakTable<Ability, Slot> slots = new ConditionalWeakTable<Ability, Slot>();

        // Abilities whose def asks for ticks (maintained effecters of Skip and the like), by the pawn whose
        // CompAbilities ticks them
        internal static readonly Dictionary<Pawn, List<Ability>> ticking = new Dictionary<Pawn, List<Ability>>();

        // Ability caches its holder's CompAbilities on first use: after the item changes hands the copy would read the
        // old holder's casting targets
        private static AccessTools.FieldRef<Ability, CompAbilities> CachedComp;

        static EntadVEFAbilities()
        {
            // The field lookup, then the patches, before anything registers: either one lost in a VEF update throws
            // here and the traits never roll, rather than rolling and doing nothing. The lookup goes first so a missing
            // field leaves no tick patch live on a class that failed to initialise.
            CachedComp = AccessTools.FieldRefAccess<Ability, CompAbilities>("comp");
            var harmony = new Harmony("SaintWacko.EntadFramework.VanillaPsycastsExpanded");
            try { harmony.PatchAll(typeof(EntadVEFAbilities).Assembly); }
            catch
            {
                // PatchAll keeps the patches it applied before the one that failed, and those would call into this
                // class, which never finished initialising, on every tick and every cast
                harmony.UnpatchAll(harmony.Id);
                throw;
            }

            MethodInfo clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var trait in DefDatabase<EntadTraitDef>.AllDefsListForReading)
            {
                if (trait.externalAbilities.NullOrEmpty()) continue;
                AbilityDef[] made = null;
                for (int j = 0; j < trait.externalAbilities.Count; j++)
                {
                    string name = trait.externalAbilities[j];
                    var source = DefDatabase<AbilityDef>.GetNamedSilentFail(name);
                    if (source == null)
                    {
                        Log.Warning($"[Entad Framework] {trait.defName}: {name} is not a Vanilla Expanded Framework ability, so the trait never rolls.");
                        continue;
                    }
                    if (made == null) made = new AbilityDef[trait.externalAbilities.Count];
                    made[j] = Copy(source, trait, j, clone);
                    ownerOf[made[j]] = new Owner { trait = trait, index = j };
                    EntadCompat.RegisterExternalAbility(name, () => source.label);
                }
                if (made != null) copies[trait] = made;
            }
            foreach (string name in VanillaPsycastTraits)
            {
                var def = DefDatabase<EntadTraitDef>.GetNamedSilentFail(name);
                if (def != null) vanillaPsycastTraits.Add(def);
            }

            EntadCompat.Exposing += Expose;
            EntadCompat.Refreshed += comp => { if (held.TryGetValue(comp, out _)) Sync(comp); };
            EntadCompat.GameStarted += ticking.Clear;
            EntadCompat.RegisterGizmos(Gizmos);
            EntadCompat.RegisterWeight(Weight);
            EntadCompat.RegisterSettings(EntadSettingsTab.Generation, DrawSettings);
        }

        // Registered in the database under the trait's name, so the abilities the items save find their def again
        private static AbilityDef Copy(AbilityDef source, EntadTraitDef trait, int j, MethodInfo clone)
        {
            var copy = (AbilityDef)clone.Invoke(source, null);
            copy.defName = "Entad_" + trait.defName + (j == 0 ? "" : "_" + j);
            copy.ResolveDefNameHash();
            copy.modExtensions = source.modExtensions?.Where(e => e.GetType().FullName != PsycastExtension).ToList() ?? new List<DefModExtension>();
            copy.requiredHediff = null;
            copy.requiredTrait = null;
            copy.gizmoClass = typeof(Command_EntadAbility);
            copy.chance = 0f;
            copy.autocastPlayerDefault = false;
            if (trait.abilityCooldownTicks > 0)
            {
                copy.cooldownTime = trait.abilityCooldownTicks;
                copy.cooldownTimeStatFactors = new List<StatModifier>();
                copy.cooldownTimeStatOffsets = new List<StatModifier>();
            }
            if (trait.abilityCastTicks > 0)
            {
                copy.castTime = trait.abilityCastTicks;
                copy.castTimeStatFactors = new List<StatModifier>();
                copy.castTimeStatOffsets = new List<StatModifier>();
            }
            DefDatabase<AbilityDef>.Add(copy);
            return copy;
        }

        internal static bool TryGetSlot(Ability ability, out Slot slot) => slots.TryGetValue(ability, out slot);

        internal static bool IsOurs(Ability ability) => ability?.def != null && ownerOf.ContainsKey(ability.def);

        private static bool HasCopies(CompEntad comp)
        {
            var traits = comp.activeTraits;
            if (traits == null) return false;
            for (int i = 0; i < traits.Count; i++)
                if (copies.ContainsKey(traits[i].def)) return true;
            return false;
        }

        // The item's abilities, one per copy its traits grant: made for a trait that is new, dropped for one that is
        // gone, and each tied to the trait that keeps its charges (a load makes new trait objects)
        private static List<Ability> Sync(CompEntad comp)
        {
            var list = held.GetValue(comp, _ => new List<Ability>());
            var traits = comp.activeTraits;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (ownerOf.TryGetValue(list[i].def, out var owner) && traits.Any(t => t.def == owner.trait)) continue;
                if (slots.TryGetValue(list[i], out var gone)) Untick(list[i], gone);
                list.RemoveAt(i);
            }
            for (int t = 0; t < traits.Count; t++)
            {
                if (!copies.TryGetValue(traits[t].def, out var made)) continue;
                for (int j = 0; j < made.Length; j++)
                {
                    var def = made[j];
                    if (def == null) continue;
                    Ability ability = null;
                    for (int k = 0; k < list.Count; k++)
                        if (list[k].def == def) { ability = list[k]; break; }
                    if (ability == null)
                    {
                        ability = (Ability)Activator.CreateInstance(def.abilityClass);
                        ability.def = def;
                        ability.holder = comp.parent;
                        ability.pawn = comp.Holder;
                        ability.Init();
                        list.Add(ability);
                    }
                    var slot = slots.GetValue(ability, _ => new Slot());
                    slot.trait = traits[t];
                    slot.index = traits[t].def.ExternalAbilitySlot(j);
                    slot.maxCharges = Math.Max(1, traits[t].def.abilityCharges);
                    slot.cooldown = def.cooldownTime;
                }
            }
            return list;
        }

        // The holder is the caster, and the pawn whose CompAbilities ticks it. A previous holder keeps ticking it until
        // someone else holds it, which only lets a maintained effect it started play out.
        private static void Hold(Ability ability, Pawn pawn)
        {
            if (ability.pawn != pawn)
            {
                ability.pawn = pawn;
                CachedComp(ability) = null;
                ability.Init();
            }
            if (!slots.TryGetValue(ability, out var slot) || slot.tickingFor == pawn) return;
            if (!ability.def.needsTicking && !ability.def.needsTickingInterval) return;
            Untick(ability, slot);
            if (!ticking.TryGetValue(pawn, out var list)) ticking[pawn] = list = new List<Ability>();
            list.Add(ability);
            slot.tickingFor = pawn;
        }

        private static void Untick(Ability ability, Slot slot)
        {
            if (slot.tickingFor == null) return;
            if (ticking.TryGetValue(slot.tickingFor, out var list))
            {
                list.Remove(ability);
                if (list.Count == 0) ticking.Remove(slot.tickingFor);
            }
            slot.tickingFor = null;
        }

        // VEF disables the gizmo while cooldown is ahead: 0 while a charge is left, else when the next one is back
        private static void RefreshCooldown(Ability ability, Slot slot)
        {
            ability.cooldown = slot.Charges > 0 ? 0 : slot.trait.AbilityReadyTick(slot.index);
        }

        internal static void Cast(Ability ability)
        {
            if (!slots.TryGetValue(ability, out var slot)) return;
            slot.trait.UseCharge(slot.index, slot.maxCharges, slot.cooldown);
            RefreshCooldown(ability, slot);
            slot.trait.Reveal(EntadEffectKind.Ability);
        }

        private static IEnumerable<Gizmo> Gizmos(CompEntad comp)
        {
            if (!HasCopies(comp)) return null;
            Pawn pawn = comp.Holder;
            // Only the player casts these (VEF's ShowGizmoOnPawn says the same): selecting a raider mustn't make the
            // raider the ability's caster. Casting runs through the pawn's CompAbilities (every humanlike race has one).
            if (pawn == null || !pawn.IsColonistPlayerControlled || !comp.ActiveFor(pawn) || pawn.GetComp<CompAbilities>() == null) return null;
            List<Gizmo> gizmos = null;
            foreach (var ability in Sync(comp))
            {
                Hold(ability, pawn);
                if (!ability.ShowGizmoOnPawn() || !slots.TryGetValue(ability, out var slot)) continue;
                RefreshCooldown(ability, slot);
                if (gizmos == null) gizmos = new List<Gizmo>();
                gizmos.Add(ability.GetGizmo());
            }
            return gizmos;
        }

        // World-map abilities (Farskip) cast from a caravan: VEF's own caravan gizmos only cover learned abilities
        internal static IEnumerable<Gizmo> CaravanGizmos(Caravan caravan)
        {
            if (copies.Count == 0) yield break;
            var pawns = caravan.PawnsListForReading;
            for (int p = 0; p < pawns.Count; p++)
            {
                Pawn pawn = pawns[p];
                // Prisoners and guests travel in caravans too, wearing their own gear
                if (!pawn.IsColonist || pawn.IsPrisoner || pawn.GetComp<CompAbilities>() == null) continue;
                var items = new List<Thing>();
                if (pawn.apparel != null) items.AddRange(pawn.apparel.WornApparel);
                if (pawn.equipment != null) items.AddRange(pawn.equipment.AllEquipmentListForReading);
                foreach (var item in items)
                {
                    var comp = item.TryGetComp<CompEntad>();
                    if (comp == null || !HasCopies(comp) || !comp.ActiveFor(pawn)) continue;
                    foreach (var ability in Sync(comp))
                    {
                        if (!ability.def.showGizmoOnWorldView) continue;
                        Hold(ability, pawn);
                        if (!ability.ShowGizmoOnPawn() || !slots.TryGetValue(ability, out var slot)) continue;
                        RefreshCooldown(ability, slot);
                        yield return ability.GetGizmo();
                    }
                }
            }
        }

        private static void Expose(CompEntad comp)
        {
            List<Ability> list = null;
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                if (!held.TryGetValue(comp, out list) || list.Count == 0) return;
                // Ability saves its caster by reference, and a discarded pawn is never written to the save
                foreach (var ability in list)
                    if (ability.pawn != null && ability.pawn.Discarded) ability.pawn = null;
            }
            Scribe_Collections.Look(ref list, "entadVEFAbilities", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.LoadingVars && list != null)
            {
                list.RemoveAll(a => a?.def == null);
                // Ability doesn't save its holder, and its load ID is built from it when references resolve
                foreach (var ability in list) ability.holder = comp.parent;
                held.Remove(comp);
                held.Add(comp, list);
            }
            // Ticking isn't restored here: base Ability only ticks maintained effecters, which VEF doesn't save. The
            // holder's gizmos register it again. Classes that tick saved state of their own (VPE's Killskip, Season
            // Timeskip) would pause after a load until then: none of the cast traits uses one, but a triggered Killskip
            // has to register its ticking on load.
            else if (Scribe.mode == LoadSaveMode.PostLoadInit && held.TryGetValue(comp, out _)) Sync(comp);
        }

        private static float Weight(EntadTraitDef def)
        {
            if (copies.ContainsKey(def)) return EntadSettings.ModuleValue(WeightKey, 1f);
            if (vanillaPsycastTraits.Contains(def)) return EntadSettings.ModuleValue(KeepVanillaKey, 0f) > 0.5f ? 1f : 0f;
            return 1f;
        }

        private static void DrawSettings(Listing_Standard list)
        {
            EntadMod.Section(list, "EF_VPE_Settings");
            float weight = EntadSettings.ModuleValue(WeightKey, 1f);
            list.Label("EF_VPE_Weight".Translate(weight.ToString("0.##")));
            EntadSettings.SetModuleValue(WeightKey, Mathf.Round(list.Slider(weight, 0f, 3f) * 20f) / 20f);
            list.Label("EF_VPE_Weight_Desc".Translate());
            list.Gap();
            bool keep = EntadSettings.ModuleValue(KeepVanillaKey, 0f) > 0.5f;
            list.CheckboxLabeled("EF_VPE_KeepVanilla".Translate(), ref keep, "EF_VPE_KeepVanilla_Tip".Translate());
            EntadSettings.SetModuleValue(KeepVanillaKey, keep ? 1f : 0f);
        }
    }

    // The gizmo VEF builds for the copies (their gizmoClass): the charges left, and "???" until the trait is known
    public class Command_EntadAbility : Command_Ability
    {
        private readonly string chargesLabel;

        public Command_EntadAbility(Pawn pawn, Ability ability) : base(pawn, ability)
        {
            if (!EntadVEFAbilities.TryGetSlot(ability, out var slot)) return;
            if (slot.maxCharges > 1) chargesLabel = slot.Charges + "/" + slot.maxCharges;
            if (slot.trait.IsRevealed(EntadEffectKind.Ability)) return;
            defaultLabel = "EF_Unknown".Translate();
            defaultDesc = "EF_AbilityUnknown".Translate();
            icon = TexButton.Info;
            // Shrunk, Command_Ability writes the ability's name in front of the description
            shrinkable = false;
            int now = Find.TickManager.TicksGame;
            if (disabled && ability.cooldown > now)
                disabledReason = "EF_VPE_HiddenCooldown".Translate((ability.cooldown - now).ToStringTicksToPeriod()).Resolve().Colorize(ColorLibrary.RedReadable);
        }

        public override string TopRightLabel => chargesLabel ?? base.TopRightLabel;
    }

    // Base Ability.Cast runs once per cast, at the end of every override (each VPE class calls it): spend the charge
    [HarmonyPatch(typeof(Ability), nameof(Ability.Cast), new[] { typeof(GlobalTargetInfo[]) })]
    public static class Patch_VEFAbility_Cast
    {
        public static void Postfix(Ability __instance) => EntadVEFAbilities.Cast(__instance);
    }

    // Maintained effecters (a skip's flash) for item abilities that need ticks, on the holder's own tick. One count
    // check per humanlike per tick while no entad ability is held.
    [HarmonyPatch(typeof(CompAbilities), nameof(CompAbilities.CompTick))]
    public static class Patch_CompAbilities_CompTick
    {
        public static void Postfix(CompAbilities __instance)
        {
            if (EntadVEFAbilities.ticking.Count == 0 || !(__instance.parent is Pawn pawn)) return;
            if (!EntadVEFAbilities.ticking.TryGetValue(pawn, out var list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i].def.needsTicking) list[i].Tick();
        }
    }

    [HarmonyPatch(typeof(CompAbilities), nameof(CompAbilities.CompTickInterval))]
    public static class Patch_CompAbilities_CompTickInterval
    {
        public static void Postfix(CompAbilities __instance, int delta)
        {
            if (EntadVEFAbilities.ticking.Count == 0 || !(__instance.parent is Pawn pawn)) return;
            if (!EntadVEFAbilities.ticking.TryGetValue(pawn, out var list)) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i].def.needsTickingInterval) list[i].TickInterval(delta);
        }
    }

    // VEF never clears currentlyCasting after a cast. An item's ability is saved with the item, so once the item is
    // gone (destroyed, sold, left on another map) the pawn's reference would fail to resolve on load: keep it only
    // while the pawn is actually casting.
    [HarmonyPatch(typeof(CompAbilities), nameof(CompAbilities.PostExposeData))]
    public static class Patch_CompAbilities_PostExposeData
    {
        public static void Prefix(CompAbilities __instance)
        {
            if (Scribe.mode != LoadSaveMode.Saving || !EntadVEFAbilities.IsOurs(__instance.currentlyCasting)) return;
            if (!(__instance.parent is Pawn pawn) || !(pawn.jobs?.curDriver is JobDriver_CastAbilityOnce))
                __instance.currentlyCasting = null;
        }
    }

    [HarmonyPatch(typeof(Caravan), nameof(Caravan.GetGizmos))]
    public static class Patch_Caravan_GetGizmos
    {
        public static void Postfix(Caravan __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = With(__result, __instance);
        }

        private static IEnumerable<Gizmo> With(IEnumerable<Gizmo> original, Caravan caravan)
        {
            foreach (var g in original) yield return g;
            foreach (var g in EntadVEFAbilities.CaravanGizmos(caravan)) yield return g;
        }
    }
}
