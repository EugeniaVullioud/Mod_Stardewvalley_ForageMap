using ForageTrackerModSV;
using StardewValley;
using StardewValley.Menus;

namespace ForageTrackerMod
{
    /// <summary>
    /// Resolves which map key applies for a given MapPage / game state.
    ///
    /// Map keys match the keys in regions.json (e.g. "Town", "Island").
    /// Previously this read the map texture name via reflection, which broke
    /// whenever SDV renamed its private fields.
    ///
    /// New approach: derive the key from the player's current location name,
    /// which is always available via Game1.player and never changes its API.
    ///
    ///   IslandSouth / IslandNorth / IslandEast / IslandWest / … → "Island"
    ///   Everything else                                           → "Town"
    ///
    /// Modded maps: if the location name contains a slash-separated prefix
    /// we use the last segment as the key, so "FantasyMod/Continent" → "Continent".
    /// This lets modded-map authors define their own key in regions.json.
    /// </summary>
    public static class MapKeyHelper
    {
        // All known Island location names — expand as needed.
        private static readonly HashSet<string> IslandLocations = new(StringComparer.OrdinalIgnoreCase)
        {
            "IslandSouth", "IslandNorth", "IslandEast", "IslandWest",
            "IslandFarmHouse", "IslandFieldOffice", "IslandShrine",
            "Caldera", "VolcanoDungeon0", "VolcanoDungeon5",
            "IslandLeftPlantRoom", "IslandRightPlantRoom",
            "IslandHut", "QiNutRoom",
        };

        /// <summary>
        /// Returns the map key that should be used for region lookup right now.
        /// Uses the player's current location, which always reflects which map
        /// tab the game is showing.
        /// </summary>
        public static string GetCurrentMapKey()
        {
            string locationName = Game1.player?.currentLocation?.Name ?? "";
            return LocationNameToMapKey(locationName);
        }

        /// <summary>
        /// Same resolution but driven by a location name string (for testing
        /// or when called from contexts without a live player).
        /// </summary>
        public static string LocationNameToMapKey(string locationName)
        {
            if (string.IsNullOrEmpty(locationName))
                return "Town";

            if (IslandLocations.Contains(locationName))
                return "Island";

            // Modded locations may be namespaced — use the last segment as the key.
            int slash = locationName.LastIndexOf('/');
            if (slash >= 0)
            {
                string segment = locationName[(slash + 1)..];
                return string.IsNullOrWhiteSpace(segment) ? "Town" : segment;
            }

            // All vanilla non-island locations appear on the Town world map.
            return "Town";
        }

        /// <summary>
        /// Legacy overload kept so MapTooltipDrawer and MapRegionEditor still compile
        /// without changes. Ignores the MapPage argument — key is derived from
        /// Game1.player.currentLocation instead.
        /// </summary>
        public static string GetMapKey(MapPage _mapPage) => GetCurrentMapKey();
    }
}
