using ForageTrackerModSV;
using ForageTrackerModSV.Compatibility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System.Text.RegularExpressions;

namespace ForageTrackerMod
{
    /// <summary>
    /// In-game map region editor.
    ///
    /// LAYOUT
    /// ┌──────────────────────────────────────┬──────────────────────┐
    /// │  [◀ Map: Town ▶]  [+ New Map]        │  Edit Regions        │
    /// │                                      │  ──────────────────  │
    /// │  World map image (correct aspect)    │  Name: [__________]  │
    /// │                                      │  Locations:          │
    /// │  Colored rectangles per region.      │  [dropdown ▼] [+Add] │
    /// │  Click = select.                     │  • Beach       [✕]   │
    /// │  Drag interior = move.               │  • Forest      [✕]   │
    /// │  Drag edge handle = resize.          │  [Cycle Color   ■ ]  │
    /// │  White border = selected.            │  L:0.3  R:0.6        │
    /// │                                      │  T:0.3  B:0.6        │
    /// │                                      │  [+ Add Region]      │
    /// │                                      │  [✕ Delete Region]   │
    /// │                                      │  [💾 Save]           │
    /// │                                      │  [✕ Cancel]          │
    /// └──────────────────────────────────────┴──────────────────────┘
    ///
    /// MULTI-MAP
    /// Each map has its own region list stored under a key in regions.json.
    /// Use ◀ ▶ to cycle between known maps, or [+ New Map] to add one.
    ///
    /// LOCATIONS DROPDOWN
    /// Every known SDV location name is listed. The player picks from the
    /// list — no free text, so no typos. Custom/modded locations discovered
    /// at runtime (from ForageTracker.TrackedLocationNames) are added too.
    /// </summary>
    public sealed class MapRegionEditor : IClickableMenu
    {
        // ── Layout constants ──────────────────────────────────────────────────

        int TabMapGap = 4;

        /// <summary>
        /// All size constants derived from a single TextScale value.
        /// Recomputed in the constructor and whenever the scale changes.
        /// </summary>
        private struct UI
        {
            // ── Single source of truth ────────────────────────────────────────
            // TextScale = Game1.options.uiScale * base factor (0.65).
            // Every height, gap and font scale is derived from it so the whole
            // sidebar stays proportional at any SDV UI scale setting.
            public float TextScale;

            // Font scales
            public float TitleScale;    // dialogueFont for header
            public float NormalScale;   // smallFont for labels and buttons
            public float SmallScale;    // smallFont for secondary labels
            public float TinyScale;     // smallFont for count hints

            // Heights — all measured from smallFont.LineSpacing * NormalScale
            public int LineH;       // one text line height
            public int LabelH;      // label row above a control (LineH + vpad)
            public int FieldH;      // text field / dropdown  (fits one text line + vpad)
            public int SliderH;     // slider track
            public int BtnH;        // button (fits label + vertical padding)
            public int TabH;        // map tab (= BtnH)
            public int RowH;        // list row (= FieldH)
            public int Pad;         // general gap between elements

            // Minimum text width helpers
            public int BtnPadX;     // horizontal padding inside a button (each side)

            // textScale: base scale (e.g. 0.85f). screenW: unused, kept for compat.
            public static UI Compute(float textScale, int screenW)
            {
                var u = new UI();
                u.TextScale = textScale;
                u.TitleScale = textScale * 1.25f;
                u.NormalScale = textScale * 0.95f;
                u.SmallScale = textScale * 0.85f;
                u.TinyScale = textScale * 0.75f;

                // All heights measured from the actual rendered line at NormalScale
                // so they stay proportional when textScale changes.
                u.LineH = (int)Math.Ceiling(Game1.smallFont.LineSpacing * u.NormalScale);
                // vpad = equal top/bottom padding inside controls, proportional to text.
                int vpad = Math.Max(3, u.LineH / 4);
                u.LabelH = u.LineH + 2;                    // label fits one line + tiny gap
                u.FieldH = u.LineH + vpad * 2;             // field: text + equal top/bottom pad
                u.RowH = u.LineH + vpad;                  // list row: compact
                u.SliderH = u.LineH + vpad;                  // slider track same as row
                u.BtnH = u.LineH + vpad * 4;             // button: generous vertical padding
                u.TabH = u.BtnH;
                u.Pad = Math.Max(6, u.LineH / 2);
                u.BtnPadX = Math.Max(10, u.LineH);
                return u;
            }

            // Width needed to fit label inside a button with standard padding.
            public int MeasureBtn(string label) =>
                (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X * NormalScale)
                + BtnPadX * 2;
        }

        UI _ui;

        private static readonly Color[] Palette =
        {
            new(  0, 170, 255),
            new(  0, 204,  68),
            new(255, 204,   0),
            new(255, 136,   0),
            new(220,  50,  50),
            new(180,  60, 240),
            new(  0, 200, 200),
            new(255, 180, 200),
        };

        static readonly Color[] TextPalette =
        {
            Color.White,
            Color.Black,
            Color.Yellow,
            Color.Cyan,
            Color.LimeGreen,
            Color.Orange,
            Color.Red,
            Color.Magenta
        };
        bool _draggingOpacity;

        // Tab navigation
        Rectangle _btnTabLeftRect;
        Rectangle _btnTabRightRect;

        // First tab currently visible in the viewport.
        int _firstVisibleTab = 0;

        // Width reserved for navigation buttons
        int TabArrowW => _ui.BtnH;

        // All known SDV location names — shown in the dropdown.
        // Populated once in constructor from hardcoded list + runtime tracker data.
        readonly List<string> _allLocations;

        // ── State ─────────────────────────────────────────────────────────────

        readonly IModHelper _helper;
        readonly IMonitor _monitor;
        readonly MapRegionConfig _cfg;
        readonly Action<MapRegionConfig> _onSave;
        int _edgeGrab;

        // All maps being edited — copied from cfg so changes are only committed on Save
        readonly Dictionary<string, List<MapRegionData>> _editMaps;

        // Currently viewed map
        string _currentMapKey;
        List<MapRegionData> _currentRegions => _editMaps[_currentMapKey];
        readonly List<string> _mapKeys;  // ordered list for ◀ ▶ navigation

        // Map display
        readonly MapPage? _mapPage;
        readonly Rectangle _mapPanel;
        Rectangle _mapArea;   // actual image rect (from MapRenderUtility)

        // Sidebar
        readonly Rectangle _sideArea;

        // Sidebar sub-rects
        Rectangle _btnNewMapRect;

        readonly List<Rectangle> _tabRects = new();  // one per _mapKeys entry

        bool _draggingTextScale;
        // Selection & drag
        int _sel = -1;
        bool _dragging = false;
        bool _draggingEdgeGrab;

        enum Handle { None, Move, Left, Right, Top, Bottom }
        Handle _handle = Handle.None;
        Point _dragAnchor;
        float _origL, _origT, _origR, _origB;

        // Text input (name field only — locations use dropdown)
        string _fieldName = "";
        bool _nameFocused = false;

        // Dropdown state
        bool _dropdownOpen = false;
        int _dropdownScroll = 0;
        const int DropdownVisibleRows = 8;
        // DropdownRowH must use _ui.RowH — NOT a const — so it scales with text.
        int DropdownRowH => Math.Max(_ui.RowH, _ui.LineH + 4);

        // New-map dialog
        bool _newMapDialogOpen = false;
        string _newMapName = "";
        const int newMapW = 130;

        // Delete map confirmation - dialogue
        bool _confirmDeleteMapOpen = false;
        string _mapPendingDelete = "";

        // Map binding: mapKey → SDV map-key string (e.g. "Town", "Island").
        // A map tab that has no binding is shown with an ⚠ warning.
        // The binding controls which tab the tooltip uses for the live map.
        // Stored alongside regions in regions.json via MapRegionConfig.Bindings.
        readonly Dictionary<string, string> _bindings;  // mapKey → bound SDV key

        // Location list scroll (replaces the old "+N more" cap)
        int _locListScroll = 0;
        int LocListRowH => Math.Max(_ui.RowH, _ui.LineH + 4);
        Rectangle _locListScrollbarTrack;

        string LiveMapKey => MapKeyHelper.GetCurrentMapKey();
        bool IsCurrentTabBoundToLiveMap => _bindings.TryGetValue(_currentMapKey, out var boundTo) && boundTo == LiveMapKey;

        SidebarLayout _layoutWithHoverButton;
        SidebarLayout _layoutWithoutHoverButton;
        SidebarLayout ActiveLayout => IsCurrentTabBoundToLiveMap ? _layoutWithHoverButton : _layoutWithoutHoverButton;


        // ── Constructor ───────────────────────────────────────────────────────

        public MapRegionEditor(IModHelper helper, IMonitor monitor, MapRegionConfig cfg, Action<MapRegionConfig> onSave) : base(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height)
        {
            _helper = helper;
            _monitor = monitor;
            _cfg = cfg;
            _onSave = onSave;

            _ui = UI.Compute(1f, Game1.uiViewport.Width);

            // _ui.SidebarW used as the minimum reservation; actual width set after map placement.
            _edgeGrab = Math.Max(2, cfg.EdgeGrabPixels);

            // Deep-copy all maps so edits don't affect live data until Save
            _editMaps = cfg.RegionsByMap.ToDictionary(kv => kv.Key, kv => kv.Value.Select(CloneRegion).ToList());

            // Ensure at least one map exists
            if (_editMaps.Count == 0) _editMaps["Town"] = new List<MapRegionData>();

            // Load bindings (mapKey → SDV map key string)
            _bindings = cfg.Bindings != null ? new Dictionary<string, string>(cfg.Bindings) : new Dictionary<string, string>();

            _mapKeys = _editMaps.Keys.OrderBy(k => k).ToList();

            string? boundTab = FindTabBoundToCurrentMap();

            if (boundTab != null && _mapKeys.Contains(boundTab))
            {
                _currentMapKey = boundTab; // SeedCurrentMap() is called after _mapPage is constructed below
            }
            else
            {
                // No tab is bound to the currently displayed world map.
                _currentMapKey = _mapKeys[0]; // Graceful fallback
            }
            EnsureSelectedTabVisible();
            ClampTabViewport();
            // ── Compute map area then fit sidebar to whatever remains ─────────
            //
            // ORDER MATTERS: map first, sidebar second.
            //
            // The live map size is controlled by SDV's UI scale, so it changes
            // at runtime. We must place the map first (using the real rendered
            // rect from ComputeActualMapRect), then position the sidebar to the
            // RIGHT of the map — never overlapping it — using whatever horizontal
            // space is left over. This way the sidebar always adjusts to the map
            // size regardless of UI scale.
            //
            // Map area: letterbox the native 1360×720 map image into the full
            // screen minus the minimum sidebar width and padding. Then use
            // ComputeActualMapRect to get the true drawn sub-rect (which may be
            // smaller than the MapPage bounds due to internal SDV margins).
            //
            // Sidebar: anchored to _mapArea.Right + Pad, filling the remaining
            // screen width. SidebarW is clamped so it never exceeds what fits.

            int sw = Game1.uiViewport.Width;
            int sh = Game1.uiViewport.Height;

            // ── Step 1: map area ──────────────────────────────────────────────
            const int NativeMapW = 1360;
            const int NativeMapH = 720;

            // Reserve minimum sidebar space so the map doesn't crush it.
            int minSidebar = 260;
            int availW = sw - minSidebar - _ui.Pad * 3;
            int availH = sh - _ui.Pad * 2 - _ui.TabH - (int)(TabMapGap * _ui.TextScale);

            float scaleX = (float)availW / NativeMapW;
            float scaleY = (float)availH / NativeMapH;
            float mapScale = Math.Min(scaleX, scaleY);

            int mapW = (int)(NativeMapW * mapScale);
            int mapH = (int)(NativeMapH * mapScale);
            int mapX = _ui.Pad + (availW - mapW) / 2;
            int mapY = _ui.Pad + _ui.TabH + TabMapGap + (availH - mapH) / 2;

            _mapPanel = new Rectangle(mapX, mapY, mapW, mapH);
            _mapPage = new MapPage(mapX, mapY, mapW, mapH);
            _mapArea = MapRenderUtility.ComputeActualMapRect(_mapPage);

            // Seed default hover mappings for the initial tab now that _mapPage exists.
            if (boundTab != null && _mapKeys.Contains(boundTab))
            {
                HoverMappingStore.SeedFromResolver(_currentMapKey, _mapPage, MapTooltipDrawer.Tracker);
            }
            MapTooltipDrawer.LastLiveMapRect ??= new Rectangle(mapX, mapY, mapW, mapH);

            // ── Step 2: sidebar — right of _mapArea, never overlapping it ─────
            int sideX = _mapArea.Right + _ui.Pad;
            int sideW = sw - sideX - _ui.Pad;          // fill remaining width
            sideW = Math.Clamp(sideW, 260, 480);
            _sideArea = new Rectangle(sideX, _ui.Pad, sideW, sh - _ui.Pad * 2);

            // Build location list from the live game world — no hardcoding.
            // BuildKnownLocations() walks Game1.locations + building interiors,
            // so Quarry and all modded locations appear automatically.
            _allLocations = BuildKnownLocations(monitor);
            _allLocations.Sort();

            //LayoutSidebar();
            _layoutWithHoverButton = CalculateSidebarLayout(true);
            _layoutWithoutHoverButton = CalculateSidebarLayout(false);
        }

