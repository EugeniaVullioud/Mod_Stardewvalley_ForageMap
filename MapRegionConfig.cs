namespace ForageTrackerMod;

/// <summary>
/// Persisted region definitions. Saved to a separate file from config.json
/// so it isn't reset when the player resets mod settings.
/// File: ForageTracker/regions.json  (inside the mod's data folder)
/// </summary>
public sealed class MapRegionConfig
{
    /// <summary>All player-defined regions. Organized by map. </summary>
    public Dictionary<string, List<MapRegionData>> RegionsByMap { get; set; } = DefaultRegions();

    public int EdgeGrabPixels { get; set; } = 10;
    public float RegionLabelScale { get; set; } = 0.6f;
    /// <summary>
    /// Maps each editor map-tab key to the SDV map key it is bound to
    /// (e.g. "MyIslandTab" → "Island").  The tooltip uses this to decide
    /// which tab's regions to apply when the player opens a given game map.
    /// Null or missing entry means the tab is unbound (shown with ⚠ in editor).
    /// </summary>
    public Dictionary<string, string>? Bindings { get; set; } = DefaultBindings();

    private static Dictionary<string, string> DefaultBindings() => new()
    {
        ["Town"]   = "Town",
        ["Island"] = "Island",
    };

    private static Dictionary<string, List<MapRegionData>> DefaultRegions() => new()
    {
        
        ["Town"] = new()
        {
            new() { Name = "Pelican Town",     Locations = new() { "Town" },
                    Left=0.30f, Top=0.42f, Right=0.60f, Bottom=0.72f, ColorPacked=0x8000AAFF },
            new() { Name = "Cindersap Forest", Locations = new() { "Forest","LeahHouse","WizardHouse","WizardHouseBasement","Tent" },
                    Left=0.00f, Top=0.52f, Right=0.30f, Bottom=0.88f, ColorPacked=0x8000CC44 },
            new() { Name = "The Mountain",     Locations = new() { "Mountain","AdventureGuild","Carpenter" },
                    Left=0.35f, Top=0.00f, Right=0.75f, Bottom=0.28f, ColorPacked=0x80886644 },
            new() { Name = "The Beach",        Locations = new() { "Beach","FishShop","ElliottHouse" },
                    Left=0.35f, Top=0.72f, Right=0.75f, Bottom=1.00f, ColorPacked=0x8000CCFF },
            new() { Name = "The Farm",         Locations = new() { "Farm","FarmHouse","Cellar","Greenhouse" },
                    Left=0.00f, Top=0.20f, Right=0.35f, Bottom=0.55f, ColorPacked=0x80FFCC00 },
            new() { Name = "Secret Woods",     Locations = new() { "Woods" },
                    Left=0.00f, Top=0.42f, Right=0.14f, Bottom=0.55f, ColorPacked=0x80006600 },
            new() { Name = "Railroad",         Locations = new() { "Railroad","BathHouseEntry","BathHousePool","BathHouseLocker","WitchSwamp","WitchHut","WitchWarpCave" },
                    Left=0.52f, Top=0.00f, Right=0.80f, Bottom=0.18f, ColorPacked=0x80996699 },
            new() { Name = "Desert",           Locations = new() { "Desert","SandyHouse","SkullCave" },
                    Left=0.00f, Top=0.00f, Right=0.30f, Bottom=0.20f, ColorPacked=0x80FFDD88 },
            new() { Name = "Ginger Island",    Locations = new() { "IslandSouth","IslandNorth","IslandEast","IslandWest","IslandFarmHouse","IslandFieldOffice","IslandShrine","Caldera","VolcanoDungeon0" },
                    Left=0.78f, Top=0.00f, Right=1.00f, Bottom=0.55f, ColorPacked=0x80FF8800 },
        },

        ["Island"] = new()
        {
            new()
            {
                Name = "Ginger Island",
                Locations = new()
                {
                    "IslandSouth",
                    "IslandNorth",
                    "IslandEast",
                    "IslandWest"
                },
                Left = 0.10f,
                Top = 0.10f,
                Right = 0.50f,
                Bottom = 0.50f,
                ColorPacked = 0x80FF8800
            }
        }
        
    };
}
