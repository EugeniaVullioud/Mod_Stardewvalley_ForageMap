using ForageTrackerModSV;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using static StardewValley.Menus.CoopMenu;

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

        private const int SidebarW  = 320;
        private const int Pad       = 12;
        private const int BtnH      = 40;
        private const int TabH      = 44;   // taller tabs so text is comfortable
        private const float MinFrac = 0.02f;
        private const int TabMapGap = 8; // space between tabs and map

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

        private static readonly Color[] TextPalette =
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
        private bool _draggingOpacity;

        // Tab navigation
        private Rectangle _btnTabLeftRect;
        private Rectangle _btnTabRightRect;

        // First tab currently visible in the viewport.
        private int _firstVisibleTab = 0;

        // Width reserved for navigation buttons
        private const int TabArrowW = 40;

        // All known SDV location names — shown in the dropdown.
        // Populated once in constructor from hardcoded list + runtime tracker data.
        private readonly List<string> _allLocations;

        // ── State ─────────────────────────────────────────────────────────────

        private readonly IModHelper              _helper;
        private readonly IMonitor                _monitor;
        private readonly MapRegionConfig         _cfg;
        private readonly Action<MapRegionConfig> _onSave;
        private int _edgeGrab;

        // All maps being edited — copied from cfg so changes are only committed on Save
        private readonly Dictionary<string, List<MapRegionData>> _editMaps;

        // Currently viewed map
        private string _currentMapKey;
        private List<MapRegionData> _currentRegions => _editMaps[_currentMapKey];
        private readonly List<string> _mapKeys;  // ordered list for ◀ ▶ navigation

        // Map display
        private readonly MapPage?  _mapPage;
        private readonly Rectangle _mapPanel;
        private Rectangle          _mapArea;   // actual image rect (from MapRenderUtility)

        // Sidebar
        private readonly Rectangle _sideArea;

        // Sidebar sub-rects
        private Rectangle _nameFieldRect;
        private Rectangle _locDropdownRect;
        private Rectangle _btnAddLocRect;
        private Rectangle _btnColorRect;
        private Rectangle _opacitySliderRect;
        private Rectangle _btnAddRegionRect;
        private Rectangle _btnDelRegionRect;
        private Rectangle _btnSaveRect;
        private Rectangle _btnCancelRect;
        private Rectangle _btnNewMapRect;
        private Rectangle _btnDelMapRect;          // "Delete this map tab" button
        private Rectangle _btnBindRect;            // "Bind to current map" button
        private readonly List<Rectangle> _tabRects = new();  // one per _mapKeys entry
        private Rectangle _btnTextColorRect;
        private Rectangle _textScaleSliderRect;
        private bool _draggingTextScale;
        // Selection & drag
        private int    _sel      = -1;
        private bool   _dragging = false;

        private Rectangle _edgeGrabSliderRect;
        private bool _draggingEdgeGrab;

        private enum Handle { None, Move, Left, Right, Top, Bottom }
        private Handle _handle   = Handle.None;
        private Point  _dragAnchor;
        private float  _origL, _origT, _origR, _origB;

        // Text input (name field only — locations use dropdown)
        private string _fieldName    = "";
        private bool   _nameFocused  = false;

        // Dropdown state
        private bool _dropdownOpen   = false;
        private int  _dropdownScroll = 0;
        private const int DropdownVisibleRows = 8;
        private const int DropdownRowH        = 28;

        // New-map dialog
        private bool   _newMapDialogOpen = false;
        private string _newMapName       = "";
        private const int newMapW = 130;

        // Map binding: mapKey → SDV map-key string (e.g. "Town", "Island").
        // A map tab that has no binding is shown with an ⚠ warning.
        // The binding controls which tab the tooltip uses for the live map.
        // Stored alongside regions in regions.json via MapRegionConfig.Bindings.
        private readonly Dictionary<string, string> _bindings;  // mapKey → bound SDV key

        // Location list scroll (replaces the old "+N more" cap)
        private int _locListScroll   = 0;
        private const int LocListRowH        = 28;
        private const int LocListVisibleRows = 6;   // rows shown before scrolling kicks in
        private Rectangle _locListScrollbarTrack;
        private bool      _locListScrollDragging = false;
        private int       _locListScrollDragStartY;
        private int       _locListScrollDragStartVal;

        // ── Constructor ───────────────────────────────────────────────────────

        public MapRegionEditor(
            IModHelper helper, IMonitor monitor,
            MapRegionConfig cfg, Action<MapRegionConfig> onSave)
            : base(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height)
        {
            _helper  = helper;
            _monitor = monitor;
            _cfg     = cfg;
            _onSave  = onSave;

            _edgeGrab = Math.Max(2, cfg.EdgeGrabPixels);

            // Deep-copy all maps so edits don't affect live data until Save
            _editMaps = cfg.RegionsByMap.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Select(CloneRegion).ToList());

            // Ensure at least one map exists
            if (_editMaps.Count == 0)
                _editMaps["Town"] = new List<MapRegionData>();


            // Load bindings (mapKey → SDV map key string)
            _bindings = cfg.Bindings != null
                ? new Dictionary<string, string>(cfg.Bindings)
                : new Dictionary<string, string>();

            _mapKeys = _editMaps.Keys.OrderBy(k => k).ToList();

            string? boundTab = FindTabBoundToCurrentMap();

            if (boundTab != null && _mapKeys.Contains(boundTab))
            {
                _currentMapKey = boundTab;
            }
            else
            {
                // Graceful fallback
                _currentMapKey = _mapKeys[0];
            }
            EnsureSelectedTabVisible();
            ClampTabViewport();
            // ── Build the sidebar and map panel ──────────────────────────────
            int sw = Game1.uiViewport.Width;
            int sh = Game1.uiViewport.Height;

            _sideArea = new Rectangle(sw - SidebarW - Pad, Pad, SidebarW, sh - Pad * 2);

            // ── Compute _mapArea by letterboxing the native map image size ────
            //
            // WHY NOT USE MapPage / GameMenu:
            //   In SDV 1.6, MapPage.draw() uses Game1.mapDisplayDevice which
            //   stretches the world map to fill the MapPage's full
            //   xPositionOnScreen/yPositionOnScreen/width/height bounds with NO
            //   letterboxing. Creating a throwaway GameMenu returns its MapPage
            //   bounds, which equal nearly the full viewport — so the letterbox
            //   calculation in the editor produced an _mapArea almost as large as
            //   the screen, leaving the map drawn over a huge background square.
            //
            // THE FIX:
            //   Letterbox the NATIVE MAP IMAGE DIMENSIONS into the available
            //   editor panel space ourselves. The native Stardew Valley world map
            //   image is 1360 × 720 pixels (this is the source rect SDV uses when
            //   rendering via mapDisplayDevice in 1.6). Letterboxing this into
            //   the panel gives us the correct proportional display rect.
            //
            //   The tooltip side (MapTooltipDrawer) uses the live MapPage bounds
            //   directly (they ARE the draw rect in 1.6 — no letterbox there).
            //   We reconcile these two coordinate systems by storing _mapArea and
            //   using it for ALL fraction ↔ pixel conversions in the editor, while
            //   the tooltip stores its rect in LastLiveMapRect. The two rects have
            //   the same ASPECT RATIO, so the fractions are equivalent even though
            //   the absolute pixel positions differ.
            //
            // Native map image size — matches the SDV 1.6 world map source.
            // If a modded map has a different size the regions will still work
            // correctly as long as the aspect ratio is similar; the player can
            // adjust region boundaries visually in the editor.
            const int NativeMapW = 1360;
            const int NativeMapH = 720;

            int availW = sw - SidebarW - Pad * 3;
            int availH = sh - Pad * 2 - TabH - TabMapGap;

            float scaleX = (float)availW / NativeMapW;
            float scaleY = (float)availH / NativeMapH;
            float scale  = Math.Min(scaleX, scaleY);

            int mapW = (int)(NativeMapW * scale);
            int mapH = (int)(NativeMapH * scale);
            // Centre the image in the available panel space
            int mapX = Pad + (availW - mapW) / 2;
            int mapY = Pad + TabH + TabMapGap + (availH - mapH) / 2;

            _mapPanel = new Rectangle(mapX, mapY, mapW, mapH);
            //_mapArea  = _mapPanel;  // they are identical — no wasted border space
            // MapPage is constructed to exactly match _mapArea so its content
            // lines up with the region overlays we draw on top.
            // In SDV 1.6 MapPage stretches its content to fill its bounds,
            // so giving it our letterboxed rect produces the correct display.
            _mapPage = new MapPage(mapX, mapY, mapW, mapH);
            _mapArea = MapRenderUtility.ComputeActualMapRect(_mapPage);


            // Publish our computed rect so MapTooltipDrawer can compare if needed.
            // The live tooltip uses the real GameMenu MapPage bounds (which are
            // the full draw area in 1.6), so LastLiveMapRect is NOT used for
            // fraction math in the editor — only _mapArea is.
            MapTooltipDrawer.LastLiveMapRect ??= new Rectangle(mapX, mapY, mapW, mapH);

            // Build location list: hardcoded known locations + runtime tracker data
            _allLocations = BuildKnownLocations();
            if (MapTooltipDrawer.Tracker != null)
                foreach (var name in MapTooltipDrawer.Tracker.TrackedLocationNames)
                    if (!_allLocations.Contains(name))
                        _allLocations.Add(name);
            _allLocations.Sort();

            LayoutSidebar();
        }
        private string? FindTabBoundToCurrentMap()
        {
            string liveMapKey = MapKeyHelper.GetCurrentMapKey();

            foreach (var pair in _bindings)
            {
                if (pair.Value == liveMapKey)
                    return pair.Key;
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

            if (_newMapDialogOpen)
            {
                _newMapName  += c;
                _newMapError  = ""; // clear error as soon as they type
            }
            else if (_nameFocused)
                _fieldName += c;
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private void RefreshMapArea()
        {
            // _mapArea is set once in the constructor from the live GameMenu bounds.
            // Tab switches don't change the map rect — nothing to do here.
            // Kept as a hook in case future SDV versions need per-tab recalculation.
        }

        private void LayoutSidebar()
        {
            int x = _sideArea.X + Pad;
            int w = _sideArea.Width - Pad * 2;
            int y = _sideArea.Y + Pad + 80; // title + map key label + hint

            // Name field
            _nameFieldRect = new Rectangle(x, y + 20, w, 34);
            y = _nameFieldRect.Bottom + Pad;

            // Location dropdown + add-location button
            int addW         = 56;
            _locDropdownRect = new Rectangle(x, y + 20, w - addW - Pad, 34);
            _btnAddLocRect   = new Rectangle(_locDropdownRect.Right + Pad, y + 20, addW, 34);
            y = _locDropdownRect.Bottom + Pad;

            // Scrollable location list — LocListVisibleRows rows tall + scrollbar strip
            int locListH          = LocListRowH * LocListVisibleRows;
            int scrollbarW        = 10;
            _locListScrollbarTrack = new Rectangle(
                _sideArea.Right - Pad - scrollbarW, y,
                scrollbarW, locListH);
            y += locListH + Pad;

            // Color
            // _btnColorRect = new Rectangle(x, y, w, BtnH);
            //y += BtnH + Pad * 2;

            // Opacity
            _btnColorRect = new Rectangle(x, y, w, BtnH);
            y += BtnH + Pad;

            _opacitySliderRect = new Rectangle(
           x,
           y,
           w,
           24);

            // Text rekated
            _btnTextColorRect = new Rectangle(x, y, w, BtnH);
            y += BtnH + Pad;
            y += 24 + Pad;

            _textScaleSliderRect = new Rectangle( x, y, w, 24);

            y += 24 + Pad;
           


            _edgeGrabSliderRect = new Rectangle(x, y, w, 24);
            y += 24 + Pad;

            // Coord info space
            y += 44;

            // Bind to current map + Delete this map — side by side
            int half2         = (w - Pad) / 2;
            _btnBindRect      = new Rectangle(x,               y, half2, BtnH);
            _btnDelMapRect    = new Rectangle(x + half2 + Pad, y, half2, BtnH);
            y += BtnH + Pad;

            // Add / Delete region
            int half          = (w - Pad) / 2;
            _btnAddRegionRect = new Rectangle(x,              y, half, BtnH);
            _btnDelRegionRect = new Rectangle(x + half + Pad, y, half, BtnH);
            y += BtnH + Pad * 2;

            // Save / Cancel
            _btnSaveRect   = new Rectangle(x, y, w, BtnH);
            y += BtnH + Pad;
            _btnCancelRect = new Rectangle(x, y, w, BtnH);
        }

        // ── Draw ──────────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            // Dim background
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * 0.6f);

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
                var  r     = _currentRegions[i];
                var  sr    = ToScreen(r);
                bool isSel = i == _sel;
                bool isHov = !isSel && sr.Contains(mx, my);

                float alpha = r.Opacity / 255f;

                b.Draw(Game1.fadeToBlackRect, sr, r.Color * (isHov ? alpha : alpha *0.85f));
                DrawBorder(b, sr, isSel ? Color.White : (isHov ? Color.Yellow : r.Color), isSel ? 3 : (isHov ? 2 : 1));

                if (sr.Width > 40)
                {
                    float scale = _cfg.RegionLabelScale;

                    Vector2 size =
                        Game1.smallFont.MeasureString(r.Name) * scale;

                    float maxWidth = sr.Width - 8;
                    float maxHeight = sr.Height - 8;

                    if (size.X > maxWidth)
                        scale *= maxWidth / size.X;

                    size = Game1.smallFont.MeasureString(r.Name) * scale;

                    if (size.Y > maxHeight)  scale *= maxHeight / size.Y;

                    b.DrawString( Game1.smallFont, r.Name, new Vector2(sr.X + 4, sr.Y + 4), r.TextColor, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                }

                if (isSel)
                {
                    DrawHandles(b, sr);

                    // Visualize grab zones
                    // Opacity-based visibility
                    byte alphaVis = (byte)Math.Min(255, _currentRegions[_sel].Opacity *1.15f); // +15%
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

            // ── Debug overlay — draws fraction grid and mouse position ────────
            // BEGIN DEBUG BLOCK — remove before shipping
            if (MapTooltipDrawer.DebugMode)
            {
                int mx2 = Game1.getMouseX(), my2 = Game1.getMouseY();
                float dbgRelX = (_mapArea.Width  > 0) ? (mx2 - _mapArea.X) / (float)_mapArea.Width  : 0f;
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

            // ── Sidebar ───────────────────────────────────────────────────────
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                _sideArea.X - 4, _sideArea.Y - 4,
                _sideArea.Width + 8, _sideArea.Height + 8,
                Color.White, drawShadow: false);

            int tx = _sideArea.X + Pad;
            int ty = _sideArea.Y + Pad;

            b.DrawString(Game1.dialogueFont, "Edit Regions",
                new Vector2(tx, ty), Game1.textColor,
                0f, Vector2.Zero, 0.65f, SpriteEffects.None, 1f);

            // Show which map tab is active and what it means
            ty += 30;
            b.DrawString(Game1.smallFont, $"Map: {_currentMapKey}",
                new Vector2(tx, ty), Game1.textColor,
                0f, Vector2.Zero, 0.7f, SpriteEffects.None, 1f);
            ty += 20;
            string mapHint = _currentMapKey switch
            {
                "Town"   => "Main Stardew Valley world map",
                "Island" => "Ginger Island map",
                _        => $"Custom / modded map: \"{_currentMapKey}\""
            };
            b.DrawString(Game1.smallFont, mapHint,
                new Vector2(tx, ty), Game1.textColor * 0.6f,
                0f, Vector2.Zero, 0.58f, SpriteEffects.None, 1f);

            if (_sel >= 0 && _sel < _currentRegions.Count)
            {
                var r = _currentRegions[_sel];

                // Name field
                b.DrawString(Game1.smallFont, "Area Name:",
                    new Vector2(tx, _nameFieldRect.Y - 18), Game1.textColor,
                    0f, Vector2.Zero, 0.65f, SpriteEffects.None, 1f);
                DrawTextField(b, _nameFieldRect, _fieldName, _nameFocused);

                // Location section header
                b.DrawString(Game1.smallFont, "SDV Locations:",
                    new Vector2(tx, _locDropdownRect.Y - 18), Game1.textColor,
                    0f, Vector2.Zero, 0.62f, SpriteEffects.None, 1f);

                // Dropdown
                DrawDropdownBox(b, _locDropdownRect);
                DrawButton(b, _btnAddLocRect, "+ Add", Color.White);

                // ── Scrollable location list ──────────────────────────────────
                int locListX  = tx;
                int locListY0 = _locDropdownRect.Bottom + 4;
                int locListW  = _sideArea.Width - Pad * 2 - _locListScrollbarTrack.Width - 4;
                int maxScroll = Math.Max(0, r.Locations.Count - LocListVisibleRows);
                _locListScroll = Math.Clamp(_locListScroll, 0, maxScroll);

                // Background for the list area
                var listAreaRect = new Rectangle(locListX, locListY0,
                    locListW + _locListScrollbarTrack.Width + 4,
                    LocListRowH * LocListVisibleRows);
                b.Draw(Game1.fadeToBlackRect, listAreaRect, Color.Black * 0.25f);

                // Rows
                for (int i = 0; i < LocListVisibleRows; i++)
                {
                    int idx = i + _locListScroll;
                    if (idx >= r.Locations.Count) break;

                    int rowY        = locListY0 + i * LocListRowH;
                    var locRect     = new Rectangle(locListX, rowY, locListW - 34, LocListRowH - 2);
                    var removeRect  = new Rectangle(locRect.Right + 4, rowY, 28, LocListRowH - 2);

                    b.Draw(Game1.fadeToBlackRect, locRect, Color.Black * 0.2f);
                    b.DrawString(Game1.smallFont, r.Locations[idx],
                        new Vector2(locRect.X + 4, locRect.Y + 5),
                        Color.White, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);

                    DrawButton(b, removeRect, "✕", new Color(220, 100, 100));
                }

                // Scrollbar
                if (r.Locations.Count > LocListVisibleRows)
                {
                    var track = _locListScrollbarTrack;
                    track.Y = locListY0;

                    b.Draw(Game1.fadeToBlackRect, track, Color.Black * 0.4f);

                    float thumbFrac   = (float)LocListVisibleRows / r.Locations.Count;
                    float thumbOffset = maxScroll > 0 ? (float)_locListScroll / maxScroll : 0f;
                    int   thumbH      = Math.Max(16, (int)(track.Height * thumbFrac));
                    int   thumbY      = track.Y + (int)((track.Height - thumbH) * thumbOffset);

                    b.Draw(Game1.fadeToBlackRect,
                        new Rectangle(track.X + 1, thumbY, track.Width - 2, thumbH),
                        Color.White * 0.7f);

                    // Count hint
                    b.DrawString(Game1.smallFont,
                        $"{_locListScroll + 1}–{Math.Min(_locListScroll + LocListVisibleRows, r.Locations.Count)}/{r.Locations.Count}",
                        new Vector2(locListX, locListY0 + LocListRowH * LocListVisibleRows + 2),
                        Color.White * 0.5f, 0f, Vector2.Zero, 0.5f, SpriteEffects.None, 1f);
                }

                // Color button
                DrawButton(b, _btnColorRect, "Cycle Color", Color.White);
                b.Draw(Game1.fadeToBlackRect,
                    new Rectangle(_btnColorRect.Right - BtnH + 4,
                                  _btnColorRect.Y + 4, BtnH - 8, BtnH - 8),  r.Color);

               
                // Opacity 
                b.DrawString( Game1.smallFont,
                    $"Opacity: {r.Opacity}",
                    new Vector2(_opacitySliderRect.X,
                                _opacitySliderRect.Y - 18),
                    Color.White,
                    0f,
                    Vector2.Zero,
                    0.6f,
                    SpriteEffects.None,
                    1f);

                b.Draw( Game1.fadeToBlackRect, _opacitySliderRect,  Color.Black * 0.6f); // Track
                DrawBorder( b, _opacitySliderRect, Color.White,  1);

                // Opacity Thumb
                float pct = r.Opacity / 255f;

                int thumbX =  _opacitySliderRect.X + (int)(_opacitySliderRect.Width * pct);

                b.Draw( Game1.fadeToBlackRect, new Rectangle( thumbX - 4, _opacitySliderRect.Y - 2, 8, _opacitySliderRect.Height + 4), Color.White);


                DrawButton(b, _btnTextColorRect, "Cycle Text Color", Color.White);

                b.DrawString( Game1.smallFont, $"Text Size: {_cfg.RegionLabelScale:F2}", new Vector2( _textScaleSliderRect.X, _textScaleSliderRect.Y - 18), Color.White,  0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);

                b.Draw(
                    Game1.fadeToBlackRect,
                    _textScaleSliderRect,
                    Color.Black * 0.6f);

                DrawBorder(
                    b,
                    _textScaleSliderRect,
                    Color.White,
                    1);

                float pctTxt = (_cfg.RegionLabelScale - 0.4f) / (1.5f - 0.4f);

                int thumbXText = _textScaleSliderRect.X + (int)(_textScaleSliderRect.Width * pctTxt);

                b.Draw( Game1.fadeToBlackRect, new Rectangle(thumbXText - 4, _textScaleSliderRect.Y - 2, 8, _textScaleSliderRect.Height + 4), Color.White);

                // Drag border
                b.DrawString(Game1.smallFont, $"Border Tolerance: {_edgeGrab}", new Vector2(_edgeGrabSliderRect.X, _edgeGrabSliderRect.Y - 18), Color.White, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);

                b.Draw(Game1.fadeToBlackRect,  _edgeGrabSliderRect, Color.Black * 0.6f);
                
                float pctBorder = (_edgeGrab - 2) / 38f;

                int thumbXBorder =  _edgeGrabSliderRect.X + (int)(_edgeGrabSliderRect.Width * pctBorder);

                int thumbWidth = _edgeGrab;
                thumbWidth = Math.Clamp(thumbWidth, 6, 30);

                b.Draw( Game1.fadeToBlackRect, new Rectangle(thumbXBorder - 4, _edgeGrabSliderRect.Y - 2, 8, _edgeGrabSliderRect.Height + 4),  Color.White);
               
            }
            else
            {
                b.DrawString(Game1.smallFont,
                    "Click a rectangle on\nthe map to select it,\nor press + Add Region.",
                    new Vector2(tx, _sideArea.Y + Pad + 54),
                    Game1.textColor * 0.7f, 0f, Vector2.Zero, 0.68f, SpriteEffects.None, 1f);
            }

            // ── Bind + Delete Map buttons ─────────────────────────────────────
            string liveKey = MapKeyHelper.GetCurrentMapKey();
            bool   isBound = _bindings.TryGetValue(_currentMapKey, out var boundTo)
                             && boundTo == liveKey;

            string bindLabel = isBound ? $"✓ {liveKey}" : $"⚑ Bind: {liveKey}";
            Color  bindColor = isBound ? new Color(140, 220, 140) : new Color(220, 200, 100);
            DrawButton(b, _btnBindRect, bindLabel, bindColor);

            bool canDelete = _mapKeys.Count > 1; // must always keep at least one tab
            DrawButton(b, _btnDelMapRect, "🗑 Del Map",
                canDelete ? new Color(220, 100, 80) : Color.Gray * 0.5f);

            // ── Binding status banners ────────────────────────────────────────
            // (1) Unbound warning — this map tab has no binding at all
            bool hasAnyBinding = _bindings.ContainsKey(_currentMapKey);
            if (!hasAnyBinding)
            {
                string warn = "⚠ This map tab is not bound to any in-game map.\n" +
                              "Press \"Bind to: ...\" above to link it.";
                DrawStatusBanner(b, warn, new Color(220, 160, 40), new Color(80, 50, 0, 200));
            }
            // (2) Wrong-map notice — has a binding but it's for a different live key
            else if (!isBound)
            {
                string notice = $"You are editing \"{_currentMapKey}\" " +
                                $"(bound to \"{boundTo}\").\n" +
                                $"Current in-game map: \"{liveKey}\".";
                DrawStatusBanner(b, notice, Color.Yellow, new Color(0, 0, 0, 200));
            }

            DrawButton(b, _btnAddRegionRect, "+ Add Region",   Color.White);
            DrawButton(b, _btnDelRegionRect, "✕ Delete",       _sel >= 0 ? Color.White : Color.Gray * 0.5f);
            DrawButton(b, _btnSaveRect,      "💾 Save",        new Color(140, 220, 140));
            DrawButton(b, _btnCancelRect,    "✕ Cancel",       new Color(220, 120, 120));

            // ── Dropdown overlay (drawn last so it's on top) ──────────────────
            if (_dropdownOpen)
                DrawDropdownList(b);

            // ── New map dialog ────────────────────────────────────────────────
            if (_newMapDialogOpen)
                DrawNewMapDialog(b);

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
                _mapArea.Y - TabH - TabMapGap;
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
            bool hiddenLeft = !allFit &&  _firstVisibleTab > 0;

            int lastVisible =  GetLastVisibleTab();

            bool hiddenRight =
                !allFit &&
                lastVisible < _mapKeys.Count - 1;

            const int newMapWw = 130;

            int tabsStartX = _mapArea.X + TabArrowW + 6;

            int tabsEndX =
                _mapArea.Right
                - newMapWw
                - TabArrowW
                - 6;

            int x = tabsStartX;

            _btnTabLeftRect = new Rectangle(
              _mapArea.X,
              tabY,
              TabArrowW,
              TabH);

            _btnTabRightRect = new Rectangle( _mapArea.Right - newMapWw - TabArrowW - 4,  tabY, TabArrowW,  TabH);

            if (hiddenLeft)
                DrawButton(b, _btnTabLeftRect, "<", Color.White);

            if (hiddenRight)
                DrawButton(b, _btnTabRightRect, ">", Color.White);



            const float scale = 0.68f;
            const int tabPadX = 18;

            for (int i = _firstVisibleTab; i <= lastVisible; i++)
            {
                string key = _mapKeys[i];

                bool active =
                    key == _currentMapKey;

                int regionCount =
                    _editMaps[key].Count;

                string label =
                    $"  {key}  ({regionCount})";

                int tw =
                    GetTabWidth(key);

                Rectangle tabRect =
                    new Rectangle(
                        x,
                        tabY,
                        tw,
                        TabH);

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
                    ? TabH + 2
                    : TabH - 4;

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
                    Game1.smallFont.MeasureString(label) * scale;

                Vector2 labelPos =
                    new Vector2(
                        x + (tw - labelSize.X) / 2f,
                        drawY + (TabH - labelSize.Y) / 2f);

                b.DrawString(
                    Game1.smallFont,
                    label,
                    labelPos,
                    textColor,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    1f);

                x += tw + 2;
            }

            _btnNewMapRect = new Rectangle(
                _mapArea.Right - newMapWw,
                tabY + 4,
                newMapWw,
                TabH - 4);

            DrawButton(
                b,
                _btnNewMapRect,
                "+ New Map",
                Color.White);
        }

        // ── Dropdown drawing ──────────────────────────────────────────────────

        private void DrawDropdownBox(SpriteBatch b, Rectangle r)
        {
            string preview = _sel >= 0 && _dropdownOpen == false
                ? "(select location...)"
                : "(select location...)";

            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.5f);
            DrawBorder(b, r, _dropdownOpen ? Color.White : Color.Gray, 2);
            b.DrawString(Game1.smallFont, preview,
                new Vector2(r.X + 6, r.Y + 8),
                Color.White * 0.7f, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);

            // Down-arrow indicator
            b.DrawString(Game1.smallFont, "▼",
                new Vector2(r.Right - 20, r.Y + 8),
                Color.White, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);
        }

        private void DrawDropdownList(SpriteBatch b)
        {
            int x = _locDropdownRect.X;
            int y = _locDropdownRect.Bottom;
            int w = _locDropdownRect.Width;
            int totalH = DropdownRowH * DropdownVisibleRows;

            // Background panel
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(x, y, w, totalH), Color.Black * 0.9f);
            DrawBorder(b, new Rectangle(x, y, w, totalH), Color.White, 2);

            int visible = Math.Min(DropdownVisibleRows, _allLocations.Count - _dropdownScroll);
            for (int i = 0; i < visible; i++)
            {
                int   idx  = i + _dropdownScroll;
                int   rowY = y + i * DropdownRowH;
                var   row  = new Rectangle(x, rowY, w, DropdownRowH);
                bool  hov  = row.Contains(Game1.getMouseX(), Game1.getMouseY());

                if (hov)
                    b.Draw(Game1.fadeToBlackRect, row, Color.White * 0.15f);

                b.DrawString(Game1.smallFont, _allLocations[idx],
                    new Vector2(x + 6, rowY + 6),
                    Color.White, 0f, Vector2.Zero, 0.62f, SpriteEffects.None, 1f);
            }

            // Scroll hint
            if (_allLocations.Count > DropdownVisibleRows)
            {
                b.DrawString(Game1.smallFont,
                    $"{_dropdownScroll + 1}–{_dropdownScroll + visible} / {_allLocations.Count}",
                    new Vector2(x + 4, y + totalH - 18),
                    Color.White * 0.5f, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 1f);
            }
        }

        // ── New-map dialog ────────────────────────────────────────────────────

        private void DrawNewMapDialog(SpriteBatch b)
        {
            bool hasError = _newMapError.Length > 0;
            int dw = 360, dh = hasError ? 195 : 160;
            int dx = (Game1.uiViewport.Width  - dw) / 2;
            int dy = (Game1.uiViewport.Height - dh) / 2;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                dx, dy, dw, dh, Color.White, drawShadow: true);

            b.DrawString(Game1.smallFont, "New map key name:",
                new Vector2(dx + 16, dy + 16), Game1.textColor,
                0f, Vector2.Zero, 0.7f, SpriteEffects.None, 1f);

            var field = new Rectangle(dx + 16, dy + 52, dw - 32, 34);
            DrawTextField(b, field, _newMapName, true);

            // Error message (duplicate name)
            if (hasError)
            {
                b.DrawString(Game1.smallFont, _newMapError,
                    new Vector2(dx + 16, dy + 94),
                    new Color(220, 80, 80),
                    0f, Vector2.Zero, 0.62f, SpriteEffects.None, 1f);
            }

            var ok     = new Rectangle(dx + 16,       dy + dh - 52, (dw - 48) / 2, 36);
            var cancel = new Rectangle(ok.Right + 16,  dy + dh - 52, (dw - 48) / 2, 36);

            DrawButton(b, ok,     "✓ Create", new Color(140, 220, 140));
            DrawButton(b, cancel, "✕ Cancel", new Color(220, 120, 120));
        }

        // ── Input ─────────────────────────────────────────────────────────────

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // ── New-map dialog takes priority ─────────────────────────────────
            if (_newMapDialogOpen)
            {
                bool hasError = _newMapError.Length > 0;
                int dw = 360, dh = hasError ? 195 : 160;
                int dx = (Game1.uiViewport.Width  - dw) / 2;
                int dy = (Game1.uiViewport.Height - dh) / 2;
                var ok     = new Rectangle(dx + 16,       dy + dh - 52, (dw - 48) / 2, 36);
                var cancel = new Rectangle(ok.Right + 16,  dy + dh - 52, (dw - 48) / 2, 36);

                if (ok.Contains(x, y) && _newMapName.Trim().Length > 0)
                    ConfirmNewMap();
                else if (cancel.Contains(x, y))
                {
                    _newMapDialogOpen = false;
                    _newMapName       = "";
                    _newMapError      = "";
                }
                return;
            }

            // ── Dropdown intercepts clicks when open ──────────────────────────
            if (_dropdownOpen)
            {
                int baseY = _locDropdownRect.Bottom;
                int visible = Math.Min(DropdownVisibleRows, _allLocations.Count - _dropdownScroll);
                for (int i = 0; i < visible; i++)
                {
                    var row = new Rectangle(_locDropdownRect.X,
                                            baseY + i * DropdownRowH,
                                            _locDropdownRect.Width, DropdownRowH);
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

                        EnsureSelectedTabVisible();

                        Game1.playSound("smallSelect");
                    }

                    return;
                }

                visibleIndex++;
            }
            if (_btnNewMapRect.Contains(x, y)) { _newMapDialogOpen = true; return; }

            // ── Sidebar buttons ───────────────────────────────────────────────
            if (_btnAddRegionRect.Contains(x, y)) { AddRegion();       return; }
            if (_btnDelRegionRect.Contains(x, y)) { DeleteSelected();  return; }
            if (_btnColorRect.Contains(x, y))     { CycleColor();      return; }
            if (_btnTextColorRect.Contains(x, y))
            {
                CycleTextColor();
                return;
            }
            if (_textScaleSliderRect.Contains(x, y))
            {
                _draggingTextScale = true;
                UpdateTextScaleFromMouse(x);
                return;
            }
            if (_btnSaveRect.Contains(x, y))      { DoSave();          return; }
            if (_btnCancelRect.Contains(x, y))    { exitThisMenu();    return; }
            if (_opacitySliderRect.Contains(x, y))
            {
                _draggingOpacity = true;
                UpdateOpacityFromMouse(x);
                return;
            }
            if (_edgeGrabSliderRect.Contains(x, y))
            {
                _draggingEdgeGrab = true;
                UpdateEdgeGrabFromMouse(x);
                return;
            }
            // Bind button — bind/rebind this map tab to the current live map key,
            // removing any previous tab that was bound to the same live key (exclusive).
            if (_btnBindRect.Contains(x, y))
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
            if (_btnDelMapRect.Contains(x, y) && _mapKeys.Count > 1)
            {
                CommitName();
                _editMaps.Remove(_currentMapKey);
                _bindings.Remove(_currentMapKey);
                _mapKeys.Remove(_currentMapKey);
                _mapKeys.Sort();
                _currentMapKey = _mapKeys[0];
                _sel           = -1;
                _fieldName     = "";
                Game1.playSound("trashcan");
                EnsureSelectedTabVisible();
                return;
            }

            // Name field
            if (_nameFieldRect.Contains(x, y)) { _nameFocused = true; return; }

            // Dropdown box
            if (_locDropdownRect.Contains(x, y)) { _dropdownOpen = !_dropdownOpen; return; }

            // Add-loc button
            if (_btnAddLocRect.Contains(x, y)) { _dropdownOpen = true; return; }

            // Remove-location buttons (scrolled list)
            if (_sel >= 0 && _sel < _currentRegions.Count)
            {
                var r     = _currentRegions[_sel];
                int locListX  = _sideArea.X + Pad;
                int locListY0 = _locDropdownRect.Bottom + 4;
                int locListW  = _sideArea.Width - Pad * 2 - _locListScrollbarTrack.Width - 4;

                for (int i = 0; i < LocListVisibleRows; i++)
                {
                    int idx = i + _locListScroll;
                    if (idx >= r.Locations.Count) break;

                    int rowY       = locListY0 + i * LocListRowH;
                    var locRect    = new Rectangle(locListX, rowY, locListW - 34, LocListRowH - 2);
                    var removeRect = new Rectangle(locRect.Right + 4, rowY, 28, LocListRowH - 2);

                    if (removeRect.Contains(x, y))
                    {
                        r.Locations.RemoveAt(idx);
                        _locListScroll = Math.Clamp(_locListScroll, 0,
                            Math.Max(0, r.Locations.Count - LocListVisibleRows));
                        return;
                    }
                }

                // Scrollbar track click — jump scroll position
                var track = _locListScrollbarTrack;
                track.Y = locListY0;
                if (track.Contains(x, y) && r.Locations.Count > LocListVisibleRows)
                {
                    float frac = (float)(y - track.Y) / track.Height;
                    _locListScroll = (int)Math.Round(frac *
                        Math.Max(0, r.Locations.Count - LocListVisibleRows));
                    _locListScroll = Math.Clamp(_locListScroll, 0,
                        Math.Max(0, r.Locations.Count - LocListVisibleRows));
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
                    var sr      = ToScreen(_currentRegions[hit]);
                    _handle     = GetHandle(sr, x, y, _edgeGrab);
                    _dragging   = true;
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

        public override void releaseLeftClick(int x, int y)
        {
            _dragging = false;
            _draggingEdgeGrab = false;
            _draggingOpacity = false;
            _draggingTextScale = false;
            _handle = Handle.None;
        }

        public override void leftClickHeld(int x, int y)
        {
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

            var r    = _currentRegions[_sel];
            float dx = (x - _dragAnchor.X) / (float)_mapArea.Width;
            float dy = (y - _dragAnchor.Y) / (float)_mapArea.Height;

            switch (_handle)
            {
                case Handle.Move:
                    float w = _origR - _origL, h = _origB - _origT;
                    r.Left   = Math.Clamp(_origL + dx, 0f, 1f - w);
                    r.Top    = Math.Clamp(_origT + dy, 0f, 1f - h);
                    r.Right  = r.Left + w;
                    r.Bottom = r.Top  + h;
                    break;
                case Handle.Left:
                    r.Left   = Math.Clamp(_origL + dx, 0f, r.Right  - MinFrac); break;
                case Handle.Right:
                    r.Right  = Math.Clamp(_origR + dx, r.Left + MinFrac, 1f);   break;
                case Handle.Top:
                    r.Top    = Math.Clamp(_origT + dy, 0f, r.Bottom - MinFrac); break;
                case Handle.Bottom:
                    r.Bottom = Math.Clamp(_origB + dy, r.Top + MinFrac, 1f);    break;
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

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
                var r         = _currentRegions[_sel];
                int locListY0 = _locDropdownRect.Bottom + 4;
                int locListH  = LocListRowH * LocListVisibleRows;
                var listArea  = new Rectangle(_sideArea.X + Pad, locListY0,
                    _sideArea.Width - Pad * 2, locListH);

                if (listArea.Contains(mx, my))
                {
                    _locListScroll = Math.Clamp(
                        _locListScroll - Math.Sign(direction),
                        0,
                        Math.Max(0, r.Locations.Count - LocListVisibleRows));
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (_dropdownOpen)      { _dropdownOpen = false; return; }
                if (_newMapDialogOpen)  { _newMapDialogOpen = false; _newMapName = ""; _newMapError = ""; return; }
                exitThisMenu();
                return;
            }

            if (key == Keys.Back)
            {
                if (_newMapDialogOpen && _newMapName.Length > 0)
                {
                    _newMapName  = _newMapName[..^1];
                    _newMapError = "";
                }
                else if (_nameFocused && _fieldName.Length > 0)
                    _fieldName = _fieldName[..^1];
                return;
            }

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

            _newMapError      = "";
            _editMaps[key]    = new List<MapRegionData>();
            _mapKeys.Add(key);
            _mapKeys.Sort();

            _currentMapKey    = key;
            _newMapDialogOpen = false;
            _newMapName       = "";
            _sel              = -1;

            /*
            int idx = _mapKeys.IndexOf(key);

            if (idx >= 0)
                _firstVisibleTab = idx;
            EnsureSelectedTabVisible();*/
        }
        private int GetTabWidth(string mapKey)
        {
            const float scale = 0.68f;
            const int tabPadX = 18;

            int regionCount = _editMaps[mapKey].Count;

            string label =
                $"  {mapKey}  ({regionCount})";

            int width =
                (int)(Game1.smallFont.MeasureString(label).X * scale)
                + tabPadX * 2;

            return Math.Max(width, 80);
        }
        private int GetLastVisibleTab()
        {
            int availableStart =  _mapArea.X + TabArrowW + 8;

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
        private void SelectRegion(int idx)
        {
            _sel           = idx;
            _fieldName     = _currentRegions[idx].Name;
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
                Name = "New Area",
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
        private void AutoContrastTextColor()
        {
            if (_sel < 0)
                return;

            var r = _currentRegions[_sel];

            r.TextColor =
                GetContrastColor(r.Color);
        }
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
            var r   = _currentRegions[_sel];
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
        private void UpdateTextScaleFromMouse(int mouseX)
        {
            float pct =
                (mouseX - _textScaleSliderRect.X)
                / (float)_textScaleSliderRect.Width;

            pct = Math.Clamp(pct, 0f, 1f);

            _cfg.RegionLabelScale =
                MathHelper.Lerp(
                    0.4f,
                    1.5f,
                    pct);
        }
        private void UpdateEdgeGrabFromMouse(int mouseX)
        {
            float pct = (mouseX - _edgeGrabSliderRect.X) / (float)_edgeGrabSliderRect.Width;

            pct = Math.Clamp(pct, 0f, 1f);

            _edgeGrab = (int)Math.Round( MathHelper.Lerp(2, 40, pct));
        }
        private void UpdateOpacityFromMouse(int mouseX)
        {
            if (_sel < 0)
                return;

            float pct =
                (mouseX - _opacitySliderRect.X)
                / (float)_opacitySliderRect.Width;

            pct = Math.Clamp(pct, 0f, 1f);

            _currentRegions[_sel].Opacity = (byte)(pct * 255);
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
            exitThisMenu();
            Game1.addHUDMessage(new HUDMessage(
                "Forage Tracker: regions saved.", HUDMessage.newQuest_type));
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
            _mapArea.X + (int)(r.Left  * _mapArea.Width),
            _mapArea.Y + (int)(r.Top   * _mapArea.Height),
            Math.Max((int)((r.Right  - r.Left) * _mapArea.Width),  2),
            Math.Max((int)((r.Bottom - r.Top)  * _mapArea.Height), 2));

        // ── Drawing helpers ───────────────────────────────────────────────────

        private static void DrawBorder(SpriteBatch b, Rectangle r, Color c, int t)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,          r.Width,  t),        c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Bottom - t, r.Width,  t),        c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X,          r.Y,          t,        r.Height), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right  - t, r.Y,          t,        r.Height), c);
        }

        private static void DrawHandles(SpriteBatch b, Rectangle r)
        {
            const int S = 10;
            void Sq(int x, int y) => b.Draw(Game1.fadeToBlackRect,
                new Rectangle(x - S / 2, y - S / 2, S, S), Color.White);
            Sq(r.X + r.Width  / 2, r.Y);
            Sq(r.X + r.Width  / 2, r.Bottom);
            Sq(r.X,                r.Y + r.Height / 2);
            Sq(r.Right,            r.Y + r.Height / 2);
        }

        private static void DrawTextField(SpriteBatch b, Rectangle r, string text, bool active)
        {
            b.Draw(Game1.fadeToBlackRect, r, Color.Black * 0.45f);
            DrawBorder(b, r, active ? Color.White : Color.Gray, 2);

            string visible = text;
            while (Game1.smallFont.MeasureString(visible).X * 0.62f > r.Width - 10
                   && visible.Length > 0)
                visible = visible[1..];

            b.DrawString(Game1.smallFont, visible + (active ? "|" : ""),
                new Vector2(r.X + 5, r.Y + 5),
                Color.White, 0f, Vector2.Zero, 0.62f, SpriteEffects.None, 1f);
        }

        private static void DrawButton(SpriteBatch b, Rectangle r, string label, Color tint)
        {
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                r.X, r.Y, r.Width, r.Height, tint, drawShadow: false);
            var sz  = Game1.smallFont.MeasureString(label) * 0.68f;
            var pos = new Vector2(r.X + (r.Width - sz.X) / 2f, r.Y + (r.Height - sz.Y) / 2f);
            b.DrawString(Game1.smallFont, label, pos, Game1.textColor,
                0f, Vector2.Zero, 0.68f, SpriteEffects.None, 1f);
        }

        /// <summary>
        /// Draws a two-line status banner anchored to the bottom of the sidebar area.
        /// Used for the "unbound" warning and the "wrong map" notice.
        /// </summary>
        private void DrawStatusBanner(SpriteBatch b, string text, Color textColor, Color bgColor)
        {
            const float scale = 0.6f;
            var sz  = Game1.smallFont.MeasureString(text) * scale;
            float bx = _sideArea.X + Pad;
            float by = _btnBindRect.Y - (int)sz.Y - Pad * 2;
            int   bw = _sideArea.Width - Pad * 2;
            int   bh = (int)sz.Y + Pad * 2;

            b.Draw(Game1.fadeToBlackRect,
                new Rectangle((int)bx - 4, (int)by - 4, bw + 8, bh + 8),
                bgColor);
            b.DrawString(Game1.smallFont, text,
                new Vector2(bx, by + Pad * 0.5f), textColor,
                0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static MapRegionData CloneRegion(MapRegionData r) => new()
        {
            Name = r.Name, Locations = new(r.Locations),
            Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom,
            ColorPacked = r.ColorPacked,
            TextColorPacked = r.TextColorPacked,
            Opacity = r.Opacity
        };

        /// <summary>
        /// The canonical list of all known SDV location names.
        /// This is what populates the dropdown. Modded locations discovered
        /// at runtime via ForageTracker are appended on top of this.
        /// </summary>
        private static List<string> BuildKnownLocations() => new()
        {
            "Town", "Forest", "Mountain", "Beach", "Farm", "FarmHouse",
            "Cellar", "Greenhouse", "Mine", "Woods", "Railroad",
            "Desert", "SandyHouse", "SkullCave",
            "IslandSouth", "IslandNorth", "IslandEast", "IslandWest",
            "IslandFarmHouse", "IslandFieldOffice", "IslandShrine",
            "Caldera", "VolcanoDungeon0",
            "Sewer", "BugLand",
            "WitchSwamp", "WitchHut", "WitchWarpCave",
            "BathHousePool", "BathHouseEntry", "BathHouseLocker",
            "JoshHouse", "HaleyHouse", "SamHouse", "Blacksmith", "Museum",
            "Hospital", "ElliottHouse", "AdventureGuild", "FishShop",
            "LeahHouse", "AnimalShop", "WizardHouse", "WizardHouseBasement",
            "Carpenter", "ScienceHouse", "ManorHouse", "Saloon",
            "Tent", "Trailer", "Sunroom",
        };
    }
}
