using ForageTrackerModSV;
using StardewModdingAPI;

namespace ForageTrackerMod
{
    /// <summary>
    /// One manual mapping: a hoverable map element (by name/ID) → the set of
    /// internal GameLocation names it should resolve to.
    /// </summary>
    public sealed class HoverMappingEntry
    {
        public string HoverName { get; set; } = string.Empty;
        public List<string> Locations { get; set; } = new();
    }

    /// <summary>
    /// All manual mappings for a single map (mapKey == the same editor key
    /// used by MapRegionConfig.RegionsByMap, e.g. "Town", "Farm").
    /// </summary>
    public sealed class HoverMappingSet
    {
        public List<HoverMappingEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Root persisted file: hoverMappings.json
    ///
    /// ── DESIGN ───────────────────────────────────────────────────────────────
    /// Mirrors MapRegionConfig's "per-map dictionary" shape so the new panel
    /// follows the same multi-map convention as the existing region editor.
    ///
    /// Mappings is the live/edited table. Defaults is a frozen snapshot taken
    /// the first time a map is seen (or refreshed explicitly) — "Reset to
    /// Default" restores Mappings[mapKey] from Defaults[mapKey].
    ///
    /// Stored separately from regions.json (hoverMappings.json) since this
    /// fully replaces MapPointResolver's algorithmic guesses rather than
    /// extending the region-rectangle system.
    /// </summary>
    public sealed class HoverMappingConfig
    {
        public Dictionary<string, HoverMappingSet> Mappings { get; set; } = new();
        public Dictionary<string, HoverMappingSet> Defaults { get; set; } = new();
    }

    /// <summary>
    /// Loads/saves hoverMappings.json and provides lookup used by
    /// MapPointResolver.ResolveAll(). This is the single source of truth for
    /// hover → location resolution; the old scoring algorithm in
    /// MapPointResolver is kept only as a fallback for hoverables that have
    /// not yet been manually mapped (see MapPointResolver.ResolveAll).
    ///
    /// ── ON CACHING ──────────────────────────────────────────────────────────
    /// An earlier version of this store kept a second "_lookup" dictionary
    /// alongside Mappings purely to speed up reads. That second copy went
    /// stale whenever the editor panel mutated Mappings directly (via
    /// EnsureMap) without rebuilding the cache, which is exactly what caused
    /// assignments made in the editor to silently disappear on the next
    /// selection. There is now exactly one copy of the data (Mappings) and
    /// every read (GetLocations) goes through it directly — a dictionary
    /// lookup plus a short linear scan over one map's entries, which is cheap
    /// enough that a cache bought nothing but a second place for bugs to hide.
    /// </summary>
    public static class HoverMappingStore
    {
        private const string FileName = "hoverMappings.json";

        private static IModHelper? _helper;
        private static IMonitor? _monitor;
        private static HoverMappingConfig _cfg = new();

        public static void Init(IModHelper helper, IMonitor monitor)
        {
            _helper = helper;
            _monitor = monitor;
            Load();
        }

        public static HoverMappingConfig Config => _cfg;

        public static void Load()
        {
            if (_helper == null) return;
            _cfg = _helper.Data.ReadJsonFile<HoverMappingConfig>(FileName) ?? new HoverMappingConfig();

            if (_cfg.Mappings.Count == 0)
            {
                var defaults = CreateDefaultConfig();
                _monitor?.Log("[HoverMappingStore] CreateDefaultConfig", LogLevel.Debug);

                _cfg.Mappings = defaults.Mappings;
                _cfg.Defaults = defaults.Defaults;

                Save();
            }
            else
            {
                _monitor?.Log("[HoverMappingStore] Has Mapping", LogLevel.Debug);

            }
        }

        public static void Save()
        {
            if (_helper == null) return;
            _helper.Data.WriteJsonFile(FileName, _cfg);
            _monitor?.Log("[HoverMappingStore] Saved hoverMappings.json", LogLevel.Debug);
        }