        // ── Dynamic sidebar layout ────────────────────────────────────────────
        //
        // The sidebar height changes with every screen resolution. This method
        // measures the total space required for fixed-height controls, then
        // allocates what remains to the scrollable location list. Everything is
        // computed from _sideArea at call time — no hardcoded pixel offsets.
        //
        // Fixed regions (top → bottom):
        //   Header block    : title + map key + hint  (~80 px, measured)
        //   Name field      : label (18) + field (34) + gap
        //   Location header : label (18) + dropdown row (34) + gap
        //   Location list   : dynamic — as many LocListRowH rows as fit
        //   Scrollbar       : same height as list, 10 px wide (right-aligned)
        //   Color button    : BtnH
        //   Opacity slider  : label (18) + slider (24) + gap
        //   Text color btn  : 24
        //   Text scale      : label (18) + slider (24) + gap
        //   Edge grab       : label (18) + slider (24) + gap
        //   Separator gap   : Pad * 2
        //   Bind + Del Map  : BtnH (side by side)
        //   Add + Del Region: BtnH (side by side)
        //   Save            : BtnH
        //   Cancel          : BtnH
        //   Bottom padding  : Pad
        private SidebarLayout CalculateSidebarLayout(bool showHoverButton)
        {
            var layout = new SidebarLayout();

            int x = _sideArea.X + _ui.Pad;
            int w = _sideArea.Width - _ui.Pad * 2;
            int gap = _ui.Pad;

            // Header
            int titleH = (int)Math.Ceiling(Game1.dialogueFont.MeasureString(EditorUIStrings.EditRegions).Y * _ui.TitleScale);

            int mapKeyH = (int)Math.Ceiling(Game1.smallFont.MeasureString("Map: X").Y * _ui.NormalScale);

            int mapHintH = (int)Math.Ceiling(Game1.smallFont.MeasureString("X").Y * _ui.SmallScale);

            int headerH = titleH + mapKeyH + mapHintH + gap * 2;

            // Fixed cost
            int fixedCost =
                  _ui.LabelH + _ui.FieldH + gap
                + _ui.LabelH + _ui.FieldH + gap
                + gap
                + _ui.BtnH + gap
                + _ui.LabelH + _ui.SliderH + gap
                + _ui.BtnH + gap
                + _ui.LabelH + _ui.SliderH + gap
                + _ui.LabelH + _ui.SliderH + gap * 2
                + _ui.BtnH + gap
                + (showHoverButton ? _ui.BtnH + gap : 0)
                + _ui.BtnH + gap
                + _ui.BtnH + gap
                + _ui.BtnH;

            // Location list
            int totalAvail = _sideArea.Height - _ui.Pad - headerH;
            int listAvail = Math.Max(0, totalAvail - fixedCost);

            int visRows = Math.Clamp(listAvail / _ui.RowH, 1, 8);
            int locListH = _ui.RowH * visRows;

            int scrollbarW = Math.Max(8, _ui.Pad / 2);

            int y = _sideArea.Y + _ui.Pad + headerH;

            // Name
            y += _ui.LabelH;
            layout.NameField = new Rectangle(x, y, w, _ui.FieldH);
            y += _ui.FieldH + gap;

            // Location dropdown
            y += _ui.LabelH;

            int addW = Math.Max(
                _ui.MeasureBtn(EditorUIStrings.AddLocation),
                (int)(w * 0.22f));

            layout.LocDropdown = new Rectangle(
                x, y, w - addW - _ui.Pad, _ui.FieldH);

            layout.AddLocButton = new Rectangle(
                layout.LocDropdown.Right + _ui.Pad,
                y, addW, _ui.FieldH);

            y += _ui.FieldH + gap;

            layout.LocScrollbar = new Rectangle(
                _sideArea.Right - _ui.Pad - scrollbarW,
                y, scrollbarW, locListH);

            y += locListH + gap;

            // Color
            layout.ColorButton = new Rectangle(x, y, w, _ui.BtnH);
            y += _ui.BtnH + gap;

            // Opacity
            y += _ui.LabelH;
            layout.OpacitySlider = new Rectangle(x, y, w, _ui.SliderH);
            y += _ui.SliderH + gap;

            // Text color
            layout.TextColorButton = new Rectangle(x, y, w, _ui.BtnH);
            y += _ui.BtnH + gap;

            // Text scale
            y += _ui.LabelH;
            layout.TextScaleSlider = new Rectangle(x, y, w, _ui.SliderH);
            y += _ui.SliderH + gap;

            // Edge grab
            y += _ui.LabelH;
            layout.EdgeGrabSlider = new Rectangle(x, y, w, _ui.SliderH);
            y += _ui.SliderH + gap * 2;

            // Bind + Delete Map
            int half = (w - _ui.Pad) / 2;

            layout.BindButton = new Rectangle(x, y, half, _ui.BtnH);
            layout.DeleteMapButton = new Rectangle(x + half + _ui.Pad, y, half, _ui.BtnH);

            y += _ui.BtnH + gap;

            // Conditional hover mapping button
            if (showHoverButton)
            {
                layout.HoverMapButton = new Rectangle(x, y, w, _ui.BtnH);
                y += _ui.BtnH + gap;
            }

            // Add + Delete Region
            layout.AddRegionButton = new Rectangle(x, y, half, _ui.BtnH);
            layout.DeleteRegionButton = new Rectangle(x + half + _ui.Pad, y, half, _ui.BtnH);

            y += _ui.BtnH + gap;

            // Save + Cancel
            layout.SaveButton = new Rectangle(x, y, w, _ui.BtnH);
            y += _ui.BtnH + gap;

            layout.CancelButton = new Rectangle(x, y, w, _ui.BtnH);

            layout.VisibleLocationRows = visRows;

            return layout;
        }
        private HashSet<string> GetAllAssignedLocations()
        {
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string hoverName in _hoverElementNames)
            {
                var locations = HoverMappingStore.GetLocations(_currentMapKey, hoverName);

                if (locations == null) continue;

                foreach (string location in locations)
                {
                    if (string.IsNullOrWhiteSpace(location))
                        continue;

                    // Ignore Default because it isn't an actual location assignment.
                    if (string.Equals(
                        location,
                        "Default",
                        StringComparison.OrdinalIgnoreCase))
                        continue;

                    assigned.Add(location);
                }
            }

            return assigned;
        }
        string? FindTabBoundToCurrentMap()
        {
            string liveMapKey = MapKeyHelper.GetCurrentMapKey();

            foreach (var pair in _bindings)
            {
                if (pair.Value == liveMapKey) return pair.Key;
            }
            return null;
        }
        // ── Suppress the farm-name banner that SDV draws over full-screen menus ─

        /// <summary>
        /// Returning true here tells the game this menu draws its own close button
        /// and prevents SDV from drawing the bottom HUD strip (farm name, clock, etc.)
        /// that normally appears over full-screen menus.
        /// </summary>
        public override bool shouldDrawCloseButton() => false;

        // ── Public text input receiver (called from ModEntry.OnButtonPressed) ─

        public void ReceiveTextInput(char c)
        {
            if (char.IsControl(c)) return;

            if (_hoverPanelOpen)
            {
                if (_hoverFilterFocused)
                {
                    _hoverFilterText += c;
                    OnHoverFilterTextChanged();
                }
                return; // panel owns all text input while open
            }

            if (_newMapDialogOpen)
            {
                _newMapName += c;
                _newMapError = ""; // clear error as soon as they type
            }
            else if (_nameFocused) _fieldName += c;
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void RefreshMapArea()
        {
            // _mapArea is set once in the constructor from the live GameMenu bounds.
            // Tab switches don't change the map rect — nothing to do here.
            // Kept as a hook in case future SDV versions need per-tab recalculation.
        }

        public override void draw(SpriteBatch b)
        {
            // Dim background
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.6f);

            // ── Draw map tabs FIRST (above the box, flush with map top) ─────────
            DrawMapTabs(b);

            // ── Map box — sized exactly to _mapArea, no unused border space ───
            // The box border wraps the map image tightly. Tabs sit just above it.
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                _mapArea.X - 4, _mapArea.Y - 4,
                _mapArea.Width + 8, _mapArea.Height + 8,
                Color.White, drawShadow: false);

            // ── Map image ─────────────────────────────────────────────────────
            // _mapPage is sized to _mapArea exactly, so no erase strips needed.
            _mapPage?.draw(b);

            // ── Region overlays ───────────────────────────────────────────────
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            for (int i = 0; i < _currentRegions.Count; i++)
            {
                var r = _currentRegions[i];
                var sr = ToScreen(r);
                bool isSel = i == _sel;
                bool isHov = !isSel && sr.Contains(mx, my);

                float alpha = r.Opacity / 255f;

                b.Draw(Game1.fadeToBlackRect, sr, r.Color * (isHov ? alpha : alpha * 0.85f));
                DrawBorder(b, sr, isSel ? Color.White : (isHov ? Color.Yellow : r.Color), isSel ? 3 : (isHov ? 2 : 1));

                if (sr.Width > 40)
                {
                    float scale = _cfg.RegionLabelScale;

                    Vector2 size = Game1.smallFont.MeasureString(r.Name) * scale;

                    float maxWidth = sr.Width - 8;
                    float maxHeight = sr.Height - 8;

                    if (size.X > maxWidth)
                        scale *= maxWidth / size.X;

                    size = Game1.smallFont.MeasureString(r.Name) * scale;

                    if (size.Y > maxHeight) scale *= maxHeight / size.Y;

                    b.DrawString(Game1.smallFont, r.Name, new Vector2(sr.X + 4, sr.Y + 4), r.TextColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                }

                if (isSel)
                {
                    DrawHandles(b, sr);

                    // Visualize grab zones
                    // Opacity-based visibility
                    byte alphaVis = (byte)Math.Min(255, _currentRegions[_sel].Opacity * 1.15f); // +15%
                    Color dragZone = _currentRegions[_sel].Color;
                    dragZone.A = alphaVis;

                    // LEFT
                    b.Draw(
                        Game1.fadeToBlackRect,
                        new Rectangle(
                            sr.Left,
                            sr.Top,
                            _edgeGrab,
                            sr.Height),
                        dragZone);

                    // RIGHT
                    b.Draw(
                        Game1.fadeToBlackRect,
                        new Rectangle(
                            sr.Right - _edgeGrab,
                            sr.Top,
                            _edgeGrab,
                            sr.Height),
                        dragZone);

                    // TOP
                    b.Draw(
                        Game1.fadeToBlackRect,
                        new Rectangle(
                            sr.Left,
                            sr.Top,
                            sr.Width,
                            _edgeGrab),
                        dragZone);

                    // BOTTOM
                    b.Draw(
                        Game1.fadeToBlackRect,
                        new Rectangle(
                            sr.Left,
                            sr.Bottom - _edgeGrab,
                            sr.Width,
                            _edgeGrab),
                        dragZone);
                }
            }
#if DEBUG
            // ── Debug overlay — draws fraction grid and mouse position ────────
            // BEGIN DEBUG BLOCK — remove before shipping
            if (MapTooltipDrawer.DebugMode)
            {
                int mx2 = Game1.getMouseX(), my2 = Game1.getMouseY();
                float dbgRelX = (_mapArea.Width > 0) ? (mx2 - _mapArea.X) / (float)_mapArea.Width : 0f;
                float dbgRelY = (_mapArea.Height > 0) ? (my2 - _mapArea.Y) / (float)_mapArea.Height : 0f;

                // Crosshair at mouse position clamped to map area
                if (_mapArea.Contains(mx2, my2))
                {
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(mx2 - 1, _mapArea.Y, 2, _mapArea.Height), Color.Yellow * 0.5f);
                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(_mapArea.X, my2 - 1, _mapArea.Width, 2), Color.Yellow * 0.5f);
                }

                // Fraction readout at cursor
                string fracLabel = $"frac ({dbgRelX:F3}, {dbgRelY:F3})";
                b.DrawString(Game1.smallFont, fracLabel,
                    new Vector2(mx2 + 12, my2 - 16),
                    Color.Yellow, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 1f);

                // mapArea info in corner of map panel
                string areaInfo =
                    $"DEBUG EDITOR\n" +
                    $"mapArea: x={_mapArea.X} y={_mapArea.Y}\n" +
                    $"w={_mapArea.Width} h={_mapArea.Height}";
                var asz = Game1.smallFont.MeasureString(areaInfo) * 0.55f;
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(_mapArea.X + 4, _mapArea.Bottom - (int)asz.Y - 12,
                                  (int)asz.X + 8, (int)asz.Y + 8),
                    Color.Black * 0.7f);
                b.DrawString(Game1.smallFont, areaInfo,
                    new Vector2(_mapArea.X + 8, _mapArea.Bottom - (int)asz.Y - 8),
                    Color.Yellow, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 1f);
            }
            // END DEBUG BLOCK
