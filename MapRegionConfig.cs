namespace ForageTrackerMod;

/// <summary>
/// Persisted region definitions. Saved to a separate file from config.json
/// so it isn't reset when the player resets mod settings.
/// File: ForageTracker/regions.json  (inside the mod's data folder)
/// </summary>
public sealed class MapRegionConfig
{
    /// <summary>All player-defined regions. Organized by map. </summary>
    public Dictionary<string, List<MapRegionData>> RegionsByMap { get; set; } = new();

    /// <summary>
    /// Maps each editor map-tab key to the SDV map key it is bound to (e.g. "MyIslandTab" → "Island").  The tooltip uses this to decide
    /// which tab's regions to apply when the player opens a given game map. Null or missing entry means the tab is unbound (shown with ⚠ in editor).
    /// </summary>
    public Dictionary<string, string>? Bindings { get; set; } = new();
    public int EdgeGrabPixels { get; set; } = 10;
    public float RegionLabelScale { get; set; } = 0.6f; 
}