        /// <summary>
        /// Returns the manually-mapped locations for a hoverable on the given
        /// map, or null if no manual mapping exists for it (caller should then
        /// fall back to the legacy resolver). Always reads live from Mappings —
        /// see the class remarks for why there is no separate cache.
        /// </summary>
        public static List<string>? GetLocations(string mapKey, string hoverName)
        {
            if (string.IsNullOrEmpty(mapKey) || string.IsNullOrEmpty(hoverName)) return null;

            if (!_cfg.Mappings.TryGetValue(mapKey, out var set)) return null;

            var entry = set.Entries.FirstOrDefault(e => string.Equals(e.HoverName, hoverName, StringComparison.OrdinalIgnoreCase));
            return entry?.Locations;
        }

        public static bool HasMapping(string mapKey, string hoverName) =>
            GetLocations(mapKey, hoverName) != null;

        /// <summary>
        /// Ensures Mappings[mapKey] and Defaults[mapKey] exist. Called by the
        /// editor when a map tab is opened so new maps start with empty sets
        /// rather than throwing on first access.
        /// </summary>
        public static HoverMappingSet EnsureMap(string mapKey)
        {
            if (!_cfg.Mappings.TryGetValue(mapKey, out var set))
            {
                set = new HoverMappingSet();
                _cfg.Mappings[mapKey] = set;
            }
            if (!_cfg.Defaults.ContainsKey(mapKey))
            {
                // Freeze the current (likely empty) state as the default the
                // first time this map is seen. Editors that want a richer
                // baseline should call SnapshotAsDefault explicitly after
                // seeding initial mappings (e.g. from a one-time auto-import).
                _cfg.Defaults[mapKey] = CloneSet(set);
            }
            return set;
        }

        /// <summary>
        /// Freezes the current mapping state for a map as its new "default" —
        /// used after an intentional bulk edit that should become the new
        /// reset baseline. Not called automatically.
        /// </summary>
        public static void SnapshotAsDefault(string mapKey)
        {
            if (_cfg.Mappings.TryGetValue(mapKey, out var set))
                _cfg.Defaults[mapKey] = CloneSet(set);
        }

        /// <summary>
        /// Reverts Mappings[mapKey] back to its frozen Defaults[mapKey] state.
        /// Caller is responsible for calling Save() afterward.
        /// </summary>
        public static void ResetToDefault(string mapKey)
        {
            if (string.IsNullOrWhiteSpace(mapKey)) return;
            _monitor?.Log("[HoverMappingStore] ResetToDefault " + mapKey, LogLevel.Debug);

            if (_cfg.Defaults.TryGetValue(mapKey, out var defaults) && defaults != null)
            {
                _cfg.Mappings[mapKey] = CloneSet(defaults);
            }
            else
            {
                _monitor?.Log($"[HoverMappingStore] No defaults found for '{mapKey}'", LogLevel.Warn);
                _cfg.Mappings[mapKey] = new HoverMappingSet();
            }

            Save();
        }

        private static HoverMappingSet CloneSet(HoverMappingSet src) => new()
        {
            Entries = src.Entries
                .Select(e => new HoverMappingEntry { HoverName = e.HoverName, Locations = new List<string>(e.Locations) })
                .ToList()
        };


        /// <summary>
        /// Creates an empty default config. Actual defaults are seeded at runtime
        /// by SeedFromResolver() once MapPointResolver has built its index from
        /// the live MapPage — this is the only way to get the correct point.name
        /// values that appear in mapPage.points.
        /// </summary>
        private static HoverMappingConfig CreateDefaultConfig() => new HoverMappingConfig();

