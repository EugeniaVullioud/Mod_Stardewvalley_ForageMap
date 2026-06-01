using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ForageTrackerModSV
{
    internal static class DrawerUIStrings
    {
        public const string Forage = "Forage today:";

    }
    internal static class EditorUIStrings
    {
        // ================
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