#endif
            // ── Sidebar ───────────────────────────────────────────────────────
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                _sideArea.X - 4, _sideArea.Y - 4,
                _sideArea.Width + 8, _sideArea.Height + 8,
                Color.White, drawShadow: false);

            int tx = _sideArea.X + _ui.Pad;
            int ty = _sideArea.Y + _ui.Pad;

            float titleScale = _ui.TitleScale;
            float mapKeyScale = _ui.NormalScale;
            float hintScale = _ui.SmallScale;

            b.DrawString(Game1.dialogueFont, EditorUIStrings.EditRegions,
                new Vector2(tx, ty), Game1.textColor,
                0f, Vector2.Zero, titleScale, SpriteEffects.None, 1f);
            ty += (int)(Game1.dialogueFont.MeasureString(EditorUIStrings.EditRegions).Y * titleScale);

            string mapKeyLine = $"{EditorUIStrings.Map} {_currentMapKey}";
            b.DrawString(Game1.smallFont, mapKeyLine,
                new Vector2(tx, ty), Game1.textColor,
                0f, Vector2.Zero, mapKeyScale, SpriteEffects.None, 1f);
            ty += (int)(Game1.smallFont.MeasureString(mapKeyLine).Y * mapKeyScale);

            string mapHint = _currentMapKey switch
            {
                "Town" => EditorUIStrings.TownMapHint,
                "Island" => EditorUIStrings.IslandMapHint,
                _ => $"{EditorUIStrings.CustomMapHint} \"{_currentMapKey}\""
            };
            b.DrawString(Game1.smallFont, mapHint,
                new Vector2(tx, ty), Game1.textColor * 0.6f,
                0f, Vector2.Zero, hintScale, SpriteEffects.None, 1f);

            if (_sel >= 0 && _sel < _currentRegions.Count)
            {
                var r = _currentRegions[_sel];

                // Name field
                b.DrawString(Game1.smallFont, EditorUIStrings.AreaName,
                    new Vector2(tx, ActiveLayout.NameField.Y - _ui.LabelH), Game1.textColor,
                    0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
                DrawTextField(b, ActiveLayout.NameField, _fieldName, _nameFocused, _ui);

                // Location section header
                b.DrawString(Game1.smallFont, EditorUIStrings.SDVLocations,
                    new Vector2(tx, ActiveLayout.LocDropdown.Y - _ui.LabelH), Game1.textColor,
                    0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);

                // Dropdown
                DrawDropdownBox(b, ActiveLayout.LocDropdown);
                DrawButton(b, ActiveLayout.AddLocButton, EditorUIStrings.AddLocation, Color.White, _ui);

                // ── Scrollable location list ──────────────────────────────────
                int locListX = tx;
                int locListY0 = ActiveLayout.LocDropdown.Bottom + 4;
                int locListW = _sideArea.Width - _ui.Pad * 2 - ActiveLayout.LocScrollbar.Width - 4;
                int maxScroll = Math.Max(0, r.Locations.Count - ActiveLayout.VisibleLocationRows);
                _locListScroll = Math.Clamp(_locListScroll, 0, maxScroll);

                // Background for the list area
                var listAreaRect = new Rectangle(locListX, locListY0,
                    locListW + ActiveLayout.LocScrollbar.Width + 4,
                    LocListRowH * ActiveLayout.VisibleLocationRows);
                b.Draw(Game1.fadeToBlackRect, listAreaRect, Color.Black * 0.25f);

                // Rows
                for (int i = 0; i < ActiveLayout.VisibleLocationRows; i++)
                {
                    int idx = i + _locListScroll;
                    if (idx >= r.Locations.Count) break;

                    int rowY = locListY0 + i * LocListRowH;
                    var locRect = new Rectangle(locListX, rowY, locListW - 34, LocListRowH - 2);
                    var removeRect = new Rectangle(locRect.Right + 4, rowY, 28, LocListRowH - 2);
                    b.Draw(Game1.fadeToBlackRect, locRect, Color.Black * 0.2f);
                    b.DrawString(Game1.smallFont, r.Locations[idx],
                        new Vector2(locRect.X + 4, locRect.Y + (_ui.RowH - _ui.LineH) / 2),
                        Color.White, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

                    DrawButton(b, removeRect, EditorUIStrings.DeleteFromDropdown, new Color(220, 100, 100), _ui);
                }

                // Scrollbar
                if (r.Locations.Count > ActiveLayout.VisibleLocationRows)
                {
                    var track = ActiveLayout.LocScrollbar;
                    track.Y = locListY0;

                    b.Draw(Game1.fadeToBlackRect, track, Color.Black * 0.4f);

                    float thumbFrac = (float)ActiveLayout.VisibleLocationRows / r.Locations.Count;
                    float thumbOffset = maxScroll > 0 ? (float)_locListScroll / maxScroll : 0f;
                    int thumbH = Math.Max(16, (int)(track.Height * thumbFrac));
                    int thumbY = track.Y + (int)((track.Height - thumbH) * thumbOffset);

                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(track.X + 1, thumbY, track.Width - 2, thumbH),
                        Color.White * 0.7f);

                    // Count hint
                    b.DrawString(Game1.smallFont,
                        $"{_locListScroll + 1}–{Math.Min(_locListScroll + ActiveLayout.VisibleLocationRows, r.Locations.Count)}/{r.Locations.Count}",
                        new Vector2(locListX, locListY0 + LocListRowH * ActiveLayout.VisibleLocationRows + 2),
                        Color.White * 0.5f, 0f, Vector2.Zero, _ui.TinyScale, SpriteEffects.None, 1f);
                }

                // Color button
                DrawButton(b, ActiveLayout.ColorButton, EditorUIStrings.CycleAreaColor, Color.White, _ui);
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(ActiveLayout.ColorButton.Right - _ui.BtnH + 4,
                                  ActiveLayout.ColorButton.Y + 4, _ui.BtnH - 8, _ui.BtnH - 8), r.Color);


                // Opacity 
                b.DrawString(Game1.smallFont, $"{EditorUIStrings.Opacity} {r.Opacity}",
                    new Vector2(ActiveLayout.OpacitySlider.X, ActiveLayout.OpacitySlider.Y - _ui.LabelH), Color.White, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

                b.Draw(Game1.fadeToBlackRect, ActiveLayout.OpacitySlider, Color.Black * 0.6f); // Track
                DrawBorder(b, ActiveLayout.OpacitySlider, Color.White, 1);

                // Opacity Thumb
                float pct = r.Opacity / 255f;

                int thumbX = ActiveLayout.OpacitySlider.X + (int)(ActiveLayout.OpacitySlider.Width * pct);

                b.Draw(Game1.fadeToBlackRect, new Rectangle(thumbX - 4, ActiveLayout.OpacitySlider.Y - 2, 8, ActiveLayout.OpacitySlider.Height + 4), Color.White);


                DrawButton(b, ActiveLayout.TextColorButton, EditorUIStrings.CycleTextColor, Color.White, _ui);

                b.DrawString(Game1.smallFont, $"{EditorUIStrings.TextSize} {_cfg.RegionLabelScale:F2}", new Vector2(ActiveLayout.TextScaleSlider.X, ActiveLayout.TextScaleSlider.Y - _ui.LabelH), Color.White, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

                b.Draw(Game1.fadeToBlackRect, ActiveLayout.TextScaleSlider, Color.Black * 0.6f);

                DrawBorder(b, ActiveLayout.TextScaleSlider, Color.White, 1);

                float pctTxt = (_cfg.RegionLabelScale - 0.4f) / (1.5f - 0.4f);

                int thumbXText = ActiveLayout.TextScaleSlider.X + (int)(ActiveLayout.TextScaleSlider.Width * pctTxt);

                b.Draw(Game1.fadeToBlackRect, new Rectangle(thumbXText - 4, ActiveLayout.TextScaleSlider.Y - 2, 8, ActiveLayout.TextScaleSlider.Height + 4), Color.White);

                // Drag border
                b.DrawString(Game1.smallFont, $"{EditorUIStrings.BorderTolerance} {_edgeGrab}", new Vector2(ActiveLayout.EdgeGrabSlider.X, ActiveLayout.EdgeGrabSlider.Y - _ui.LabelH), Color.White, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

                b.Draw(Game1.fadeToBlackRect, ActiveLayout.EdgeGrabSlider, Color.Black * 0.6f);

                float pctBorder = (_edgeGrab - 2) / 38f;

                int thumbXBorder = ActiveLayout.EdgeGrabSlider.X + (int)(ActiveLayout.EdgeGrabSlider.Width * pctBorder);

                int thumbWidth = _edgeGrab;
                thumbWidth = Math.Clamp(thumbWidth, 6, 30);

                b.Draw(Game1.fadeToBlackRect, new Rectangle(thumbXBorder - 4, ActiveLayout.EdgeGrabSlider.Y - 2, 8, ActiveLayout.EdgeGrabSlider.Height + 4), Color.White);

            }
            else
            {
                // Draw the hint where the region controls would appear, so it
                // never overlaps the header. _nameFieldRect.Y is the top of the
                // control zone — computed dynamically by LayoutSidebar.
                b.DrawString(Game1.smallFont, EditorUIStrings.ClickRegionHint, new Vector2(tx, ActiveLayout.NameField.Y),
                    Game1.textColor * 0.7f, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
            }

            // ── Bind + Delete Map buttons ─────────────────────────────────────
            string liveKey = MapKeyHelper.GetCurrentMapKey();
            bool isBound = _bindings.TryGetValue(_currentMapKey, out var boundTo) && boundTo == liveKey;

            string bindLabel = isBound ? $"✓ {liveKey}" : $"{EditorUIStrings.Bind} {liveKey}";
            Color bindColor = isBound ? new Color(140, 220, 140) : new Color(220, 200, 100);
            DrawButton(b, ActiveLayout.BindButton, bindLabel, bindColor, _ui);

            bool canDelete = _mapKeys.Count > 1; // must always keep at least one tab
            DrawButton(b, ActiveLayout.DeleteMapButton, EditorUIStrings.DeleteMap, canDelete ? new Color(220, 100, 80) : Color.Gray * 0.5f, _ui);

            // ── Binding status banners ────────────────────────────────────────
            // (1) Unbound warning — this map tab has no binding at all
            bool hasAnyBinding = _bindings.ContainsKey(_currentMapKey);
            if (!hasAnyBinding)
            {
                DrawStatusBanner(b, EditorUIStrings.UnboundMapWarning, new Color(220, 160, 40), new Color(80, 50, 0, 200));
            }
            // (2) Wrong-map notice — has a binding but it's for a different live key
            else if (!isBound)
            {
                string notice = $"{EditorUIStrings.EditingNotCurrentWarning} \"{_currentMapKey}\" " +
                                $"{EditorUIStrings.EditingNotCurrentWarningBound} \"{boundTo}\").\n" +
                                $"{EditorUIStrings.EditingNotCurrentWarningCurrentIs} \"{liveKey}\".";
                DrawStatusBanner(b, notice, Color.Yellow, new Color(0, 0, 0, 200));
            }

            if (IsCurrentTabBoundToLiveMap)
            {
                DrawButton(b, ActiveLayout.HoverMapButton, EditorHoverableUIStrings.EditHoverMapping, new Color(120, 170, 230), _ui);
            }

            DrawButton(b, ActiveLayout.AddRegionButton, EditorUIStrings.AddRegion, Color.White, _ui);
            DrawButton(b, ActiveLayout.DeleteRegionButton, EditorUIStrings.Delete, _sel >= 0 ? Color.White : Color.Gray * 0.5f, _ui);
            DrawButton(b, ActiveLayout.SaveButton, EditorUIStrings.Save, new Color(140, 220, 140), _ui);
            DrawButton(b, ActiveLayout.CancelButton, EditorUIStrings.Cancel, new Color(220, 120, 120), _ui);

            // ── Dropdown overlay (drawn last so it's on top) ──────────────────
            if (_dropdownOpen) DrawDropdownList(b);

            // ── New map dialog ────────────────────────────────────────────────
            if (_newMapDialogOpen) DrawNewMapDialog(b);

            // ── Delete map dialog ────────────────────────────────────────────────
            if (_confirmDeleteMapOpen) DrawDeleteMapDialog(b);

            // ── Hover Mapping panel — full-panel overlay, drawn above everything
            // else in the editor (it is its own tab, not a small dialog) ──────
            if (_hoverPanelOpen) DrawHoverMappingPanel(b);

            drawMouse(b);
        }

        // ── Map tabs ──────────────────────────────────────────────────────────

        /// <summary>
        /// Draws one tab per map key above the map panel.
        /// Active tab is highlighted and visually connects to the panel below.
        /// A "+ New Map" button sits at the right end of the tab row.
        /// Also populates _tabRects for click detection.
        /// </summary>
        private void DrawMapTabs(SpriteBatch b)
        {
            _tabRects.Clear();

            //EnsureSelectedTabVisible();

            int tabY =
                _mapArea.Y - _ui.TabH - (int)(TabMapGap * _ui.TextScale);
            /*
            bool hiddenLeft =
                _firstVisibleTab > 0;

            int lastVisible =
                GetLastVisibleTab();

            bool hiddenRight =
                lastVisible < _mapKeys.Count - 1;

       */

            bool allFit = AllTabsFit();
            if (allFit)
            {
                _firstVisibleTab = 0;
            }
            bool hiddenLeft = !allFit && _firstVisibleTab > 0;

            int lastVisible = GetLastVisibleTab();

            bool hiddenRight =
                !allFit &&
                lastVisible < _mapKeys.Count - 1;

            // "+ New Map" button width measured from its label — no hardcoding.
            int newMapBtnW = _ui.MeasureBtn(EditorUIStrings.NewMap);

            // Arrow buttons sit flush at each end of the tab row.
            // Tab content runs between them, separated by _ui.Pad.
            int tabsStartX = _mapArea.X + TabArrowW + _ui.Pad;
            int tabsEndX = _mapArea.Right - newMapBtnW - TabArrowW - _ui.Pad;

            int x = tabsStartX;

            _btnTabLeftRect = new Rectangle(_mapArea.X, tabY, TabArrowW, _ui.TabH);
            _btnTabRightRect = new Rectangle(_mapArea.Right - newMapBtnW - TabArrowW - _ui.Pad,
                                             tabY, TabArrowW, _ui.TabH);

            if (hiddenLeft) DrawButton(b, _btnTabLeftRect, EditorUIStrings.LeftArrow, Color.White, _ui);

            if (hiddenRight) DrawButton(b, _btnTabRightRect, EditorUIStrings.RightArrow, Color.White, _ui);


            float tabScale = _ui.NormalScale;

            for (int i = _firstVisibleTab; i <= lastVisible; i++)
            {
                string key = _mapKeys[i];

                bool active =
                    key == _currentMapKey;

                int regionCount =
                    _editMaps[key].Count;

                string label = $"  {key}  ({regionCount})";

                int tw =
                    GetTabWidth(key);

                Rectangle tabRect =
                    new Rectangle(
                        x,
                        tabY,
                        tw,
                        _ui.TabH);

                _tabRects.Add(tabRect);

                Color bgColor =
                    active
                    ? new Color(230, 210, 170)
                    : new Color(140, 120, 90);

                Color textColor =
                    active
                    ? Game1.textColor
                    : Color.White * 0.75f;

                int drawY =
                    active
                    ? tabY
                    : tabY + 4;

                int drawH =
                    active
                    ? _ui.TabH + 2
                    : _ui.TabH - 4;

                Rectangle drawRect =
                    new Rectangle(
                        x,
                        drawY,
                        tw,
                        drawH);

                IClickableMenu.drawTextureBox(
                    b,
                    Game1.menuTexture,
                    new Rectangle(0, 256, 60, 60),
                    drawRect.X,
                    drawRect.Y,
                    drawRect.Width,
                    drawRect.Height,
                    bgColor,
                    drawShadow: !active);

                Vector2 labelSize =
                    Game1.smallFont.MeasureString(label) * tabScale;

                Vector2 labelPos =
                    new Vector2(
                        x + (tw - labelSize.X) / 2f,
                        drawY + (_ui.TabH - labelSize.Y) / 2f);

                b.DrawString(
                    Game1.smallFont,
                    label,
                    labelPos,
                    textColor,
                    0f,
                    Vector2.Zero,
                    tabScale,
                    SpriteEffects.None,
                    1f);

                x += tw + 2;
            }

            // "+ New Map" sits flush at the right end; slight vertical inset
            // so inactive tabs visually connect to the map below.
            _btnNewMapRect = new Rectangle(
                _mapArea.Right - newMapBtnW,
                tabY + _ui.Pad / 2,
                newMapBtnW,
                _ui.TabH - _ui.Pad / 2);

            DrawButton(
                b,
                _btnNewMapRect,
                EditorUIStrings.NewMap,
                Color.White, _ui);
        }

        // ── Dropdown drawing ──────────────────────────────────────────────────

        private void DrawDropdownBox(SpriteBatch b, Rectangle r)
        {
            string preview = _sel >= 0 && _dropdownOpen == false ? EditorUIStrings.SelectLocation : EditorUIStrings.SelectLocation;

            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.5f);
            DrawBorder(b, r, _dropdownOpen ? Color.White : Color.Gray, 2);
            b.DrawString(Game1.smallFont, preview,
                new Vector2(r.X + 6, r.Y + (_ui.FieldH - _ui.LineH) / 2),
                Color.White * 0.7f, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);

            // Down-arrow indicator
            b.DrawString(Game1.smallFont, EditorUIStrings.DownDropdown,
                new Vector2(r.Right - _ui.Pad * 2, r.Y + (_ui.FieldH - _ui.LineH) / 2),
                Color.White, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
        }

        private void DrawDropdownList(SpriteBatch b)
        {
            int x = ActiveLayout.LocDropdown.X;
            int y = ActiveLayout.LocDropdown.Bottom;
            int w = ActiveLayout.LocDropdown.Width;
            int rowH = DropdownRowH;  // evaluated once — consistent per frame
            int visible = Math.Min(DropdownVisibleRows, _allLocations.Count - _dropdownScroll);
            int totalH = rowH * visible; // background exactly fits drawn rows

            // Background panel — exactly as tall as the rows we draw.
            b.Draw(Game1.fadeToBlackRect, new Rectangle(x, y, w, totalH), Color.Black * 0.9f);
            DrawBorder(b, new Rectangle(x, y, w, totalH), Color.White, 2);

            for (int i = 0; i < visible; i++)
            {
                int idx = i + _dropdownScroll;
                int rowY = y + i * rowH;
                var row = new Rectangle(x, rowY, w, rowH);
                bool hov = row.Contains(Game1.getMouseX(), Game1.getMouseY());

                if (hov)
                    b.Draw(Game1.fadeToBlackRect, row, Color.White * 0.15f);

                // Centre text vertically in the row.
                float textY = rowY + (rowH - _ui.LineH) / 2f;
                b.DrawString(Game1.smallFont, _allLocations[idx],
                    new Vector2(x + 6, textY),
                    Color.White, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
            }

            // Scroll hint drawn below the list, not inside it.
            if (_allLocations.Count > DropdownVisibleRows)
            {
                b.DrawString(Game1.smallFont,
                    $"{_dropdownScroll + 1}–{_dropdownScroll + visible} / {_allLocations.Count}",
                    new Vector2(x + 4, y + totalH + 2),
                    Color.White * 0.5f, 0f, Vector2.Zero, _ui.TinyScale, SpriteEffects.None, 1f);
            }
        }

        // ── New-map dialog ────────────────────────────────────────────────────

        private void DrawNewMapDialog(SpriteBatch b)
        {
            bool hasError = _newMapError.Length > 0;
            int dw = 360, dh = hasError ? 195 : 160;
            int dx = (Game1.uiViewport.Width - dw) / 2;
            int dy = (Game1.uiViewport.Height - dh) / 2;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                dx, dy, dw, dh, Color.White, drawShadow: true);

            b.DrawString(Game1.smallFont, EditorUIStrings.NewMapTitle,
                new Vector2(dx + 16, dy + 16), Game1.textColor,
                0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);

            var field = new Rectangle(dx + 16, dy + 52, dw - 32, 34);
            DrawTextField(b, field, _newMapName, true, _ui);

            // Error message (duplicate name)
            if (hasError)
            {
                b.DrawString(Game1.smallFont, _newMapError,
                    new Vector2(dx + 16, dy + 94),
                    new Color(220, 80, 80),
                    0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);
            }

            var ok = new Rectangle(dx + 16, dy + dh - 52, (dw - 48) / 2, 36);
            var cancel = new Rectangle(ok.Right + 16, dy + dh - 52, (dw - 48) / 2, 36);

            DrawButton(b, ok, EditorUIStrings.Create, new Color(140, 220, 140), _ui);
            DrawButton(b, cancel, EditorUIStrings.Cancel, new Color(220, 120, 120), _ui);
        }
        private void DrawDeleteMapDialog(SpriteBatch b)
        {
            int dw = 420;
            int dh = 180;

            int dx = (Game1.uiViewport.Width - dw) / 2;
            int dy = (Game1.uiViewport.Height - dh) / 2;

            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                dx,
                dy,
                dw,
                dh,
                Color.White,
                drawShadow: true);

            string msg = $"{EditorUIStrings.ConfirmDeleteMapInstruction} \"{_mapPendingDelete}\"?\n{EditorUIStrings.ConfirmDeleteMapWarning}";

            b.DrawString(
                Game1.smallFont,
                msg,
                new Vector2(dx + 16, dy + 16),
                Color.White,
                0f,
                Vector2.Zero,
                _ui.NormalScale,
                SpriteEffects.None,
                1f);

            Rectangle deleteBtn =
                new Rectangle(
                    dx + 16,
                    dy + dh - 52,
                    (dw - 48) / 2,
                    36);

            Rectangle cancelBtn =
                new Rectangle(
                    deleteBtn.Right + 16,
                    dy + dh - 52,
                    (dw - 48) / 2,
                    36);

            DrawButton(
                b,
                deleteBtn,
               EditorUIStrings.Delete,
                new Color(220, 100, 80), _ui);

            DrawButton(
                b,
                cancelBtn,
                EditorUIStrings.CancelSimple,
                new Color(140, 140, 140), _ui);
        }
        // ── Input ─────────────────────────────────────────────────────────────

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // ── Hover Mapping panel takes absolute priority while open ────────
            // It is a full editor tab, not a small dialog, so every click while
            // it's open is routed to its own handler and nothing below runs.
            if (_hoverPanelOpen)
            {
                ReceiveLeftClickHoverPanel(x, y);
                return;
            }
            UpdateSidebarLayoutState();

            // ── Delete confirmation ─────────────────────────────────
            if (_confirmDeleteMapOpen)
            {
                int dw = 420;
                int dh = 180;

                int dx = (Game1.uiViewport.Width - dw) / 2;
                int dy = (Game1.uiViewport.Height - dh) / 2;

                Rectangle deleteBtn =
                    new Rectangle(
                        dx + 16,
                        dy + dh - 52,
                        (dw - 48) / 2,
                        36);

                Rectangle cancelBtn =
                    new Rectangle(
                        deleteBtn.Right + 16,
                        dy + dh - 52,
                        (dw - 48) / 2,
                        36);

                if (deleteBtn.Contains(x, y))
                {
                    DeleteCurrentMap();
                }
                else if (cancelBtn.Contains(x, y))
                {
                    _confirmDeleteMapOpen = false;
                    _mapPendingDelete = "";
                }

                return;
            }
            // ── New-map dialog takes priority ─────────────────────────────────
            if (_newMapDialogOpen)
            {
                bool hasError = _newMapError.Length > 0;
                int dw = 360, dh = hasError ? 195 : 160;
                int dx = (Game1.uiViewport.Width - dw) / 2;
                int dy = (Game1.uiViewport.Height - dh) / 2;
                var ok = new Rectangle(dx + 16, dy + dh - 52, (dw - 48) / 2, 36);
                var cancel = new Rectangle(ok.Right + 16, dy + dh - 52, (dw - 48) / 2, 36);

                if (ok.Contains(x, y) && _newMapName.Trim().Length > 0)
                    ConfirmNewMap();
                else if (cancel.Contains(x, y))
                {
                    _newMapDialogOpen = false;
                    _newMapName = "";
                    _newMapError = "";
                }
                return;
            }

            // ── Dropdown intercepts clicks when open ──────────────────────────
            if (_dropdownOpen)
            {
                int baseY = ActiveLayout.LocDropdown.Bottom;
                int visible = Math.Min(DropdownVisibleRows, _allLocations.Count - _dropdownScroll);
                for (int i = 0; i < visible; i++)
                {
                    var row = new Rectangle(ActiveLayout.LocDropdown.X,
                                            baseY + i * DropdownRowH,
                                            ActiveLayout.LocDropdown.Width, DropdownRowH);
                    if (row.Contains(x, y))
                    {
                        AddLocationToSelected(_allLocations[i + _dropdownScroll]);
                        _dropdownOpen = false;
                        return;
                    }
                }
                _dropdownOpen = false;
                return;
            }
            if (_btnTabLeftRect.Contains(x, y))
            {
                if (_firstVisibleTab > 0)
                {
                    _firstVisibleTab--;
                    Game1.playSound("shwip");
                }

                return;
            }

            if (_btnTabRightRect.Contains(x, y))
            {
                if (GetLastVisibleTab() < _mapKeys.Count - 1)
                {
                    _firstVisibleTab++;
                    Game1.playSound("shwip");
                }

                return;
            }
            // ── Map tabs ──────────────────────────────────────────────────────
            int visibleIndex = 0;

            for (int mapIndex = _firstVisibleTab;
                 mapIndex <= GetLastVisibleTab();
                 mapIndex++)
            {
                if (visibleIndex >= _tabRects.Count)
                    break;

                if (_tabRects[visibleIndex].Contains(x, y))
                {
                    if (_mapKeys[mapIndex] != _currentMapKey)
                    {
                        CommitName();

                        _sel = -1;

                        _currentMapKey =
                            _mapKeys[mapIndex];

                        SeedCurrentMap();
                        EnsureSelectedTabVisible();

                        Game1.playSound("smallSelect");
                    }

                    return;
                }

                visibleIndex++;
            }
            if (_btnNewMapRect.Contains(x, y)) { _newMapDialogOpen = true; return; }

            // ── Sidebar buttons ───────────────────────────────────────────────
            if (ActiveLayout.AddRegionButton.Contains(x, y)) { AddRegion(); return; }
            if (ActiveLayout.DeleteRegionButton.Contains(x, y)) { DeleteSelected(); return; }
            if (ActiveLayout.ColorButton.Contains(x, y)) { CycleColor(); return; }
            if (ActiveLayout.TextColorButton.Contains(x, y))
            {
                CycleTextColor();
                return;
            }
            if (ActiveLayout.TextScaleSlider.Contains(x, y))
            {
                _draggingTextScale = true;
                UpdateTextScaleFromMouse(x);
                return;
            }
            if (ActiveLayout.SaveButton.Contains(x, y)) { DoSave(); return; }
            if (ActiveLayout.CancelButton.Contains(x, y)) { exitThisMenu(); return; }
            if (ActiveLayout.OpacitySlider.Contains(x, y))
            {
                _draggingOpacity = true;
                UpdateOpacityFromMouse(x);
                return;
            }
            if (ActiveLayout.EdgeGrabSlider.Contains(x, y))
            {
                _draggingEdgeGrab = true;
                UpdateEdgeGrabFromMouse(x);
                return;
            }
            // Bind button — bind/rebind this map tab to the current live map key,
            // removing any previous tab that was bound to the same live key (exclusive).
            if (ActiveLayout.BindButton.Contains(x, y))
            {
                string liveKey2 = MapKeyHelper.GetCurrentMapKey();
                // Remove the binding from whichever other tab currently owns this live key
                foreach (var key in _mapKeys)
                {
                    if (key != _currentMapKey &&
                        _bindings.TryGetValue(key, out var prev) && prev == liveKey2)
                    {
                        _bindings.Remove(key);
                        break;
                    }
                }
                _bindings[_currentMapKey] = liveKey2;
                Game1.playSound("coin");
                return;
            }

            // Delete Map button — remove the current map tab (minimum 1 tab always kept)
            if (ActiveLayout.DeleteMapButton.Contains(x, y) && _mapKeys.Count > 1)
            {
                _confirmDeleteMapOpen = true;
                _mapPendingDelete = _currentMapKey;

                Game1.playSound("smallSelect");
                return;
            }

            // Edit Map Hover Data Relationships — opens the manual mapping panel
            if (IsCurrentTabBoundToLiveMap && ActiveLayout.HoverMapButton.Contains(x, y))
            {
                OpenHoverMappingPanel();
                Game1.playSound("bigSelect");
                return;
            }

            // Name field
            if (ActiveLayout.NameField.Contains(x, y)) { _nameFocused = true; return; }

            // Dropdown box
            if (ActiveLayout.LocDropdown.Contains(x, y)) { _dropdownOpen = !_dropdownOpen; return; }

            // Add-loc button
            if (ActiveLayout.AddLocButton.Contains(x, y)) { _dropdownOpen = true; return; }

            // Remove-location buttons (scrolled list)
            if (_sel >= 0 && _sel < _currentRegions.Count)
            {
                var r = _currentRegions[_sel];
                int locListX = _sideArea.X + _ui.Pad;
                int locListY0 = ActiveLayout.LocDropdown.Bottom + 4;
                int locListW = _sideArea.Width - _ui.Pad * 2 - _locListScrollbarTrack.Width - 4;

                for (int i = 0; i < ActiveLayout.VisibleLocationRows; i++)
                {
                    int idx = i + _locListScroll;
                    if (idx >= r.Locations.Count) break;

                    int rowY = locListY0 + i * LocListRowH;
                    var locRect = new Rectangle(locListX, rowY, locListW - 34, LocListRowH - 2);
                    var removeRect = new Rectangle(locRect.Right + 4, rowY, 28, LocListRowH - 2);

                    if (removeRect.Contains(x, y))
                    {
                        r.Locations.RemoveAt(idx);
                        _locListScroll = Math.Clamp(_locListScroll, 0,
                            Math.Max(0, r.Locations.Count - ActiveLayout.VisibleLocationRows));
                        return;
                    }
                }

                // Scrollbar track click — jump scroll position
                var track = _locListScrollbarTrack;
                track.Y = locListY0;
                if (track.Contains(x, y) && r.Locations.Count > ActiveLayout.VisibleLocationRows)
                {
                    float frac = (float)(y - track.Y) / track.Height;
                    _locListScroll = (int)Math.Round(frac *
                        Math.Max(0, r.Locations.Count - ActiveLayout.VisibleLocationRows));
                    _locListScroll = Math.Clamp(_locListScroll, 0,
                        Math.Max(0, r.Locations.Count - ActiveLayout.VisibleLocationRows));
                    return;
                }
            }

            _nameFocused = false;

            // ── Map area ──────────────────────────────────────────────────────
            if (_mapPanel.Contains(x, y))
            {
                _draggingOpacity = false;
                int hit = HitTest(x, y);
                if (hit >= 0)
                {
                    CommitName();
                    SelectRegion(hit);
                    var sr = ToScreen(_currentRegions[hit]);
                    _handle = GetHandle(sr, x, y, _edgeGrab);
                    _dragging = true;
                    _dragAnchor = new Point(x, y);
                    _origL = _currentRegions[hit].Left;
                    _origT = _currentRegions[hit].Top;
                    _origR = _currentRegions[hit].Right;
                    _origB = _currentRegions[hit].Bottom;
                }
                else
                {
                    CommitName();
                    _sel = -1;
                }
            }
        }
        bool _lastLayoutBindingState;

        void UpdateSidebarLayoutState()
        {
            bool currentState = IsCurrentTabBoundToLiveMap;

            if (currentState == _lastLayoutBindingState) return;

            _lastLayoutBindingState = currentState;
        }
        public override void releaseLeftClick(int x, int y)
        {
            _draggingHoverLeftScroll = false;
            _draggingHoverRightScroll = false;
            _dragging = false;
            _draggingEdgeGrab = false;
            _draggingOpacity = false;
            _draggingTextScale = false;
            _handle = Handle.None;
        }

        public override void leftClickHeld(int x, int y)
        {
            if (_draggingHoverLeftScroll)
            {
                _hoverLeftScroll = ScrollOffsetFromTrackY(_hoverLeftScrollTrackRect, _hoverElementNames.Count, _hoverLeftVisibleRows, y);
                return;
            }
            if (_draggingHoverRightScroll)
            {
                _hoverRightScroll = ScrollOffsetFromTrackY(_hoverRightScrollTrackRect, _hoverFilteredLocations.Count, _hoverRightVisibleRows, y);
                return;
            }

            if (_draggingOpacity)
            {
                UpdateOpacityFromMouse(x);
                return;
            }

            if (_draggingEdgeGrab)
            {
                UpdateEdgeGrabFromMouse(x);
                return;
            }
            if (_draggingTextScale)
            {
                UpdateTextScaleFromMouse(x);
                return;
            }
            if (!_dragging || _sel < 0) return;

            var r = _currentRegions[_sel];
            float dx = (x - _dragAnchor.X) / (float)_mapArea.Width;
            float dy = (y - _dragAnchor.Y) / (float)_mapArea.Height;
            const float MinFrac = 0.02f; // minimum region size as a fraction of map dimensions

            switch (_handle)
            {
                case Handle.Move:
                    float w = _origR - _origL, h = _origB - _origT;
                    r.Left = Math.Clamp(_origL + dx, 0f, 1f - w);
                    r.Top = Math.Clamp(_origT + dy, 0f, 1f - h);
                    r.Right = r.Left + w;
                    r.Bottom = r.Top + h;
                    break;
                case Handle.Left:
                    r.Left = Math.Clamp(_origL + dx, 0f, r.Right - MinFrac); break;
                case Handle.Right:
                    r.Right = Math.Clamp(_origR + dx, r.Left + MinFrac, 1f); break;
                case Handle.Top:
                    r.Top = Math.Clamp(_origT + dy, 0f, r.Bottom - MinFrac); break;
                case Handle.Bottom:
                    r.Bottom = Math.Clamp(_origB + dy, r.Top + MinFrac, 1f); break;
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

            if (_hoverPanelOpen)
            {
                ReceiveScrollHoverPanel(mx, my, direction);
                return;
            }

            if (_dropdownOpen)
            {
                _dropdownScroll = Math.Clamp(
                    _dropdownScroll - Math.Sign(direction),
                    0,
                    Math.Max(0, _allLocations.Count - DropdownVisibleRows));
                return;
            }

            // Location list scroll — active when mouse is over the list area
            if (_sel >= 0 && _sel < _currentRegions.Count)
            {
                var r = _currentRegions[_sel];
                int locListY0 = ActiveLayout.LocDropdown.Bottom + 4;
                int locListH = LocListRowH * ActiveLayout.VisibleLocationRows;
                var listArea = new Rectangle(_sideArea.X + _ui.Pad, locListY0,
                    _sideArea.Width - _ui.Pad * 2, locListH);

                if (listArea.Contains(mx, my))
                {
                    _locListScroll = Math.Clamp(
                        _locListScroll - Math.Sign(direction),
                        0,
                        Math.Max(0, r.Locations.Count - ActiveLayout.VisibleLocationRows));
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (_hoverPanelOpen)
                {
                    if (_hoverNewMapDropdownOpen) { _hoverNewMapDropdownOpen = false; return; }
                    CloseHoverMappingPanel();
                    return;
                }
                if (_dropdownOpen) { _dropdownOpen = false; return; }
                if (_newMapDialogOpen) { _newMapDialogOpen = false; _newMapName = ""; _newMapError = ""; return; }
                if (_confirmDeleteMapOpen)
                {
                    _confirmDeleteMapOpen = false;
                    _mapPendingDelete = "";
                    return;
                }
                exitThisMenu();
                return;
            }

            if (key == Keys.Back)
            {
                if (_hoverPanelOpen)
                {
                    if (_hoverFilterFocused && _hoverFilterText.Length > 0)
                    {
                        _hoverFilterText = _hoverFilterText[..^1];
                        OnHoverFilterTextChanged();
                    }
                    return;
                }
                if (_newMapDialogOpen && _newMapName.Length > 0)
                {
                    _newMapName = _newMapName[..^1];
                    _newMapError = "";
                }
                else if (_nameFocused && _fieldName.Length > 0)
                    _fieldName = _fieldName[..^1];
                return;
            }

            if (_hoverPanelOpen) return; // panel handles its own input only

            if (key == Keys.Tab && _nameFocused)
            {
                CommitName();
                _nameFocused = false;
                return;
            }

            if (!_nameFocused && !_newMapDialogOpen)
                base.receiveKeyPress(key);
        }

        // ── Operations ────────────────────────────────────────────────────────

        // Error message shown when the player tries to create a duplicate map name.
        private string _newMapError = "";
        private void DeleteCurrentMap()
        {
            CommitName();

            string deletedMap = _mapPendingDelete;

            _editMaps.Remove(deletedMap);
            _bindings.Remove(deletedMap);
            _mapKeys.Remove(deletedMap);

            _confirmDeleteMapOpen = false;
            _mapPendingDelete = "";

            if (_mapKeys.Count > 0)
            {
                int nextIndex =
                    Math.Min(
                        _mapKeys.Count - 1,
                        _mapKeys.IndexOf(_currentMapKey));

                if (nextIndex < 0)
                    nextIndex = 0;

                _currentMapKey = _mapKeys[nextIndex];
            }

            _sel = -1;
            _fieldName = "";

            SeedCurrentMap();
            EnsureSelectedTabVisible();

            Game1.playSound("trashcan");
        }
        /// <summary>
        /// Seeds default hover mappings for the current map tab if none exist yet.
        /// Calls HoverMappingStore.SeedFromResolver which uses MapPointResolver's
        /// built index to generate defaults from the exact point.name values in
        /// mapPage.points. Safe to call multiple times — skips if already seeded.
        /// </summary>
        private void SeedCurrentMap()
        {
            if (_mapPage == null) return;
            HoverMappingStore.SeedFromResolver(_currentMapKey, _mapPage, MapTooltipDrawer.Tracker);
        }

        private void ConfirmNewMap()
        {
            string key = _newMapName.Trim();
            if (key.Length == 0) return;

            // Reject duplicate names — tell the player clearly.
            if (_editMaps.ContainsKey(key))
            {
                _newMapError = $"A map named \"{key}\" already exists.";
                return;
            }

            _newMapError = "";
            _editMaps[key] = new List<MapRegionData>();
            _mapKeys.Add(key);
            _mapKeys.Sort();

            _currentMapKey = key;
            _newMapDialogOpen = false;
            _newMapName = "";
            _sel = -1;

            /*
            int idx = _mapKeys.IndexOf(key);

            if (idx >= 0)
                _firstVisibleTab = idx;
            EnsureSelectedTabVisible();*/
        }

        #region Tabs
        private int GetTabWidth(string mapKey)
        {
            int regionCount = _editMaps[mapKey].Count;
            string label = $"  {mapKey}  ({regionCount})";
            int width =
                (int)Math.Ceiling(Game1.smallFont.MeasureString(label).X * _ui.NormalScale)
                + _ui.Pad * 2;

            return Math.Max(width, _ui.BtnH * 2);
        }
        private int GetLastVisibleTab()
        {
            int availableStart = _mapArea.X + TabArrowW + 8;

            int availableEnd =
                _mapArea.Right
                - newMapW       // reserve New Map button
                - TabArrowW     // reserve right arrow
                - 8;

            int availableWidth =
                Math.Max(0, availableEnd - availableStart);

            int usedWidth = 0;
            int last = _firstVisibleTab - 1;

            for (int i = _firstVisibleTab; i < _mapKeys.Count; i++)
            {
                int tabWidth = GetTabWidth(_mapKeys[i]);

                if (usedWidth + tabWidth > availableWidth)
                    break;

                usedWidth += tabWidth + 2;
                last = i;
            }

            return Math.Max(last, _firstVisibleTab);
        }
        private void ClampTabViewport()
        {
            // If every tab fits, always reset viewport.
            if (AllTabsFit())
            {
                _firstVisibleTab = 0;
            }
        }
        private bool AllTabsFit()
        {
            int availableWidth =
                GetTabViewportWidth();

            int requiredWidth = 0;

            foreach (string key in _mapKeys)
            {
                requiredWidth += GetTabWidth(key) + 2;
            }

            return requiredWidth <= availableWidth;
        }
        private int GetTabViewportWidth()
        {
            int left =
                _mapArea.X + TabArrowW + 6;

            int right =
                _btnNewMapRect.X - 6;

            return Math.Max(0, right - left);
        }
        private void EnsureSelectedTabVisible()
        {
            int selectedIndex =
         _mapKeys.IndexOf(_currentMapKey);

            if (selectedIndex < 0)
                return;

            if (selectedIndex < _firstVisibleTab)
            {
                _firstVisibleTab = selectedIndex;
                return;
            }

            while (selectedIndex > GetLastVisibleTab())
            {
                _firstVisibleTab++;

                if (_firstVisibleTab >= _mapKeys.Count)
                {
                    _firstVisibleTab =
                        Math.Max(0, _mapKeys.Count - 1);
                    break;
                }
            }
        }
        #endregion
        private void SelectRegion(int idx)
        {
            _sel = idx;
            _fieldName = _currentRegions[idx].Name;
            _locListScroll = 0;   // reset scroll when switching regions
        }

        private void CommitName()
        {
            if (_sel < 0 || _sel >= _currentRegions.Count) return;
            _currentRegions[_sel].Name = _fieldName.Trim();
        }

        private void AddLocationToSelected(string locationName)
        {
            if (_sel < 0 || _sel >= _currentRegions.Count) return;
            var locs = _currentRegions[_sel].Locations;
            if (!locs.Contains(locationName))
                locs.Add(locationName);
        }

        private void AddRegion()
        {
            CommitName();
            var region = new MapRegionData
            {
                Name = EditorUIStrings.NewAreaDefault,
                Locations = new(),
                Left = 0.35f,
                Top = 0.35f,
                Right = 0.65f,
                Bottom = 0.65f,
                ColorPacked = 0x8000FF88
            };

            region.TextColor =
                GetContrastColor(region.Color);

            _currentRegions.Add(region);
            SelectRegion(_currentRegions.Count - 1);
            _nameFocused = true;
        }

        private void DeleteSelected()
        {
            if (_sel < 0 || _sel >= _currentRegions.Count) return;
            _currentRegions.RemoveAt(_sel);
            _sel = -1; _fieldName = "";
        }

        private static Color GetContrastColor(Color bg)
        {
            float luminance =
                (0.299f * bg.R +
                 0.587f * bg.G +
                 0.114f * bg.B);

            return luminance > 128f
                ? Color.Black
                : Color.White;
        }

        #region Editor Config
        private void CycleTextColor()
        {
            if (_sel < 0)
                return;

            var r = _currentRegions[_sel];

            Color current = r.TextColor;

            int idx = Array.FindIndex(
                TextPalette,
                c => c.PackedValue == current.PackedValue);

            if (idx < 0)
                idx = 0;

            r.TextColor =
                TextPalette[(idx + 1) % TextPalette.Length];
        }
        private void CycleColor()
        {
            if (_sel < 0) return;
            var r = _currentRegions[_sel];
            Color current = r.Color;

            int idx = Array.FindIndex(Palette, c =>
                c.R == current.R &&
                c.G == current.G &&
                c.B == current.B);

            if (idx < 0)
                idx = 0;

            Color next = Palette[(idx + 1) % Palette.Length];

            next.A = current.A;   // keep transparency slider value

            r.Color = next;
        }
        void UpdateTextScaleFromMouse(int mouseX)
        {
            float pct = (mouseX - ActiveLayout.TextScaleSlider.X) / (float)ActiveLayout.TextScaleSlider.Width;
            pct = Math.Clamp(pct, 0f, 1f);

            _cfg.RegionLabelScale = MathHelper.Lerp(0.4f, 1.5f, pct);
        }
        void UpdateEdgeGrabFromMouse(int mouseX)
        {
            float pct = (mouseX - ActiveLayout.EdgeGrabSlider.X) / (float)ActiveLayout.EdgeGrabSlider.Width;
            pct = Math.Clamp(pct, 0f, 1f);

            _edgeGrab = (int)Math.Round(MathHelper.Lerp(2, 40, pct));
        }
        void UpdateOpacityFromMouse(int mouseX)
        {
            if (_sel < 0) return;

            float pct = (mouseX - ActiveLayout.OpacitySlider.X) / (float)ActiveLayout.OpacitySlider.Width;

            pct = Math.Clamp(pct, 0f, 1f);

            _currentRegions[_sel].Opacity = (byte)(pct * 255);
        }
        #endregion

        // ═════════════════════════════════════════════════════════════════════
        // ── Hover Mapping panel ─────────────────────────────────────────────
        // ═════════════════════════════════════════════════════════════════════
        //
        // Manual replacement for MapPointResolver's algorithmic guessing.
        // Two-sided list: hoverable map elements (left) ↔ internal locations
        // (right). Clicking a hoverable then a location toggles a mapping
        // between them. One hoverable can map to many locations. Mappings are
        // stored per-map in HoverMappingStore (hoverMappings.json), scoped by
        // _currentMapKey so switching the underlying editor's map tab also
        // switches which mapping set this panel edits.
        //
        // All rects below are computed from _ui in LayoutHoverPanel(), called
        // every time the panel opens — nothing here is a hardcoded pixel value.

        private bool _hoverPanelOpen = false;

        // Left list: hoverable element names for _currentMapKey (mapPage.points
        // ∪ mapAreas[].Data.WorldPositions, deduplicated). Built once per map
        // when the panel opens — these names don't change at runtime.
        private List<string> _hoverElementNames = new();
        private int _hoverLeftSel = -1;
        private int _hoverLeftScroll = 0;

        // Right list: same _allLocations source the main region editor uses.
        private int _hoverRightScroll = 0;

        // Live editing buffer for the currently selected hoverable's mapped
        // locations. Mirrors HoverMappingStore until the panel commits a
        // change, at which point it's written straight through (no separate
        // "apply" step — toggling a checkbox saves immediately into the
        // in-memory store; only the JSON file write is deferred to Save).
        private HashSet<string> _hoverSelectedLocations = new(StringComparer.OrdinalIgnoreCase);

        // Optional text filter for the (potentially very large) location list,
        // per the "must remain usable with many locations" requirement.
        private string _hoverFilterText = "";
        private bool _hoverFilterFocused = false;
        private List<string> _hoverFilteredLocations = new();

        // Unused placeholder kept for the Escape-key guard above — this panel
        // has no dropdown of its own today, but the flag keeps the input
        // priority chain future-proof if one is added later.
        private bool _hoverNewMapDropdownOpen = false;

        // Layout rects — all derived from _ui, recomputed on open/resize.
        private Rectangle _hoverPanelRect;
        private Rectangle _hoverInstructionsRect;     // hint text, now ABOVE both lists
        private Rectangle _hoverLeftListRect;         // left list content area (scrollbar excluded)
        private Rectangle _hoverRightListRect;        // right list content area (scrollbar excluded)
        private Rectangle _hoverLeftScrollTrackRect;  // left list's scrollbar track
        private Rectangle _hoverRightScrollTrackRect; // right list's scrollbar track
        private Rectangle _hoverFilterRect;
        private Rectangle _btnHoverCloseRect;
        private Rectangle _btnHoverResetRect;
        private Rectangle _btnHoverClearRect;
        private int _hoverRowH;

        // Visible-row counts are computed PER LIST from that list's own rect
        // height — the two lists are not guaranteed to be the same height
        // (the right column loses height to the filter field above it), so
        // sharing one count between them caused the right list to render rows
        // past its own bottom edge and made its scroll clamp wrong.
        private int _hoverLeftVisibleRows;
        private int _hoverRightVisibleRows;

        // Scrollbar thumb dragging — same _dragging-flag pattern the region
        // resize/move handles already use (see leftClickHeld/releaseLeftClick).
        private bool _draggingHoverLeftScroll = false;
        private bool _draggingHoverRightScroll = false;

        private void OpenHoverMappingPanel()
        {
            CommitName(); // flush any in-progress edit in the underlying editor first
            _hoverPanelOpen = true;
            _hoverFilterText = "";
            _hoverFilterFocused = false;
            RebuildHoverElementNames();
            LayoutHoverPanel();
            SelectHoverLeftIndex(_hoverElementNames.Count > 0 ? 0 : -1);
        }

        private void CloseHoverMappingPanel()
        {
            _hoverPanelOpen = false;
        }

        /// <summary>
        /// Builds the left-side hoverable list for the current map: the union
        /// of mapPage.points (via MapPageCompat) and mapAreas[].Data.WorldPositions
        /// point names, deduplicated case-insensitively. This is the same data
        /// MapPointResolver consumes, so anything resolvable today shows up
        /// here for manual mapping.
        /// </summary>
        private void RebuildHoverElementNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var list = new List<string>();

            void Add(string? name)
            {
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name)) list.Add(name);
            }

            if (_mapPage != null)
            {
                foreach (var point in MapPageCompat.GetPoints(_mapPage))
                {
                    // Keep the exact name used by HoverMappingStore.
                    Add(point?.name);
                }
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            _hoverElementNames = list;
            _hoverLeftScroll = 0;
        }
        public string FormatName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return input;

            // Remove underscore and everything after it
            int underscoreIndex = input.IndexOf('_');
            if (underscoreIndex >= 0)
                input = input[..underscoreIndex];

            int slashIndex = input.IndexOf('/');

            if (slashIndex < 0)
                return AddSpaces(input);

            string left = input[..slashIndex];
            string right = input[(slashIndex + 1)..];

            // Special case: */Default
            if (right.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                return $"{AddSpaces(left)} (Own Location)";
            }

            // Normal case
            return $"{AddSpaces(right)} ({AddSpaces(left)})";
        }

        private string AddSpaces(string text)
        {
            return Regex.Replace(
                text,
                @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])",
                " ");
        }
        private void SelectHoverLeftIndex(int index)
        {
            _hoverLeftSel = index;
            _hoverSelectedLocations.Clear();
            _hoverRightScroll = 0;

            if (index < 0 || index >= _hoverElementNames.Count)
                return;

            string hoverName = _hoverElementNames[index];

            HoverMappingStore.EnsureMap(_currentMapKey);

            var existing = HoverMappingStore.GetLocations(
                _currentMapKey, hoverName);

            _monitor.Log(
                $"[HoverPanel] Map='{_currentMapKey}', " +
                $"HoverName='{hoverName}', " +
                $"FoundMapping={existing != null}, " +
                $"Locations=[{string.Join(", ", existing ?? new List<string>())}]",
                LogLevel.Debug);

            if (existing != null)
            {
                foreach (var loc in existing)
                    _hoverSelectedLocations.Add(loc);
            }
            SortHoverFilteredLocations();

            _monitor.Log(
                $"[HoverPanel] Selected count={_hoverSelectedLocations.Count}; " +
                $"Right-list count={_hoverFilteredLocations.Count}; " +
                $"Matches={_hoverFilteredLocations.Count(loc => _hoverSelectedLocations.Contains(loc))}",
                LogLevel.Debug);
        }
        private void SortHoverFilteredLocations()
        {
            _hoverFilteredLocations.Sort((a, b) =>
            {
                bool aSelected = _hoverSelectedLocations.Contains(a);
                bool bSelected = _hoverSelectedLocations.Contains(b);

                // Selected locations first.
                if (aSelected != bSelected)
                    return aSelected ? -1 : 1;

                // Alphabetical within each group.
                return StringComparer.OrdinalIgnoreCase.Compare(a, b);
            });

            // Keep the scroll position valid after reordering.
            _hoverRightScroll = Math.Clamp(
                _hoverRightScroll,
                0,
                Math.Max(0, _hoverFilteredLocations.Count - _hoverRightVisibleRows));
        }
        /// <summary>
        /// Writes the current _hoverSelectedLocations buffer back into the
        /// in-memory mapping store for the selected hoverable. Called after
        /// every toggle so the right-hand checkmarks and the underlying data
        /// never drift apart; HoverMappingStore.Save() (on the main editor
        /// Save, or the panel's own Reset) is what actually persists to disk.
        /// </summary>
        private void CommitHoverSelection()
        {
            if (_hoverLeftSel < 0 || _hoverLeftSel >= _hoverElementNames.Count) return;
            string hoverName = _hoverElementNames[_hoverLeftSel];

            var set = HoverMappingStore.EnsureMap(_currentMapKey);
            var entry = set.Entries.FirstOrDefault(
                e => string.Equals(e.HoverName, hoverName, StringComparison.OrdinalIgnoreCase));

            if (_hoverSelectedLocations.Count == 0)
            {
                if (entry != null) set.Entries.Remove(entry);
                return;
            }

            if (entry == null)
            {
                entry = new HoverMappingEntry { HoverName = hoverName };
                set.Entries.Add(entry);
            }
            entry.Locations = _hoverSelectedLocations.ToList();
        }

        private void RebuildHoverFilteredLocations()
        {
            _hoverFilteredLocations = string.IsNullOrWhiteSpace(_hoverFilterText)
                ? _allLocations
                : _allLocations.Where(n => n.Contains(_hoverFilterText, StringComparison.OrdinalIgnoreCase)).ToList();
            // Scroll clamping against the correct per-list visible-row count
            // happens in LayoutHoverPanel right after this call (and again
            // wherever the filter text changes) — see _hoverRightVisibleRows.
        }

        /// <summary>
        /// Called whenever the user types or backspaces in the filter field.
        /// Rebuilds the filtered location list and re-clamps the right list's
        /// scroll position — typing a narrower filter can shrink the content
        /// below the current scroll offset, which would otherwise leave the
        /// list showing nothing until the user manually scrolled back up.
        /// </summary>
        private void OnHoverFilterTextChanged()
        {
            RebuildHoverFilteredLocations();
            _hoverRightScroll = Math.Clamp(_hoverRightScroll, 0, Math.Max(0, _hoverFilteredLocations.Count - _hoverRightVisibleRows));
        }

        /// <summary>
        /// Computes every rect for the panel from _ui — same single-source-of-
        /// truth pattern as LayoutSidebar(). Two equal-width columns split the
        /// panel area, each with its own scrollable list and scrollbar track.
        /// Recomputed whenever the panel opens so it always matches the
        /// current screen size and SDV UI scale — nothing here is a fixed
        /// pixel constant; every size is derived from _ui or from measuring
        /// the actual instruction text at the active scale.
        /// </summary>
        private void LayoutHoverPanel()
        {
            int margin = _ui.Pad * 3;
            _hoverPanelRect = new Rectangle(
                margin, margin,
                Game1.uiViewport.Width - margin * 2,
                Game1.uiViewport.Height - margin * 2);

            int pad = _ui.Pad;
            int titleH = (int)Math.Ceiling(Game1.dialogueFont.MeasureString(EditorHoverableUIStrings.EditHoverMapping).Y * _ui.TitleScale);
            int topY = _hoverPanelRect.Y + pad + titleH + pad;

            // Top-right action buttons (Reset / Close), measured to fit labels.
            int closeW = _ui.MeasureBtn(EditorHoverableUIStrings.Close);
            int resetW = _ui.MeasureBtn(EditorHoverableUIStrings.ResetToDefault);
            int clearW = _ui.MeasureBtn(EditorHoverableUIStrings.ClearMapping);

            _btnHoverCloseRect = new Rectangle(_hoverPanelRect.Right - pad - closeW, _hoverPanelRect.Y + pad, closeW, _ui.BtnH);
            _btnHoverResetRect = new Rectangle(_btnHoverCloseRect.X - pad - resetW, _hoverPanelRect.Y + pad, resetW, _ui.BtnH);
            _btnHoverClearRect = new Rectangle(_btnHoverResetRect.X - pad - clearW, _hoverPanelRect.Y + pad, clearW, _ui.BtnH);

            // Map key label sits right under the title row.
            int mapLabelH = (int)Math.Ceiling(Game1.smallFont.MeasureString("X").Y * _ui.SmallScale);
            int mapLabelY = topY;

            // ── Instructions now live ABOVE both lists, directly under the map
            // label. Height is measured from the actual wrapped text at the
            // panel's current width, so it grows/shrinks with font scale and
            // never clips or leaves dead space.
            int instructionsY = mapLabelY + mapLabelH + pad;
            string wrappedHint = Game1.parseText(EditorHoverableUIStrings.HoverMappingHint, Game1.smallFont, _hoverPanelRect.Width - pad * 2);
            int instructionsH = (int)Math.Ceiling(Game1.smallFont.MeasureString(wrappedHint).Y * _ui.SmallScale) + pad;
            _hoverInstructionsRect = new Rectangle(_hoverPanelRect.X + pad, instructionsY, _hoverPanelRect.Width - pad * 2, instructionsH);

            // Two columns, equal width, filling the remaining vertical space
            // down to the bottom margin. No footer strip anymore — the
            // instructions moved above, so the lists can use that space too.
            int colW = (_hoverPanelRect.Width - pad * 3) / 2;
            int listTop = _hoverInstructionsRect.Bottom + pad;
            int listBottom = _hoverPanelRect.Bottom - pad;
            int listH = Math.Max(_ui.RowH, listBottom - listTop);

            // Scrollbar track width: wide enough to be an easy drag/click
            // target at the current UI scale, derived from Pad rather than a
            // fixed pixel count.
            int trackW = Math.Max(8, _ui.Pad);

            int leftColX = _hoverPanelRect.X + pad;
            _hoverLeftScrollTrackRect = new Rectangle(leftColX + colW - trackW, listTop, trackW, listH);
            _hoverLeftListRect = new Rectangle(leftColX, listTop, colW - trackW - pad / 2, listH);

            // Right column reserves a filter field above its list, and a
            // track column on its right edge just like the left list.
            int rightX = leftColX + colW + pad;
            _hoverFilterRect = new Rectangle(rightX, listTop, colW, _ui.FieldH);
            int rightListTop = listTop + _ui.FieldH + pad;
            int rightListH = listH - _ui.FieldH - pad;
            _hoverRightScrollTrackRect = new Rectangle(rightX + colW - trackW, rightListTop, trackW, rightListH);
            _hoverRightListRect = new Rectangle(rightX, rightListTop, colW - trackW - pad / 2, rightListH);

            _hoverRowH = Math.Max(_ui.RowH, _ui.LineH + 4);

            // Each list's visible-row count comes from ITS OWN final height,
            // not a shared value — this is what keeps the right list (which is
            // shorter, having lost height to the filter field) from rendering
            // rows past its own bottom edge.
            _hoverLeftVisibleRows = Math.Max(1, _hoverLeftListRect.Height / _hoverRowH);
            _hoverRightVisibleRows = Math.Max(1, _hoverRightListRect.Height / _hoverRowH);

            RebuildHoverFilteredLocations();

            // Re-clamp both scroll offsets against the freshly computed
            // per-list visible-row counts (panel may have been resized, or
            // the filter may have changed the right list's content length).
            _hoverLeftScroll = Math.Clamp(_hoverLeftScroll, 0, Math.Max(0, _hoverElementNames.Count - _hoverLeftVisibleRows));
            _hoverRightScroll = Math.Clamp(_hoverRightScroll, 0, Math.Max(0, _hoverFilteredLocations.Count - _hoverRightVisibleRows));
        }

        private void DrawHoverMappingPanel(SpriteBatch b)
        {
            // Dim everything behind the panel — it fully replaces interaction
            // with the rest of the editor while open.
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height), Color.Black * 0.55f);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                _hoverPanelRect.X, _hoverPanelRect.Y, _hoverPanelRect.Width, _hoverPanelRect.Height,
                Color.White, drawShadow: true);

            b.DrawString(Game1.dialogueFont, EditorHoverableUIStrings.EditHoverMapping,
                new Vector2(_hoverPanelRect.X + _ui.Pad, _hoverPanelRect.Y + _ui.Pad),
                Game1.textColor, 0f, Vector2.Zero, _ui.TitleScale, SpriteEffects.None, 1f);

            DrawButton(b, _btnHoverClearRect, EditorHoverableUIStrings.ClearMapping, new Color(220, 140, 80), _ui);
            DrawButton(b, _btnHoverResetRect, EditorHoverableUIStrings.ResetToDefault, new Color(220, 160, 100), _ui);
            DrawButton(b, _btnHoverCloseRect, EditorHoverableUIStrings.Close, new Color(200, 200, 200), _ui);

            // Map key label, so it's unambiguous which map's mappings are showing.
            string mapLabel = $"{EditorUIStrings.Map}: {_currentMapKey}";
            b.DrawString(Game1.smallFont, mapLabel,
                new Vector2(_hoverPanelRect.X + _ui.Pad, _hoverInstructionsRect.Y - _ui.Pad),
                Game1.textColor * 0.8f, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

            // ── Instructions — ABOVE both lists, per the panel's UX requirements.
            // Wrapped to the panel width at layout time, so this always matches
            // however many lines parseText produced for the current text/scale.
            string wrappedHint = Game1.parseText(EditorHoverableUIStrings.HoverMappingHint, Game1.smallFont, _hoverInstructionsRect.Width);
            b.DrawString(Game1.smallFont, wrappedHint,
                new Vector2(_hoverInstructionsRect.X, _hoverInstructionsRect.Y),
                Game1.textColor * 0.75f, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);

            DrawHoverLeftList(b);
            DrawHoverRightList(b);
        }
        (string ChildName, string ParentText) GetHoverDisplayName(string hoverName)
        {
            int separator = hoverName.IndexOf('/');

            if (separator < 0) return (hoverName, "");

            string parentLocation = hoverName.Substring(0, separator);
            string childLocation = hoverName.Substring(separator + 1);

            int underscore = childLocation.IndexOf('_');
            if (underscore >= 0) childLocation = childLocation.Substring(0, underscore);

            // Separate words at lowercase-to-uppercase transitions.
            childLocation = Regex.Replace(childLocation, @"(?<=[a-z])(?=[A-Z])", " ");

            if (string.Equals(childLocation, "Default", StringComparison.OrdinalIgnoreCase)) return (parentLocation, "(Has its Own Area)");

            return (childLocation, $"({parentLocation})");
        }
        int GetMeaningfulMappingCount(string hoverName)
        {
            var locations = HoverMappingStore.GetLocations(_currentMapKey, hoverName);

            if (locations == null) return 0;

            return locations.Count(loc => !string.IsNullOrWhiteSpace(loc) && !string.Equals(loc, "Default", StringComparison.OrdinalIgnoreCase));
        }
        private void DrawHoverLeftList(SpriteBatch b)
        {
            var r = _hoverLeftListRect;
            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.35f);
            DrawBorder(b, r, Color.Gray, 2);

            int visible = Math.Min(_hoverLeftVisibleRows, _hoverElementNames.Count - _hoverLeftScroll);
            for (int i = 0; i < visible; i++)
            {
                int idx = i + _hoverLeftScroll;
                var row = new Rectangle(r.X, r.Y + i * _hoverRowH, r.Width, _hoverRowH);
                bool selected = idx == _hoverLeftSel;
                //bool hasMapping = HoverMappingStore.HasMapping(_currentMapKey, _hoverElementNames[idx]);

                var locations = HoverMappingStore.GetLocations(_currentMapKey, _hoverElementNames[idx]);
                bool hasMapping = locations != null && locations.Any(loc => !string.IsNullOrWhiteSpace(loc) && !string.Equals(loc, "Default", StringComparison.OrdinalIgnoreCase));

                if (selected)
                    b.Draw(Game1.fadeToBlackRect, row, Color.White * 0.25f);
                else if (row.Contains(Game1.getMouseX(), Game1.getMouseY()))
                    b.Draw(Game1.fadeToBlackRect, row, Color.White * 0.1f);

                // Small left-edge tick for hoverables that already have a
                // manual mapping, so users can see progress at a glance.
                if (hasMapping) b.Draw(Game1.fadeToBlackRect, new Rectangle(row.X, row.Y, 4, row.Height), new Color(140, 220, 140));

                var display = GetHoverDisplayName(_hoverElementNames[idx]);

                Vector2 textPosition = new Vector2(row.X + 8, row.Y + (_hoverRowH - _ui.LineH) / 2f);

                Color textColor = selected ? Color.Yellow : Color.White;

                // Scale the bold offset with the UI scale.
                float boldOffset = Math.Max(1f, _ui.NormalScale);

                b.DrawString(
                    Game1.smallFont,
                    display.ChildName,
                    textPosition,
                    textColor,
                    0f, Vector2.Zero,
                    _ui.NormalScale,
                    SpriteEffects.None, 1f
                );
                /*
                // A second pass creates a stronger bold effect.
                b.DrawString(
                    Game1.smallFont,
                    display.ChildName,
                    textPosition + new Vector2(boldOffset, 0f),
                    textColor,
                    0f, Vector2.Zero,
                    _ui.NormalScale,
                    SpriteEffects.None, 1f
                );
                */
                // Parent text starts after the actual rendered child width.
                float childWidth =
                    Game1.smallFont.MeasureString(display.ChildName).X * _ui.NormalScale
                    + boldOffset;

                Color descriptionColor = selected
                ? new Color(220, 205, 145) // Muted yellow when selected
                : new Color(175, 175, 175); // Subtle gray normally
                b.DrawString(
                    Game1.smallFont,
                    " " + display.ParentText,
                    textPosition + new Vector2(childWidth, 0f),
                    descriptionColor,
                    0f, Vector2.Zero,
                    _ui.NormalScale,
                    SpriteEffects.None, 1f
                );
                // Draws the amount of mappings it has.

                int mappingCount = GetMeaningfulMappingCount(_hoverElementNames[idx]);

                if (mappingCount > 0)
                {
                    string countText = mappingCount.ToString();

                    float countWidth = Game1.smallFont.MeasureString(countText).X * _ui.NormalScale;

                    float countX = row.Right - 10 - countWidth;
                    float countY = row.Y + (_hoverRowH - _ui.LineH) / 2f;

                    b.DrawString(Game1.smallFont, countText, new Vector2(countX, countY), descriptionColor, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
                }
            }

            if (_hoverElementNames.Count == 0)
            {
                b.DrawString(Game1.smallFont, EditorHoverableUIStrings.NoHoverElements,
                    new Vector2(r.X + 8, r.Y + 8),
                    Color.White * 0.5f, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);
            }

            DrawScrollbar(b, _hoverLeftScrollTrackRect, _hoverElementNames.Count, _hoverLeftVisibleRows, _hoverLeftScroll);
        }
        string GetLocationDisplayName(string locationName)
        {
            // Replace underscores with spaces.
            string result = locationName.Replace("_", " ");

            // Split lowercase-to-uppercase transitions.
            result = Regex.Replace(result, @"(?<=[a-z])(?=[A-Z])", " ");

            // Separate names from digit sequences, keeping digits together.
            result = Regex.Replace(result, @"(?<=[a-zA-Z])(?=\d)", " ");
            return result;
        }
        private void DrawHoverRightList(SpriteBatch b)
        {
            DrawTextField(b, _hoverFilterRect,
                _hoverFilterText.Length > 0 ? _hoverFilterText : EditorHoverableUIStrings.FilterLocations,
                _hoverFilterFocused, _ui);

            var r = _hoverRightListRect;
            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.35f);
            DrawBorder(b, r, _hoverLeftSel >= 0 ? Color.Gray : Color.Gray * 0.4f, 2);

            if (_hoverLeftSel < 0)
            {
                b.DrawString(Game1.smallFont, EditorHoverableUIStrings.SelectHoverableFirst,
                    new Vector2(r.X + 8, r.Y + 8),
                    Color.White * 0.5f, 0f, Vector2.Zero, _ui.SmallScale, SpriteEffects.None, 1f);
                return;
            }

            // All locations assigned anywhere in the left column.
            var allAssignedLocations = GetAllAssignedLocations();

            int visible = Math.Min(_hoverRightVisibleRows, _hoverFilteredLocations.Count - _hoverRightScroll);

            for (int i = 0; i < visible; i++)
            {
                int idx = i + _hoverRightScroll;
                string locName = _hoverFilteredLocations[idx];
                var row = new Rectangle(r.X, r.Y + i * _hoverRowH, r.Width, _hoverRowH);
                bool isMapped = _hoverSelectedLocations.Contains(locName);

                // Has this location ever been assigned to ANY hoverable?
                bool hasEverBeenAssigned = allAssignedLocations.Contains(locName);

                if (isMapped)
                {
                    // Green: assigned to the currently selected hoverable.
                    b.Draw(
                        Game1.fadeToBlackRect,
                        row,
                        new Color(140, 220, 140) * 0.25f
                    );
                }
                else if (!hasEverBeenAssigned)
                {
                    // Red: never assigned to any hoverable.
                    b.Draw(
                        Game1.fadeToBlackRect,
                        row,
                        new Color(220, 65, 65) * 0.30f
                    );
                }
                else if (row.Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    // Normal hover highlight.
                    b.Draw(
                        Game1.fadeToBlackRect,
                        row,
                        Color.White * 0.1f
                    );
                }

                // Checkbox glyph — reuses the same checkmark convention as
                // elsewhere in the editor (✓ for bound state).
                string box = isMapped ? "[x]" : "[ ]";
                b.DrawString(Game1.smallFont, box,
                    new Vector2(row.X + 6, row.Y + (_hoverRowH - _ui.LineH) / 2f),
                    isMapped ? new Color(140, 220, 140) : Color.White * 0.6f,
                    0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);

                float textX = row.X + 6 + Game1.smallFont.MeasureString("[x] ").X * _ui.NormalScale;

                string displayName = GetLocationDisplayName(locName);

                b.DrawString(Game1.smallFont, displayName,
                    new Vector2(textX, row.Y + (_hoverRowH - _ui.LineH) / 2f),
                    Color.White, 0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
            }

            DrawScrollbar(b, _hoverRightScrollTrackRect, _hoverFilteredLocations.Count, _hoverRightVisibleRows, _hoverRightScroll);
        }

        /// <summary>
        /// Draws a track + draggable thumb for a scrollable list. Thumb size
        /// and position are both derived entirely from content/viewport ratios
        /// — never a fixed pixel size — so they stay accurate for any content
        /// length and any track size the current UI scale produces:
        ///
        ///   thumbHeight = track.Height * (visibleRows / totalRows)
        ///   thumbY      = track.Y + track.Height * (scrollOffset / totalRows)
        ///
        /// When all content already fits (totalRows <= visibleRows) the track
        /// is drawn but no thumb is shown, since there's nothing to scroll —
        /// this is also the visual tell that a list isn't currently capped.
        /// </summary>
        private static void DrawScrollbar(SpriteBatch b, Rectangle track, int totalRows, int visibleRows, int scrollOffset)
        {
            b.Draw(Game1.fadeToBlackRect, track, Color.Black * 0.3f);
            DrawBorder(b, track, Color.Gray * 0.6f, 1);

            if (totalRows <= visibleRows || totalRows <= 0)
                return; // nothing to scroll — bare track communicates that

            float visibleFraction = Math.Clamp(visibleRows / (float)totalRows, 0f, 1f);
            int thumbH = Math.Max(track.Height / 12, (int)(track.Height * visibleFraction)); // floor so the thumb never shrinks to invisible

            int maxScroll = Math.Max(1, totalRows - visibleRows);
            float scrollFraction = Math.Clamp(scrollOffset / (float)maxScroll, 0f, 1f);
            int thumbY = track.Y + (int)((track.Height - thumbH) * scrollFraction);

            var thumb = new Rectangle(track.X + 1, thumbY, Math.Max(1, track.Width - 2), thumbH);
            b.Draw(Game1.fadeToBlackRect, thumb, Color.White * 0.7f);
        }

        /// <summary>
        /// Converts a mouse Y position over a scrollbar track into a scroll
        /// offset, scaled by the same content/viewport ratio DrawScrollbar
        /// uses to position the thumb — so clicking or dragging anywhere in
        /// the track maps to the matching point in the content, regardless of
        /// track height or content length.
        /// </summary>
        private static int ScrollOffsetFromTrackY(Rectangle track, int totalRows, int visibleRows, int mouseY)
        {
            int maxScroll = Math.Max(0, totalRows - visibleRows);
            if (maxScroll == 0 || track.Height <= 0) return 0;

            float fraction = Math.Clamp((mouseY - track.Y) / (float)track.Height, 0f, 1f);
            return (int)Math.Round(fraction * maxScroll);
        }
        void RefreshHoverSelection()
        {
            _hoverSelectedLocations.Clear();

            if (_hoverLeftSel < 0 || _hoverLeftSel >= _hoverElementNames.Count)
                return;

            string hoverName = _hoverElementNames[_hoverLeftSel];

            var locations = HoverMappingStore.GetLocations(
                _currentMapKey, hoverName);

            if (locations == null)
            {
                _monitor.Log(
                    $"[HoverPanel] No locations for '{hoverName}'",
                    LogLevel.Debug);
                return;
            }

            foreach (string location in locations)
                _hoverSelectedLocations.Add(location);

            _monitor.Log(
                $"[HoverPanel] Selected '{hoverName}': " +
                $"[{string.Join(", ", _hoverSelectedLocations)}]",
                LogLevel.Debug);

            _hoverRightScroll = Math.Clamp(
                _hoverRightScroll,
                0,
                Math.Max(0, _hoverFilteredLocations.Count - _hoverRightVisibleRows));
        }
        void ReceiveLeftClickHoverPanel(int x, int y)
        {
            if (_btnHoverCloseRect.Contains(x, y))
            {
                CloseHoverMappingPanel();
                return;
            }

            if (_btnHoverResetRect.Contains(x, y))
            {
                HoverMappingStore.ResetToDefault(_currentMapKey);
                HoverMappingStore.Save();

                // Reload the selected parent's children from restored data.
                RefreshHoverSelection();
                SortHoverFilteredLocations();
                // Game1.playSound("trashcan");
                Game1.playSound("smallSelect");
                return;
            }

            if (_btnHoverClearRect.Contains(x, y))
            {
                _hoverSelectedLocations.Clear();
                CommitHoverSelection();
                Game1.playSound("trashcan");
                return;
            }

            if (_hoverFilterRect.Contains(x, y)) { _hoverFilterFocused = true; return; }
            _hoverFilterFocused = false;

            // Scrollbar tracks — click jumps the thumb to that position and
            // begins a drag, exactly like the existing region-handle drags.
            if (_hoverLeftScrollTrackRect.Contains(x, y))
            {
                _hoverLeftScroll = ScrollOffsetFromTrackY(_hoverLeftScrollTrackRect, _hoverElementNames.Count, _hoverLeftVisibleRows, y);
                _draggingHoverLeftScroll = true;
                return;
            }
            if (_hoverRightScrollTrackRect.Contains(x, y))
            {
                _hoverRightScroll = ScrollOffsetFromTrackY(_hoverRightScrollTrackRect, _hoverFilteredLocations.Count, _hoverRightVisibleRows, y);
                _draggingHoverRightScroll = true;
                return;
            }

            // Left list — select a hoverable
            if (_hoverLeftListRect.Contains(x, y))
            {
                int row = (y - _hoverLeftListRect.Y) / _hoverRowH;
                int idx = row + _hoverLeftScroll;
                if (idx >= 0 && idx < _hoverElementNames.Count)
                {
                    //SelectHoverLeftIndex(idx);
                    _hoverLeftSel = idx;
                    _hoverRightScroll = 0;

                    RefreshHoverSelection();
                    SortHoverFilteredLocations();
                    Game1.playSound("smallSelect");
                }
                return;
            }

            // Right list — toggle a location mapping for the selected hoverable
            if (_hoverRightListRect.Contains(x, y) && _hoverLeftSel >= 0)
            {
                int row = (y - _hoverRightListRect.Y) / _hoverRowH;
                int idx = row + _hoverRightScroll;
                if (idx >= 0 && idx < _hoverFilteredLocations.Count)
                {
                    string locName = _hoverFilteredLocations[idx];
                    if (!_hoverSelectedLocations.Add(locName))
                        _hoverSelectedLocations.Remove(locName);

                    CommitHoverSelection();
                    SortHoverFilteredLocations();
                    Game1.playSound("smallSelect");
                }
                return;
            }
        }

        private void ReceiveScrollHoverPanel(int mx, int my, int direction)
        {
            if (_hoverLeftListRect.Contains(mx, my) || _hoverLeftScrollTrackRect.Contains(mx, my))
            {
                _hoverLeftScroll = Math.Clamp(
                    _hoverLeftScroll - Math.Sign(direction),
                    0, Math.Max(0, _hoverElementNames.Count - _hoverLeftVisibleRows));
                return;
            }

            if (_hoverRightListRect.Contains(mx, my) || _hoverRightScrollTrackRect.Contains(mx, my))
            {
                _hoverRightScroll = Math.Clamp(
                    _hoverRightScroll - Math.Sign(direction),
                    0, Math.Max(0, _hoverFilteredLocations.Count - _hoverRightVisibleRows));
                return;
            }
        }

        private void DoSave()
        {
            CommitName();
            // Replace RegionsByMap entirely from _editMaps so deleted tabs are removed.
            _cfg.RegionsByMap = _editMaps.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(CloneRegion).ToList());
            // Persist bindings
            _cfg.Bindings = new Dictionary<string, string>(_bindings);
            _onSave(_cfg);

            // Hover mappings are stored separately (hoverMappings.json) and are
            // edited live in-place via HoverMappingStore, so they only need an
            // explicit Save() call here, not inclusion in MapRegionConfig.
            HoverMappingStore.Save();

            exitThisMenu();
            Game1.addHUDMessage(new HUDMessage(EditorUIStrings.RegionsSaved, HUDMessage.newQuest_type));
        }

        // ── Hit testing ───────────────────────────────────────────────────────

        private int HitTest(int x, int y)
        {
            for (int i = _currentRegions.Count - 1; i >= 0; i--)
                if (ToScreen(_currentRegions[i]).Contains(x, y)) return i;
            return -1;
        }

        private static Handle GetHandle(Rectangle sr, int x, int y, int edge)
        {
            if (Math.Abs(x - sr.Left) <= edge) return Handle.Left;
            if (Math.Abs(x - sr.Right) <= edge) return Handle.Right;
            if (Math.Abs(y - sr.Top) <= edge) return Handle.Top;
            if (Math.Abs(y - sr.Bottom) <= edge) return Handle.Bottom;
            return Handle.Move;
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        private Rectangle ToScreen(MapRegionData r) => new(
            _mapArea.X + (int)(r.Left * _mapArea.Width),
            _mapArea.Y + (int)(r.Top * _mapArea.Height),
            Math.Max((int)((r.Right - r.Left) * _mapArea.Width), 2),
            Math.Max((int)((r.Bottom - r.Top) * _mapArea.Height), 2));

        // ── Drawing helpers ───────────────────────────────────────────────────

        private static void DrawBorder(SpriteBatch b, Rectangle r, Color c, int t)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, r.Width, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, t, r.Height), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
        }

        private static void DrawHandles(SpriteBatch b, Rectangle r)
        {
            const int S = 10;
            void Sq(int x, int y) => b.Draw(Game1.fadeToBlackRect,
                new Rectangle(x - S / 2, y - S / 2, S, S), Color.White);
            Sq(r.X + r.Width / 2, r.Y);
            Sq(r.X + r.Width / 2, r.Bottom);
            Sq(r.X, r.Y + r.Height / 2);
            Sq(r.Right, r.Y + r.Height / 2);
        }

        private static void DrawTextField(SpriteBatch b, Rectangle r, string text, bool active, UI ui)
        {
            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.45f);
            DrawBorder(b, r, active ? Color.White : Color.Gray, 2);

            string visible = text;
            while (Game1.smallFont.MeasureString(visible).X * ui.SmallScale > r.Width - 10
                   && visible.Length > 0)
                visible = visible[1..];

            b.DrawString(Game1.smallFont, visible + (active ? "|" : ""),
                new Vector2(r.X + 5, r.Y + 5),
                Color.White, 0f, Vector2.Zero, ui.NormalScale, SpriteEffects.None, 1f);
        }

        private static void DrawButton(SpriteBatch b, Rectangle r, string label, Color tint, UI ui)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                r.X, r.Y, r.Width, r.Height, tint, drawShadow: false);

            // Shrink text scale if the label is wider than the button interior.
            // This makes every button consistent: same height, text always fits,
            // same horizontal padding regardless of label length.
            float scale = ui.NormalScale;
            float maxTextW = r.Width - ui.BtnPadX * 2;
            float textW = Game1.smallFont.MeasureString(label).X * scale;
            if (textW > maxTextW && maxTextW > 0)
                scale *= maxTextW / textW;

            var sz = Game1.smallFont.MeasureString(label) * scale;
            var pos = new Vector2(r.X + (r.Width - sz.X) / 2f,
                                  r.Y + (r.Height - sz.Y) / 2f);
            b.DrawString(Game1.smallFont, label, pos, Game1.textColor,
                0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
        }

        /// <summary>
        /// Draws a two-line status banner anchored to the bottom of the sidebar area.
        /// Used for the "unbound" warning and the "wrong map" notice.
        /// </summary>
        private void DrawStatusBanner(SpriteBatch b, string text, Color textColor, Color bgColor)
        {
            var sz = Game1.smallFont.MeasureString(text) * _ui.NormalScale;
            float bx = _sideArea.X + _ui.Pad;
            float by = ActiveLayout.BindButton.Y - (int)sz.Y - _ui.Pad * 2;
            int bw = _sideArea.Width - _ui.Pad * 2;
            int bh = (int)sz.Y + _ui.Pad * 2;

            b.Draw(Game1.fadeToBlackRect,
                new Rectangle((int)bx - 4, (int)by - 4, bw + 8, bh + 8),
                bgColor);
            b.DrawString(Game1.smallFont, text,
                new Vector2(bx, by + _ui.Pad * 0.5f), textColor,
                0f, Vector2.Zero, _ui.NormalScale, SpriteEffects.None, 1f);
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static MapRegionData CloneRegion(MapRegionData r) => new()
        {
            Name = r.Name,
            Locations = new(r.Locations),
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom,
            ColorPacked = r.ColorPacked,
            TextColorPacked = r.TextColorPacked,
            Opacity = r.Opacity
        };

        /// <summary>
        /// Builds the location list from the live game world at runtime.
        /// Reads Game1.locations and all building interiors — no hardcoding,
        /// so Quarry, modded locations, and anything else appears automatically.
        /// Also merges any names already tracked by ForageTracker.
        /// </summary>
        static List<string> BuildKnownLocations(IMonitor monitor)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();

            void Add(string? name)
            {
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!))
                    list.Add(name!);
            }

            // Walk all loaded game locations and their building interiors.
            // This picks up Quarry, Mines, and any modded locations that are
            // present in Game1.locations at editor-open time.
            foreach (var loc in Game1.locations)
            {
                if (loc == null) continue;
                Add(loc.Name);
                foreach (var building in loc.buildings)
                {

                    var interior = building.indoors.Value;
                    if (interior != null)
                    {
                        Add(interior.Name);
                    }
                }
            } // ojo solo interires de farm esta haciendo

            // Also merge every name the ForageTracker has seen this session.
            // This covers locations that spawn forageables but aren't in
            // Game1.locations at editor-open time (e.g. dynamically loaded areas).
            if (MapTooltipDrawer.Tracker != null)
                foreach (var name in MapTooltipDrawer.Tracker.TrackedLocationNames)
                    Add(name);

            return list;
        }
    }
}