        /// <summary>
        /// Seeds the mapping for an editor map key from MapPointResolver's built index.
        /// Called the first time a map tab is viewed so the player sees useful defaults.
        ///
        /// Uses the EXACT point.name strings from mapPage.points as HoverName values.
        /// Hardcoded display names cannot be used because they differ by game version
        /// and Content Patcher mods. The resolver provides best-guess location lists.
        ///
        /// Safe to call multiple times — skips keys that already have entries.
        /// </summary>
        public static void SeedFromResolver(string mapKey, StardewValley.Menus.MapPage mapPage, ForageTracker? tracker)
        {
            if (_helper == null || mapPage == null) return;

            MapPointResolver.EnsureBuilt(mapPage);
            var set = EnsureMap(mapKey);
            bool mappingsChanged = false;

            // Import explicitly configured custom mappings.
            var customEntries =
                CustomHoverMappingStore.GetValidEntries(mapKey);

            foreach (var customEntry in customEntries)
            {
                var existing = set.Entries.FirstOrDefault(e =>
                    string.Equals(
                        e.HoverName,
                        customEntry.HoverName,
                        StringComparison.OrdinalIgnoreCase));

                if (existing == null)
                {
                    set.Entries.Add(new HoverMappingEntry
                    {
                        HoverName = customEntry.HoverName,
                        Locations = new List<string>(customEntry.Locations)
                    });

                    mappingsChanged = true;

                    _monitor?.Log(
                        $"[HoverMappingStore][CUSTOM IMPORT] " +
                        $"Added '{customEntry.HoverName}' => " +
                        $"[{string.Join(", ", customEntry.Locations)}]",
                        LogLevel.Info);
                }
                else
                {
                    foreach (var location in customEntry.Locations)
                    {
                        if (existing.Locations.Contains(
                            location,
                            StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        existing.Locations.Add(location);
                        mappingsChanged = true;

                        _monitor?.Log(
                            $"[HoverMappingStore][CUSTOM IMPORT] " +
                            $"Added location '{location}' to " +
                            $"'{customEntry.HoverName}'",
                            LogLevel.Info);
                    }
                }
            }


            // Seed missing parent entries without overwriting manual mappings.
            var points = ForageTrackerModSV.Compatibility.MapPageCompat.GetPoints(mapPage);

            _monitor?.Log(
                $"[HoverMappingStore][SEED] MapKey='{mapKey}', " +
                $"AvailablePoints=[{string.Join(", ", points.Select(p => p.name))}]",
                LogLevel.Info);

            foreach (var point in points)
            {
                if (string.IsNullOrWhiteSpace(point.name))
                    continue;

                var resolved = MapPointResolver.ResolveAll(mapKey, point.name, tracker);

                // Preserve Default parent-area mappings.
                int slash = point.name.IndexOf('/');

                if (slash > 0)
                {
                    string parent = point.name[..slash];
                    string child = point.name[(slash + 1)..];

                    if (string.Equals(
                        child,
                        "Default",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (MapPointResolver.IsRealLocation(parent))
                        {
                            resolved.Add(parent);
                        }
                    }
                }

                // Add normalized automatic matches.
                resolved.AddRange(MapPointResolver.GetNormalizedMatches(point.name));

                // Retrieve manually configured locations.
                var custom = CustomHoverMappingStore.GetLocations(mapKey, point.name);

                if (custom != null) resolved.AddRange(custom);

                // Validate and deduplicate.
                resolved = resolved
                    .Where(MapPointResolver.IsRealLocation)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (resolved.Count == 0) continue;

                var existingEntry = set.Entries.FirstOrDefault(e =>
                    string.Equals(
                        e.HoverName,
                        point.name,
                        StringComparison.OrdinalIgnoreCase));

                if (existingEntry == null)
                {
                    set.Entries.Add(new HoverMappingEntry
                    {
                        HoverName = point.name,
                        Locations = resolved
                    });

                    mappingsChanged = true;
                }
                else
                {
                    foreach (var location in resolved)
                    {
                        if (!existingEntry.Locations.Contains(
                            location,
                            StringComparer.OrdinalIgnoreCase))
                        {
                            existingEntry.Locations.Add(location);
                            mappingsChanged = true;
                        }
                    }
                }
            }

            // Ensure defaults contain the seeded parent-child relationships.
            if (!_cfg.Defaults.TryGetValue(mapKey, out var defaults) || defaults == null || (defaults.Entries.Count == 0 && set.Entries.Count > 0))
            {
                _cfg.Defaults[mapKey] = CloneSet(set);
                mappingsChanged = true;
            }

            if (mappingsChanged) Save();

            _monitor?.Log($"[HoverMappingStore] Seeded map '{mapKey}': " + $"{set.Entries.Count} mapping entries, " + $"{_cfg.Defaults[mapKey].Entries.Count} default entries", LogLevel.Debug);

        }
    }
}