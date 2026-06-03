namespace ForageTrackerModSV
{
    /// <summary>
    /// Centralized UI string definitions for the mod entry configuration screens.
    /// These strings are used in configuration menus, toggles, tooltips and general UI labels.
    /// Designed to support future localization by keeping all user-facing text in one place.
    /// </summary>
    internal static class ModEntryUIStrings
    {
        // ── General ──────────────────────────────────
        public const string Editor = "Region Editor";
        public const string EditorTooltip = "Open the forage region editor.";
        public const string EditRegions = "Edit Regions";

        // ==================================================
        // INSIDE OWN CONFIG
        // ==================================================
        public const string EnabledStatus = "Enable Forage Tracker";
        public const string EnabledStatusTooltip = "Track and display daily forageable counts on the map tooltip.";

        public const string Display = "Display Mode";
        public const string DisplayTooltip = "What to show per entry: icon + name, icon + count only, or text only.";

        // ── Icons ──────────────────────────────────
        public const string IconScale = "Icon Scale";
        public const string IconScaleTooltip = "Icon size multiplier (1.0 = default).";
        public const string IconOptionBoth = "Icon + Text";
        public const string IconOptionIconOnly = "Icon Only";
        public const string IconOptionTextOnly = "Text Only";
        public const string IconOptionBothDropdown = "Both";
        public const string IconOptionIconOnlyDropdown = "IconOnly";
        public const string IconOptionTextOnlyDropdown = "TextOnly";

        // ── Amount ──────────────────────────────────
        public const string Amount = "Show Remaining Only";
        public const string AmountTooltip = "ON = hide fully-collected items. OFF = always show with remaining / total";

    }
    /// <summary>
    /// UI strings used for small HUD / overlay elements related to forage display.
    /// Typically shown in the in-game UI rather than configuration menus.
    /// </summary>
    internal static class DrawerUIStrings
    {
        public const string Forage = "Forage today:";
    }
    /// <summary>
    /// UI strings used exclusively in the in-game region editor interface.
    /// Includes map selection, region creation tools, dialogs and editor status messages.
    /// </summary>
    internal static class EditorUIStrings
    {
        // ==================================================
        // EDITOR - MAIN
        // ==================================================
        public const string NewMap = "+ New Map";
        public const string LeftArrow = "<";
        public const string RightArrow = ">";

        // ── Map Data ──────────────────────────────────
        public const string TownMapHint = "Main Stardew Valley world map";
        public const string IslandMapHint = "Ginger Island map";
        public const string CustomMapHint = "Custom / modded map:";
        public const string Map = "Map:";

        // ==================================================
        // EDITOR - SIDEBAR
        // ==================================================

        public const string EditRegions = "Edit Regions";

        public const string AreaName = "Area Name:";
        public const string NewAreaDefault = "New Area";
        public const string SDVLocations = "SDV Locations:";

        public const string SelectLocation = "(select location...)";
        public const string AddLocation = "+ Add";
        public const string DownDropdown = "▼";
        public const string DeleteFromDropdown = "✕";

        public const string AddRegion = "+ Add Region";
        public const string DeleteRegion = "✕ Delete";

        // ── Customization ──────────────────────────────────
        public const string CycleAreaColor = "Cycle Color";
        public const string Opacity = "Opacity: ";
        
        public const string BorderTolerance = "Border Tolerance: ";

        public const string CycleTextColor = "Cycle Text Color";
        public const string TextSize = "Text Size:";

        // ── General ──────────────────────────────────
        public const string Bind = "⚑ Bind:";
        public const string Save = "💾 Save";
        public const string Cancel = "✕ Cancel";

        public const string DeleteMap = "🗑 Del Map";

        public const string ClickRegionHint =
            "Click a rectangle on\n" +
            "the map to select it,\n" +
            "or press + Add Region.";

        // ==================================================
        // DIALOGS
        // ==================================================

        public const string NewMapTitle = "New map key name:";

        public const string Create = "✓ Create";

        public const string Delete = "Delete";
        public const string CancelSimple = "Cancel";

        public const string ConfirmDeleteMapInstruction = "Delete map tab\n";
        public const string ConfirmDeleteMapWarning = "\nThis tab will be eliminated.";

        // ==================================================
        // STATUS
        // ==================================================

        public const string UnboundMapWarning =
            "⚠ This map tab is not bound to any in-game map.\n" +
            "Press \"Bind to: ...\" above to link it.";

        public const string EditingNotCurrentWarning = "You are editing";
        public const string EditingNotCurrentWarningBound = "(bound to";
        public const string EditingNotCurrentWarningCurrentIs = "Current in-game map:";

        public const string RegionsSaved = "Forage Tracker: regions saved.";
    }
}
