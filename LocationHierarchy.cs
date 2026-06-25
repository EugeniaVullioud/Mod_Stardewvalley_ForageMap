using StardewValley;
using StardewValley.Buildings;
using StardewModdingAPI;

namespace ForageTrackerMod
{
    public enum LocationType
    {
        Outdoor,
        FarmBuilding,   // Barn, Coop, FarmHouse, etc.
        IndoorMap           // Saloon, ElliottHouse, SeedShop, etc.
    }
    /// <summary>
    /// Builds and caches the full parent→child hierarchy of every GameLocation
    /// in the game world, including modded locations.
    ///
    /// ── DESIGN ───────────────────────────────────────────────────────────────
    ///
    /// SDV's location graph is a two-level tree rooted at Game1.locations:
    ///
    ///   Game1.locations           ← "root" outdoor/instanced locations
    ///     └─ GameLocation
    ///          └─ buildings[]     ← buildings ON this location (e.g. FarmHouse on Farm)
    ///               └─ Building
    ///                    └─ indoors.Value   ← the interior GameLocation (e.g. FarmHouse)
    ///                         └─ buildings[] ← some interiors have their own buildings
    ///                              └─ ...   ← recursively (Cellar under FarmHouse, etc.)
    ///
    /// The hierarchy is derived entirely from live game data — no hardcoding.
    /// It works with any mod that follows SDV's standard location registration:
    ///   • Locations added via Game1.locations appear at the root.
    ///   • Buildings placed on any location expose their interior via indoors.Value.
    ///
    /// ── WHAT IT EXPOSES ──────────────────────────────────────────────────────
    ///
    ///   • GetParent(locationName)          → parent location name, or null if root
    ///   • GetChildren(locationName)        → all direct interior/sub-location names
    ///   • GetAllDescendants(locationName)  → recursive subtree (all nested interiors)
    ///   • GetRootAncestor(locationName)    → topmost outdoor location in the chain
    ///   • IsIndoor(locationName)           → true if location has a parent
    ///   • AllLocations                     → flat list of every known location name
    ///
    /// ── REBUILDING ───────────────────────────────────────────────────────────
    ///
    /// Call Rebuild() once after save-loaded (DayStarted / SaveLoaded events).
    /// The hierarchy does not change mid-day in vanilla; for mods that add
    /// locations dynamically, call Rebuild() again after they register.
    ///
    /// ── MOD COMPATIBILITY ────────────────────────────────────────────────────
    ///
    /// Any mod that:
    ///   (a) adds a GameLocation to Game1.locations, OR
    ///   (b) places a Building with an indoors.Value on any existing location
    /// will be picked up automatically on the next Rebuild().
    ///
    /// Mods using Content Patcher to add map locations are also covered because
    /// CP locations go through the standard Game1.locations list.
    /// </summary>
    public static class LocationHierarchy
    {
        // ── Internal graph ────────────────────────────────────────────────────

        // locationName → parent locationName (null = root)
        private static readonly Dictionary<string, string?> _parent  = new();

        // locationName → direct child location names
        private static readonly Dictionary<string, List<string>> _children = new();

        // locationName → cached GameLocation reference (avoids repeated lookups)
        private static readonly Dictionary<string, GameLocation> _byName = new();

        private static bool _built = false;
        private static IMonitor? _monitor;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Wires up the monitor for debug logging. Call once in ModEntry.Entry.
        /// </summary>
        public static void Init(IMonitor monitor) => _monitor = monitor;

        /// <summary>
        /// Rebuilds the hierarchy from the live game world.
        /// Call on SaveLoaded and DayStarted.
        /// Safe to call multiple times — clears and rebuilds cleanly.
        /// </summary>
        public static void Rebuild()
        {
            _parent.Clear();
            _children.Clear();
            _byName.Clear();
            _built = false;

            foreach (var root in Game1.locations)
            {
                if (root == null) continue;
                Visit(root, parentName: null);
            }

            _built = true;
            BuildKindCache();

            _monitor?.Log(
                $"[LocationHierarchy] Built: {_byName.Count} locations, " +
                $"{_parent.Values.Count(v => v != null)} interiors.",
                LogLevel.Debug);
        }

        /// <summary>
        /// Returns the name of the direct parent location, or null if this is
        /// a root location (i.e. directly in Game1.locations).
        /// Returns null for unknown location names.
        /// </summary>
        public static string? GetParent(string locationName)
        {
            EnsureBuilt();
            return _parent.TryGetValue(locationName, out var p) ? p : null;
        }

