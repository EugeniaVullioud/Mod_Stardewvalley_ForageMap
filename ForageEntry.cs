using Microsoft.Xna.Framework;

namespace ForageTrackerMod
{
    /// <summary>
    /// Represents a single forageable item present on the map at day-start.
    /// LocationName is intentionally omitted — it is already the dictionary key
    /// in ForageTracker, so storing it here would be redundant on every entry.
    /// </summary>
    public sealed class ForageEntry
    {
        /// <summary>Qualified item id, e.g. "(O)281".</summary>
        public string ItemId { get; init; } = string.Empty;

        /// <summary>Human-readable display name resolved at scan time.</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>Tile position within the location.</summary>
        public Vector2 Tile { get; init; }

        /// <summary>True once the player has collected this item.</summary>
        public bool Picked { get; set; } = false;
    }
}