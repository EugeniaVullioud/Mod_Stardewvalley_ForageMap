using ForageTrackerMod;
using StardewModdingAPI;

namespace ForageTrackerModSV
{
    public sealed class CustomHoverMappingConfig
    {
        public Dictionary<string, HoverMappingSet> Mappings { get; set; } = new();
    }

    public static class CustomHoverMappingStore
    {
        private const string FileName = "customHoverMappings.json";

        private static IModHelper? _helper;
        private static IMonitor? _monitor;

        private static CustomHoverMappingConfig _config = new();

        public static void Init(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;

            Load();
        }

        public static void Load()
        {
            if (_helper == null) return;

            try
            {
                var loaded = _helper.Data.ReadJsonFile<CustomHoverMappingConfig>(FileName);

                if (loaded == null)
                {
                    _config = new CustomHoverMappingConfig();
                    _helper.Data.WriteJsonFile(FileName, _config);

                    return;
                }

                _config = loaded;


                // Report duplicate entries and duplicate locations.
                foreach (var map in _config.Mappings)
                {
                    var seenHoverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in map.Value.Entries)
                    {
                        var seenLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
            catch (Exception ex)
            {
                _config = new CustomHoverMappingConfig();
            }
        }
        public static List<HoverMappingEntry> GetValidEntries(string mapKey)
        {
            var result = new List<HoverMappingEntry>();

            if (!_config.Mappings.TryGetValue(mapKey, out var set)) return result;

            foreach (var entry in set.Entries)
            {
                var validLocations = new List<string>();

                foreach (var location in entry.Locations)
                {
                    bool isReal = MapPointResolver.IsRealLocation(location);

                    if (isReal && !validLocations.Contains(location, StringComparer.OrdinalIgnoreCase))
                    {
                        validLocations.Add(location);
                    }
                }

                if (validLocations.Count == 0) continue;

                result.Add(new HoverMappingEntry
                {
                    HoverName = entry.HoverName,
                    Locations = validLocations
                });
            }

            return result;
        }
        public static List<string>? GetLocations(string mapKey, string hoverName)
        {
            if (string.IsNullOrWhiteSpace(mapKey) || string.IsNullOrWhiteSpace(hoverName)) return null;

            if (!_config.Mappings.TryGetValue(mapKey, out var set)) return null;

            var entries = set.Entries
                .Where(e => string.Equals(e.HoverName, hoverName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (entries.Count == 0) return null;

            var validLocations = new List<string>();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                foreach (var locationName in entry.Locations)
                {
                    if (string.IsNullOrWhiteSpace(locationName)) continue;

                    if (!seen.Add(locationName)) continue;

                    if (!MapPointResolver.IsRealLocation(locationName)) continue;

                    validLocations.Add(locationName);

                }
            }

            if (validLocations.Count == 0) return null;

            return validLocations;
        }
    }
}
