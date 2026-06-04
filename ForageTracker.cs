using ForageTrackerModSV.Debug;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using SObject = StardewValley.Object;

namespace ForageTrackerMod
{
    /// <summary>
    /// Tracks forageable items across all locations for the current in-game day.
    ///
    /// Internal storage uses two parallel structures per location:
    ///   • A List&lt;ForageEntry&gt; for iteration (building summaries).
    ///   • A Dictionary&lt;Vector2, ForageEntry&gt; for O(1) pickup lookup by tile.
    ///
    /// The pre-computed summary cache means the tooltip draw path never allocates
    /// — it reads a cached object that is only rebuilt when state actually changes
    /// (day start or a pickup event).
    /// </summary>
    public sealed class ForageTracker
    {
        // -------------------------------------------------------------------------
        // Internal per-location record
        // -------------------------------------------------------------------------

        private sealed class LocationData
        {
            /// <summary>All entries for fast iteration.</summary>
            public readonly List<ForageEntry> Entries = new();

            /// <summary>Tile → entry for O(1) pickup marking.</summary>
            public readonly Dictionary<Vector2, ForageEntry> ByTile = new();

            /// <summary>
            /// Pre-computed summary: DisplayName → (ItemId, Total, Remaining).
            /// Rebuilt by RebuildSummary() after any state change.
            /// </summary>
            public Dictionary<string, SummaryEntry> Summary = new();

            public void RebuildSummary()
            {
                Summary = new Dictionary<string, SummaryEntry>(Entries.Count);
                foreach (var e in Entries)
                {
                    if (!Summary.TryGetValue(e.DisplayName, out var s))
                    {
                        s = new SummaryEntry { ItemId = e.ItemId };
                        Summary[e.DisplayName] = s;
                    }
                    s.Total++;
                    if (!e.Picked) s.Remaining++;
                }
            }
        }

        // -------------------------------------------------------------------------
        // Public summary type (read by the patcher)
        // -------------------------------------------------------------------------

        public sealed class SummaryEntry
        {
            public string ItemId { get; init; } = string.Empty;
            public int Total { get; set; }
            public int Remaining { get; set; }
        }

        // -------------------------------------------------------------------------
        // Fields
        // -------------------------------------------------------------------------

        private readonly IMonitor _monitor;

        /// <summary>LocationName → per-location data.</summary>
        private readonly Dictionary<string, LocationData> _data = new();

        // -------------------------------------------------------------------------
        // Constructor
        // -------------------------------------------------------------------------

        public ForageTracker(IMonitor monitor)
        {
            _monitor = monitor;
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Full scan at day-start.  Clears previous data and rebuilds everything.
        /// Runs once per in-game day — allocation cost here is acceptable.
        /// </summary>
        public void ScanAllLocations()
        {
            _data.Clear();
            int total = 0;

            foreach (var location in GetAllLocations())
            {
                var ld = ScanLocation(location);
                if (ld.Entries.Count == 0) continue;

                ld.RebuildSummary();
                _data[location.Name] = ld;
                total += ld.Entries.Count;
            }
            Debugger.DebugLog(_monitor, $"[ForageTracker] Day scan: {total} forageable(s) across {_data.Count} location(s).", LogLevel.Debug);
        }

        /// <summary>
        /// Marks a forageable as picked.  O(1) via tile dictionary.
        /// Invalidates the cached summary for that location.
        /// </summary>
        public void MarkPicked(string locationName, Vector2 tile)
        {
            if (!_data.TryGetValue(locationName, out var ld)) return;
            if (!ld.ByTile.TryGetValue(tile, out var entry)) return;
            if (entry.Picked) return;

            entry.Picked = true;
            ld.RebuildSummary();   // O(n entries in this location) — only on actual pickup

            Debugger.DebugLog(_monitor, $"[ForageTracker] Picked: {entry.DisplayName} @ {tile} in {locationName}.", LogLevel.Trace);
        }

        /// <summary>
        /// Marks a forageable as un-picked (e.g. the player dropped it back).
        /// If the tile is already tracked and un-picked, this is a no-op.
        /// If the tile is not tracked at all (new object placed mid-day),
        /// the item is added as a new entry and the summary rebuilt.
        /// </summary>
        public void MarkAdded(string locationName, Vector2 tile, string itemId, string displayName)
        {
            if (!_data.TryGetValue(locationName, out var ld))
            {
                // Location had no forage — create a new slot for it.
                ld = new LocationData();
                _data[locationName] = ld;
            }

            if (ld.ByTile.TryGetValue(tile, out var existing))
            {
                // Already tracked (was picked earlier) — just un-pick it.
                if (!existing.Picked) return;
                existing.Picked = false;
            }
            else
            {
                // Brand-new tile (player dropped a forageable they had in inventory).
                var entry = new ForageEntry
                {
                    ItemId      = itemId,
                    DisplayName = displayName,
                    Tile        = tile,
                    Picked      = false
                };
                ld.Entries.Add(entry);
                ld.ByTile[tile] = entry;
            }

            ld.RebuildSummary();

            Debugger.DebugLog(_monitor, $"[ForageTracker] Added/restored: {displayName} @ {tile} in {locationName}.", LogLevel.Trace);
        }

        /// <summary>
        /// Returns the pre-computed summary for a location.
        /// This is a direct reference — zero allocation on the draw path.
        /// Returns null if the location has no tracked forageables.
        /// </summary>
        public Dictionary<string, SummaryEntry>? GetSummary(string locationName)
        {
            if (!_data.TryGetValue(locationName, out var ld)) return null;
            return ld.Summary.Count > 0 ? ld.Summary : null;
        }

        // -------------------------------------------------------------------------
        // Public helpers used by MapTooltipPatcher
        // -------------------------------------------------------------------------

        /// <summary>All currently tracked location names (for key-cache rebuild).</summary>
        public IEnumerable<string> TrackedLocationNames => _data.Keys;

        /// <summary>Fast existence check — avoids exposing the internal dict.</summary>
        public bool IsTracked(string locationName) => _data.ContainsKey(locationName);

        // -------------------------------------------------------------------------
        // Private helpers
        // -------------------------------------------------------------------------

        private static LocationData ScanLocation(GameLocation location)
        {
            var ld = new LocationData();

            foreach (var (tile, obj) in location.Objects.Pairs)
            {
                if (!IsForageable(obj)) continue;

                var entry = new ForageEntry
                {
                    ItemId = obj.QualifiedItemId,
                    DisplayName = obj.DisplayName,
                    Tile = tile
                };

                ld.Entries.Add(entry);
                ld.ByTile[tile] = entry;
            }

            return ld;
        }

        /// <summary>
        /// Iterates all locations including building interiors — no intermediate
        /// collections, no LINQ allocations.
        /// </summary>
        private static IEnumerable<GameLocation> GetAllLocations()
        {
            foreach (var location in Game1.locations)
            {
                if (location == null) continue;
                yield return location;

                foreach (var building in location.buildings)
                {
                    var interior = building.indoors.Value;
                    if (interior != null) yield return interior;
                }
            }
        }
        private static bool IsForageable(SObject obj) => obj != null && !obj.bigCraftable.Value && obj.IsSpawnedObject;
    }
}