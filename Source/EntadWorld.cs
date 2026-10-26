using System.Collections.Generic;
using Verse;

namespace EntadFramework
{
    /// <summary>The sources of entads that turn up in a game without a content mod making them.
    /// World is the master switch: every other feature also needs it on.</summary>
    public enum EntadWorldFeature
    {
        World,
        /// <summary>Raiders, visitors, traders' guards and settlement defenders carrying entads.</summary>
        Carriers,
        /// <summary>Entads in trader stock.</summary>
        Traders,
        /// <summary>Entads among quest rewards.</summary>
        Rewards,
        /// <summary>Entads in site, ancient complex and settlement loot.</summary>
        Loot,
        /// <summary>Caravan and settlement appraisers who identify entads.</summary>
        Appraisers,
    }

    /// <summary>
    /// Whether each world source of entads is on in the current game. The mod settings are the defaults; a mod can
    /// override any feature for one game (Dungeon Madness does it from its new-colony window), and the override is
    /// saved with that game. Same shape as Bastle Framework's BastleWorld.
    /// </summary>
    public static class EntadWorld
    {
        public static bool Enabled(EntadWorldFeature feature)
            => Raw(EntadWorldFeature.World) && (feature == EntadWorldFeature.World || Raw(feature));

        private static bool Raw(EntadWorldFeature feature)
        {
            var game = EntadWorldGame.Instance;
            if (game != null && game.overrides.Count > 0 && game.overrides.TryGetValue(feature, out bool v)) return v;
            return FromSettings(feature);
        }

        /// <summary>The settings' value, ignoring any override in the current game. Trader stock, rewards and loot
        /// share one setting; appraisers have no switch of their own in the settings (their chances decide), so they
        /// read as on.</summary>
        public static bool FromSettings(EntadWorldFeature feature)
        {
            switch (feature)
            {
                case EntadWorldFeature.World: return true;
                case EntadWorldFeature.Carriers: return EntadSettings.CarriersEnabled;
                case EntadWorldFeature.Traders:
                case EntadWorldFeature.Rewards:
                case EntadWorldFeature.Loot: return EntadSettings.WorldItemsEnabled;
                case EntadWorldFeature.Appraisers: return true;
                default: return false;
            }
        }

        /// <summary>The current game's override, or null when it follows the settings.</summary>
        public static bool? GetForGame(EntadWorldFeature feature)
        {
            var game = EntadWorldGame.Instance;
            return game != null && game.overrides.TryGetValue(feature, out bool v) ? v : (bool?)null;
        }

        /// <summary>Overrides a feature for the current game only (null goes back to the settings). Call it once the
        /// game exists: from a new-colony window, after Current.Game is set, or at any point in play. While
        /// Appraisers is off no new ones turn up and existing ones (settlement or caravan) are hidden; they are
        /// remembered and come back when it is turned on again.</summary>
        public static void SetForGame(EntadWorldFeature feature, bool? enabled)
        {
            var game = EntadWorldGame.Instance;
            if (game == null)
            {
                Log.Warning("[Entad Framework] EntadWorld.SetForGame was called with no game running; ignored.");
                return;
            }
            if (enabled.HasValue) game.overrides[feature] = enabled.Value;
            else game.overrides.Remove(feature);
        }
    }

    public class EntadWorldGame : GameComponent
    {
        internal Dictionary<EntadWorldFeature, bool> overrides = new Dictionary<EntadWorldFeature, bool>();

        public EntadWorldGame(Game game) { }

        public static EntadWorldGame Instance => Current.Game?.GetComponent<EntadWorldGame>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref overrides, "worldOverrides", LookMode.Value, LookMode.Value);
            if (overrides == null) overrides = new Dictionary<EntadWorldFeature, bool>();
        }
    }
}
