using RimWorld;
using Verse;

namespace EntadFramework
{
    // Storage and bill filters "allow entads" / "allow non-entads". An item counts as an entad when it has at least one
    // trait; a packed-up piece of furniture is judged by the furniture inside it.
    //
    // CanEverMatch is limited to defs that carry CompEntad (or minified things, which can hold an entad building), so the
    // two checkboxes only appear in filter trees that contain gear or furniture, not under food or corpses. The comp is
    // added to defs at startup (EntadUtility), before any filter UI is drawn.
    public static class EntadThingFilterUtility
    {
        public static bool IsEntad(Thing t)
        {
            var comp = t.GetInnerIfMinified().TryGetComp<CompEntad>();
            return comp != null && !comp.activeTraits.NullOrEmpty();
        }

        public static bool CanBeEntad(ThingDef def) => def.HasComp(typeof(CompEntad)) || typeof(MinifiedThing).IsAssignableFrom(def.thingClass);
    }

    public class SpecialThingFilterWorker_Entad : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) => EntadThingFilterUtility.IsEntad(t);
        public override bool CanEverMatch(ThingDef def) => EntadThingFilterUtility.CanBeEntad(def);
    }

    public class SpecialThingFilterWorker_NonEntad : SpecialThingFilterWorker
    {
        public override bool Matches(Thing t) =>
            EntadThingFilterUtility.CanBeEntad(t.def) && !EntadThingFilterUtility.IsEntad(t);
        public override bool CanEverMatch(ThingDef def) => EntadThingFilterUtility.CanBeEntad(def);
    }
}