        /// <summary>
        /// Returns the names of all direct children (building interiors) of
        /// this location. Empty list if none or unknown.
        /// </summary>
        public static IReadOnlyList<string> GetChildren(string locationName)
        {
            EnsureBuilt();
            return _children.TryGetValue(locationName, out var c)
                ? c
                : System.Array.Empty<string>();
        }

        /// <summary>
        /// Returns all descendants recursively — the full interior subtree.
        /// For example, GetAllDescendants("Farm") returns
        /// ["FarmHouse", "Cellar", "Greenhouse", ...].
        /// </summary>
        public static IEnumerable<string> GetAllDescendants(string locationName)
        {
            EnsureBuilt();
            if (!_children.TryGetValue(locationName, out var direct))
                yield break;

            foreach (var child in direct)
            {
                yield return child;
                foreach (var desc in GetAllDescendants(child))
                    yield return desc;
            }
        }

        /// <summary>
        /// Walks up the parent chain and returns the topmost root location.
        /// For "Cellar" this returns "Farm".
        /// For a root location this returns the location itself.
        /// </summary>
        public static string GetRootAncestor(string locationName)
        {
            EnsureBuilt();
            string current = locationName;
            int guard = 32; // prevent infinite loops from malformed hierarchies
            while (guard-- > 0)
            {
                if (!_parent.TryGetValue(current, out var p) || p == null)
                    return current;
                current = p;
            }
            return current;
        }

        /// <summary>
        /// Returns true if the location is an interior (has a parent).
        /// Uses live game data — works for any modded building.
        /// </summary>
        public static bool IsIndoor(string locationName)
        {
            EnsureBuilt();
            return _parent.TryGetValue(locationName, out var p) && p != null;
        }

        /// <summary>
        /// Returns all location names known to the hierarchy.
        /// </summary>
        public static IReadOnlyCollection<string> AllLocations
        {
            get { EnsureBuilt(); return _byName.Keys; }
        }

        /// <summary>
        /// Tries to get the live GameLocation by name. Faster than
        /// Game1.getLocationFromName because it uses the pre-built cache.
        /// </summary>
        public static bool TryGetLocation(string name, out GameLocation loc)
        {
            EnsureBuilt();
            return _byName.TryGetValue(name, out loc!);
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void Visit(GameLocation location, string? parentName)
        {
            if (location == null) return;

            string name = location.Name;

            // Guard against duplicate names (can happen with some mods).
            if (_byName.ContainsKey(name))
            {
                _monitor?.Log(
                    $"[LocationHierarchy] Duplicate location name '{name}' " +
                    $"(parent: {parentName ?? "root"}). Skipping.",
                    LogLevel.Trace);
                return;
            }

            _byName[name]   = location;
            _parent[name]   = parentName;
            _children[name] = new List<string>();

            // Register as child of parent.
            if (parentName != null && _children.TryGetValue(parentName, out var siblingList))
                siblingList.Add(name);

            // Recurse into building interiors.
            foreach (var building in location.buildings)
            {
                var interior = building?.indoors?.Value;
                if (interior == null) continue;
                Visit(interior, parentName: name);
            }
        }
        public static LocationType GetLocationType(string name)
        {
            EnsureBuilt();

            if (!_byName.TryGetValue(name, out var loc))
                return LocationType.Outdoor;
            /*
            if (_buildingInteriors.Contains(name))
                return LocationType.FarmBuilding;*/

            if (loc.IsOutdoors)
                return LocationType.Outdoor;

            return LocationType.IndoorMap;
        }
        private static readonly Dictionary<string, LocationType> _kindCache = new();
        private static void BuildKindCache()
        {
            _kindCache.Clear();

            foreach (var loc in Game1.locations)
            {
                if (loc == null) continue;
                /*
                if (_buildingInteriors.Contains(loc.Name))
                    _kindCache[loc.Name] = LocationType.FarmBuilding;*/

                else if (loc.IsOutdoors)
                    _kindCache[loc.Name] = LocationType.Outdoor;

                else
                    _kindCache[loc.Name] = LocationType.IndoorMap;
            }
        }
        public static LocationType GetKindFast(string name)
        {
            if (string.IsNullOrEmpty(name))
                return LocationType.IndoorMap; // safe default
            if (_kindCache.TryGetValue(name, out var kind))
                return kind;

            return LocationType.IndoorMap; // safe fallback
        }
        private static void EnsureBuilt()
        {
            if (!_built) Rebuild();
        }
    }
}
