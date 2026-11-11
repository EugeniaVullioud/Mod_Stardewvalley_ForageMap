using ForageTrackerMod;
using StardewModdingAPI;
using System.Text.Json;

namespace ForageTrackerModSV
{
    public static class DefaultMapRegionLoader
    {
        const string FileName = "DefaultMaps.json";

        static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        static MapRegionConfig? LoadDefaultFile(IModHelper helper, IMonitor monitor)
        {
            string path = Path.Combine(helper.DirectoryPath, FileName);

            if (!File.Exists(path))
            {
                monitor.Log($"Default map regions file missing: {path}", LogLevel.Error);
                return null;
            }

            string json = File.ReadAllText(path);

            return JsonSerializer.Deserialize<MapRegionConfig>(json, JsonOptions            );
        }

        // Used only when creating a brand-new player config.
        public static void InitializeNewConfig(IModHelper helper, IMonitor monitor, MapRegionConfig config)
        {
            var defaults = LoadDefaultFile(helper, monitor);

            if (defaults == null) return;

            if (config.RegionsByMap == null)
            {
                config.RegionsByMap = CloneMaps(defaults.RegionsByMap);
                config.Bindings = defaults.Bindings != null ? new Dictionary<string, string>(defaults.Bindings) : new Dictionary<string, string>();
            }
        }

        // Restore default tabs without touching custom tabs.
        public static bool RestoreDefaults(IModHelper helper, IMonitor monitor, MapRegionConfig config)
        {
            try
            {
                var defaults = LoadDefaultFile(helper, monitor);

                if (defaults?.RegionsByMap == null) return false;

                config.RegionsByMap ??= new();
                config.Bindings ??= new();

                foreach (var pair in defaults.RegionsByMap)
                {
                    // Replace existing default tab or add missing tab.
                    config.RegionsByMap[pair.Key] = pair.Value.Select(CloneRegion).ToList();

                    // Restore the default binding for this tab.
                    if (defaults.Bindings != null && defaults.Bindings.TryGetValue(pair.Key, out string? binding))
                    {
                        config.Bindings[pair.Key] = binding;
                    }
                    else
                    {
                        // The default tab has no binding.
                        config.Bindings.Remove(pair.Key);
                    }
                }

                monitor.Log("Default map tabs restored successfully.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed to restore default map tabs: {ex}", LogLevel.Error);
                return false;
            }
        }

        static Dictionary<string, List<MapRegionData>> CloneMaps(Dictionary<string, List<MapRegionData>> source)
        {
            return source.ToDictionary(pair => pair.Key, pair => pair.Value.Select(CloneRegion).ToList());
        }

        static MapRegionData CloneRegion(MapRegionData source)
        {
            return new MapRegionData
            {
                Name = source.Name,
                Locations = new List<string>(source.Locations),
                Left = source.Left,
                Top = source.Top,
                Right = source.Right,
                Bottom = source.Bottom,
                ColorPacked = source.ColorPacked
            };
        }
    }
}
