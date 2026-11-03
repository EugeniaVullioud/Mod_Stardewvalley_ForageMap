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

                    _helper.Data.WriteJsonFile(
                        FileName,
                        _config);

                    _monitor?.Log($"[CustomMappings] Created {FileName}", LogLevel.Info);

                    return;
                }

                _config = loaded;

                _monitor?.Log(
                    $"[CustomMappings] Loaded {FileName}",
                    LogLevel.Info);

                // Report duplicate entries and duplicate locations.
                foreach (var map in _config.Mappings)
                {
                    var seenHoverNames =
                        new HashSet<string>(
                            StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in map.Value.Entries)
                    {
                        if (!seenHoverNames.Add(entry.HoverName))
                        {
                            _monitor?.Log(
                                $"[CustomMappings] Duplicate hover name " +
                                $"'{entry.HoverName}' in map '{map.Key}'.",
                                LogLevel.Warn);
                        }

                        var seenLocations =
                            new HashSet<string>(
                                StringComparer.OrdinalIgnoreCase);

                        foreach (var location in entry.Locations)
                        {
                            if (!seenLocations.Add(location))
                            {
                                _monitor?.Log(
                                    $"[CustomMappings] Duplicate location " +
                                    $"'{location}' in '{entry.HoverName}'.",
                                    LogLevel.Warn);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor?.Log(
                    $"[CustomMappings] Failed to load {FileName}: " +
                    ex.Message,
                    LogLevel.Error);

                _config = new CustomHoverMappingConfig();
            }
        }

        public static List<string>? GetLocations(string mapKey, string hoverName)
        {
            if (string.IsNullOrWhiteSpace(mapKey) || string.IsNullOrWhiteSpace(hoverName))
            {
                return null;
            }
            _monitor?.Log(
    $"[CustomMappings][LOOKUP] Map='{mapKey}', " +
    $"Hover='{hoverName}'",
    LogLevel.Info);
            if (!_config.Mappings.TryGetValue(mapKey, out var set))
            {
                _monitor?.Log(
                    $"[CustomMappings][MAP NOT FOUND] '{mapKey}'. " +
                    $"Available maps: {string.Join(", ", _config.Mappings.Keys)}",
                    LogLevel.Warn);

                return null;
            }
            foreach (var e in set.Entries)
            {
                _monitor?.Log(
                    $"[CustomMappings][ENTRY] " +
                    $"Configured='{e.HoverName}', " +
                    $"Requested='{hoverName}', " +
                    $"Match={string.Equals(e.HoverName, hoverName, StringComparison.OrdinalIgnoreCase)}",
                    LogLevel.Info);
            }
            var entries = set.Entries
                .Where(e => string.Equals(
                    e.HoverName,
                    hoverName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count == 0)
                return null;

            var validLocations = new List<string>();

            var seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                foreach (var locationName in entry.Locations)
                {
                    if (string.IsNullOrWhiteSpace(locationName))
                        continue;

                    if (!seen.Add(locationName))
                        continue;

                    if (!MapPointResolver.IsRealLocation(locationName))
                    {
                        _monitor?.Log(
                            $"[CustomMappings] INVALID location " +
                            $"'{locationName}' for '{mapKey}/" +
                            $"{hoverName}'. Location does not exist.",
                            LogLevel.Warn);

                        continue;
                    }

                    validLocations.Add(locationName);

                    _monitor?.Log(
                        $"[CustomMappings] VALID '{mapKey}/" +
                        $"{hoverName}' → '{locationName}'",
                        LogLevel.Trace);
                }
            }

            if (validLocations.Count == 0)
            {
                _monitor?.Log(
                    $"[CustomMappings] No valid locations for " +
                    $"'{mapKey}/{hoverName}'. Falling back to resolver.",
                    LogLevel.Warn);

                return null;
            }

            _monitor?.Log(
                $"[CustomMappings] MATCH '{mapKey}/{hoverName}' → " +
                $"[{string.Join(", ", validLocations)}]",
                LogLevel.Info);

            return validLocations;
        }
    }
}
