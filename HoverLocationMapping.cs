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
    /// Loads/saves hoverMappings.json and provides fast lookup used by
    /// MapPointResolver.ResolveAll(). This is the single source of truth for
    /// hover → location resolution; the old scoring algorithm in
    /// MapPointResolver is kept only as a fallback for hoverables that have
    /// not yet been manually mapped (see MapPointResolver.ResolveAll).
    /// </summary>
    public static class HoverMappingStore
    {
        private const string FileName = "hoverMappings.json";

        private static IModHelper? _helper;
        private static IMonitor? _monitor;
        private static HoverMappingConfig _cfg = new();

        // mapKey → (hoverName → locations), rebuilt whenever _cfg changes,
        // so editor saves and tooltip lookups never re-scan the entry list.
        private static readonly Dictionary<string, Dictionary<string, List<string>>> _lookup =
            new(StringComparer.OrdinalIgnoreCase);

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
            RebuildLookup();
        }

        public static void Save()
        {
            if (_helper == null) return;
            _helper.Data.WriteJsonFile(FileName, _cfg);
            RebuildLookup();
            _monitor?.Log("[HoverMappingStore] Saved hoverMappings.json", LogLevel.Debug);
        }

        /// <summary>
        /// Returns the manually-mapped locations for a hoverable on the given
        /// map, or null if no manual mapping exists for it (caller should then
        /// fall back to the legacy resolver).
        /// </summary>
        public static List<string>? GetLocations(string mapKey, string hoverName)
        {
            if (string.IsNullOrEmpty(mapKey) || string.IsNullOrEmpty(hoverName))
                return null;

            return _lookup.TryGetValue(mapKey, out var byHover)
                && byHover.TryGetValue(hoverName, out var locs)
                ? locs
                : null;
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
            _cfg.Mappings[mapKey] = _cfg.Defaults.TryGetValue(mapKey, out var def)
                ? CloneSet(def)
                : new HoverMappingSet();
            RebuildLookup();
        }

        private static HoverMappingSet CloneSet(HoverMappingSet src) => new()
        {
            Entries = src.Entries
                .Select(e => new HoverMappingEntry { HoverName = e.HoverName, Locations = new List<string>(e.Locations) })
                .ToList()
        };

        private static void RebuildLookup()
        {
            _lookup.Clear();
            foreach (var (mapKey, set) in _cfg.Mappings)
            {
                var byHover = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in set.Entries)
                {
                    if (string.IsNullOrEmpty(entry.HoverName)) continue;
                    byHover[entry.HoverName] = entry.Locations;
                }
                _lookup[mapKey] = byHover;
            }
        }
    }
}
