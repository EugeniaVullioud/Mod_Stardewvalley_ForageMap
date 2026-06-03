using ForageTrackerModSV;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System.Reflection;

namespace ForageTrackerMod
{
    public static class MapTooltipDrawer
    {
        // ── Injected dependencies ─────────────────────────────────────────────

        public static ForageTracker? Tracker { get; set; }
        public static ModConfig? Config { get; set; }
        public static IMonitor? Monitor { get; set; }

        // ── Region storage ────────────────────────────────────────────────────

        private static Dictionary<string, List<MapRegionData>> s_regionsByMap = new();

        /// <summary>
        /// mapKey (editor tab) → SDV map key (e.g. "Town", "Island").
        /// Used to select which region set applies to the current live map.
        /// </summary>
        private static Dictionary<string, string> s_bindings = new();

        public static void SetRegionsByMap(Dictionary<string, List<MapRegionData>> regions)
            => s_regionsByMap = regions;

        public static void SetRegionsByMap(string mapKey, List<MapRegionData> regions)
            => s_regionsByMap[mapKey] = regions;

        public static void SetBindings(Dictionary<string, string>? bindings)
            => s_bindings = bindings ?? new();

        /// <summary>
        /// The last DestinationRect computed for the live game MapPage.
        /// Updated every frame the map menu is open. The editor reads this
        /// so both sides always use the exact same coordinate space.
        /// Null until the map has been opened at least once this session.
        /// </summary>
        public static Rectangle? LastLiveMapRect { get; set; }

        // ─────────────────────────────────────────────────────────────────────
        // DEBUG — set DebugMode = true to draw region overlays and the mouse
        // fractional position on the live map. Remove (or set to false) before
        // shipping. The entire DEBUG block is marked so it is easy to find.
        // ─────────────────────────────────────────────────────────────────────
#if DEBUG
        public static bool DebugMode { get; set; } = true;
#else
        public static bool DebugMode { get; set; } = false;
#endif
        // ─────────────────────────────────────────────────────────────────────

        // ── Icon cache ────────────────────────────────────────────────────────

        private sealed class IconData
        {
            public Texture2D Texture { get; init; } = null!;
            public Rectangle SourceRect { get; init; }
        }
        private static readonly Dictionary<string, IconData?> s_iconCache = new();

        // ── Scale cache ───────────────────────────────────────────────────────

        private static float _cachedUiScale = -1f;
        private static float _cachedIconScale = -1f;
        private static float _iconPx;
        private static float _spriteScale;
        private static float _lineHeight;
        private static float _padding;
        private static float _textScale;
        private static float _headerHeight;

        // Prefix for synthetic region names created from vanilla map hover-points.
        // Stripped before display; used to route resolution in ResolveLocationNames.
        private const string VanillaFallbackPrefix = "__vanilla__";

        // ── Pre-allocated line buffer ─────────────────────────────────────────

        private static readonly List<(string ItemId, string Label)> s_lineBuffer = new(16);

        // Set just before DrawForageSummary so MeasureVanillaBannerRect has the label.
        private static string _lastDisplayName = string.Empty;

        // ── Location key cache ────────────────────────────────────────────────

        private static readonly Dictionary<string, string> s_lowercaseKeyCache = new();

        // =========================================================================
        // Public entry point
        // =========================================================================

        public static void OnRenderedActiveMenu(SpriteBatch b)
        {
            if (Config == null || !Config.Enabled || Tracker == null)
                return;

            if (Game1.activeClickableMenu is not GameMenu gameMenu)
                return;
            if (gameMenu.currentTab != GameMenu.mapTab)
                return;
            if (gameMenu.GetCurrentPage() is not MapPage mapPage)
                return;

            if (!MapRenderUtility.TryGetMapRenderData(mapPage, out var rd))
                return;

            Rectangle mapImageRect = rd.DestinationRect;

            // ── Compute the map image rect ────────────────────────────────────
            //
            // In SDV 1.6, MapPage stretches the world map to fill its entire
            // bounds with no letterboxing. The page bounds are (e.g.) 890×680,
            // which is NOT the native 1360×720 aspect ratio.
            //
            // The editor computes region fractions by letterboxing 1360×720 into
            // its panel space. To keep fractions consistent we must do the same
            // here: derive a rect with the native 1360:720 aspect ratio fitted
            // inside the MapPage bounds, centred.
            //
            // This is the SAME calculation the editor uses, applied to the live
            // MapPage bounds instead of the editor panel — so both sides evaluate
            // fractions against the same relative coordinate space.
            const int NativeMapW = 1360;
            const int NativeMapH = 720;

            int pageW = mapPage.width;
            int pageH = mapPage.height;
            if (pageW <= 0 || pageH <= 0) return;

            float scaleX = (float)pageW / NativeMapW;
            float scaleY = (float)pageH / NativeMapH;
            float fitScale = Math.Min(scaleX, scaleY);

            int fitW = (int)(NativeMapW * fitScale);
            int fitH = (int)(NativeMapH * fitScale);
            int fitX = mapPage.xPositionOnScreen + (pageW - fitW) / 2;
            int fitY = mapPage.yPositionOnScreen + (pageH - fitH) / 2;

            // Rectangle mapImageRect = new Rectangle(fitX, fitY, fitW, fitH);
            LastLiveMapRect = mapImageRect;

            // ── Debug overlay — draws BEFORE tooltip so tooltip is on top ─────
            // BEGIN DEBUG BLOCK — remove or set DebugMode = false to disable
            if (DebugMode) DrawDebugOverlay(b, mapPage, mapImageRect);

            try
            {
                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();

                string? regionName = GetRegionAtPoint(mapImageRect, mouseX, mouseY, mapPage);
                if (regionName == null) return;

                var locationNames = ResolveLocationNames(regionName, mapPage);
                if (locationNames.Count == 0)
                {
                    Monitor?.Log($"[Tooltip] Region '{regionName}' has no locations declared.", LogLevel.Trace);
                    return;
                }

                var merged = MergeSummaries(locationNames);
                if (merged.Count == 0)
                {
                    Monitor?.Log($"[Tooltip] Region '{regionName}' has no forage today.", LogLevel.Trace);
                    return;
                }

                // Strip the internal vanilla-fallback prefix for display.
                string displayName = regionName.StartsWith(VanillaFallbackPrefix, StringComparison.Ordinal)
                    ? regionName[VanillaFallbackPrefix.Length..]
                    : regionName;

                RefreshScaleCache();
                _lastDisplayName = displayName;
                float vanillaH = Game1.dialogueFont.MeasureString(displayName).Y
                                 + 16f * _cachedUiScale;
               // DrawForageSummary(b, mouseX, mouseY, vanillaH, merged);
                DrawForageSummary(b, mouseX, mouseY, mapPage, merged);
            }
            catch (Exception ex)
            {
                Monitor?.Log($"[MapTooltipDrawer] Draw error: {ex}", LogLevel.Error);
            }
        }

        // =========================================================================
        // DEBUG OVERLAY
        // Remove this entire method (and its call above) before shipping.
        /// <summary>
        /// Given a live SDV map key ("Town", "Island", etc.), returns the editor
        /// tab key whose regions should be used — by looking up which tab is bound
        /// to that live key.  Falls back to the live key itself if nothing is bound.
        /// </summary>
        private static string ResolveEditorKey(string liveMapKey)
        {
            foreach (var kv in s_bindings)
                if (kv.Value == liveMapKey) return kv.Key;
            return liveMapKey;
        }

        static void DrawMarker(SpriteBatch b, int x, int y, Color c)
        {
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(x - 5, y - 5, 10, 10),
                c);
        }
        // =========================================================================

        /// <summary>
        /// Draws on the live game map:
        ///   • Coloured rectangles for every region in the current map key
        ///   • The region name inside each rectangle
        ///   • The mouse cursor's current fractional position (relX, relY)
        ///   • The raw mapImageRect coordinates so you can compare with the editor
        ///
        /// This lets you verify that saved fractions match the live map positions
        /// without needing to guess.
        /// </summary>
        private static void DrawDebugOverlay(SpriteBatch b, MapPage mapPage, Rectangle mapImageRect)
        {
            string liveKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey(liveKey);

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            float relX = (mouseX - mapImageRect.X) / (float)mapImageRect.Width;
            float relY = (mouseY - mapImageRect.Y) / (float)mapImageRect.Height;

            // Draw each region as a coloured rectangle
            if (s_regionsByMap.TryGetValue(editorKey, out var regions))
            {
                foreach (var region in regions)
                {
                    // Convert fractions to screen pixels using the LIVE map rect
                    // (same calculation as the hit-test uses)
                    int rx = mapImageRect.X + (int)(region.Left * mapImageRect.Width);
                    int ry = mapImageRect.Y + (int)(region.Top * mapImageRect.Height);
                    int rw = (int)((region.Right - region.Left) * mapImageRect.Width);
                    int rh = (int)((region.Bottom - region.Top) * mapImageRect.Height);
                    var sr = new Rectangle(rx, ry, Math.Max(rw, 2), Math.Max(rh, 2));

                    // Semi-transparent fill
                    b.Draw(Game1.fadeToBlackRect, sr, region.Color * 0.35f);

                    // Bright border
                    DrawDebugBorder(b, sr, region.Color, 2);

                    // Region name
                    if (sr.Width > 20)
                        b.DrawString(Game1.smallFont, region.Name,
                            new Vector2(sr.X + 3, sr.Y + 3),
                            Color.White, 0f, Vector2.Zero, 0.55f, SpriteEffects.None, 1f);
                }
            }

            // Draw fractional mouse position in the top-left corner of the map
            string debugText =
                $"DEBUG MAP: live={liveKey}  editor={editorKey}\n" +
                $"mapRect: x={mapImageRect.X} y={mapImageRect.Y} " +
                $"w={mapImageRect.Width} h={mapImageRect.Height}\n" +
                $"mouse: screen=({mouseX},{mouseY})  frac=({relX:F3},{relY:F3})";

            // Dark backing
            var textSz = Game1.smallFont.MeasureString(debugText) * 0.6f;
            b.Draw(Game1.fadeToBlackRect,
                new Rectangle(mapImageRect.X + 4, mapImageRect.Y + 4,
                              (int)textSz.X + 8, (int)textSz.Y + 8),
                Color.Black * 0.7f);

            b.DrawString(Game1.smallFont, debugText,
                new Vector2(mapImageRect.X + 8, mapImageRect.Y + 8),
                Color.Yellow, 0f, Vector2.Zero, 0.6f, SpriteEffects.None, 1f);
        }

        private static void DrawDebugBorder(SpriteBatch b, Rectangle r, Color c, int t)
        {
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, r.Width, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, t, r.Height), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t, r.Y, t, r.Height), c);
        }

        // =========================================================================
        // Region hit test
        // =========================================================================

        /// <summary>
        /// Determines which region (if any) the mouse is over.
        ///
        /// THE FIX FOR THE OFFSET BUG:
        /// Fractions are stored as values relative to the map IMAGE, not the
        /// MapPage widget bounds. Previously, GetRegionAtPoint called
        /// TryGetMapRenderData inside itself, getting the DestinationRect for the
        /// *game's* MapPage. The editor also called TryGetMapRenderData, but on
        /// a *different* MapPage instance at a different screen position, so the
        /// two DestinationRects had different X/Y origins even though their Width
        /// and Height were the same. This caused the fractions to appear correct
        /// in the editor but offset on the live map.
        ///
        /// The fix: OnRenderedActiveMenu computes mapImageRect ONCE from the live
        /// MapPage and passes it here. The editor does the same from its own
        /// MapPage. Both use TryGetMapRenderData → DestinationRect, but only the
        /// editor's rect is used in ToScreen() and only the live map's rect is
        /// used here. The fractions are therefore always evaluated against the
        /// rect that matches where they were drawn.
        ///
        /// What keeps them in sync: both MapPage instances are constructed with
        /// the same Width and Height (the editor explicitly mirrors the game
        /// panel size), and TryGetMapRenderData's letterbox calculation produces
        /// the same RELATIVE offset within those bounds. The absolute X/Y differ,
        /// but the fractions are relative to the image top-left in both cases, so
        /// they match.
        /// </summary>
        private static string? GetRegionAtPoint(
            Rectangle mapImageRect, int mouseX, int mouseY, MapPage mapPage)
        {
            float relX = (mouseX - mapImageRect.X) / (float)mapImageRect.Width;
            float relY = (mouseY - mapImageRect.Y) / (float)mapImageRect.Height;

            if (relX < 0f || relX > 1f || relY < 0f || relY > 1f)
                return null;

            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey(liveMapKey);

            // ── Player-defined region rectangles ──────────────────────────────
            // Check these first — player rectangles take priority over vanilla points.
            if (s_regionsByMap.TryGetValue(editorKey, out var regions))
            {
                foreach (var region in regions)
                {
                    if (relX >= region.Left && relX <= region.Right &&
                        relY >= region.Top && relY <= region.Bottom)
                        return region.Name;
                }
            }

            // ── Vanilla mapPage.points fallback ───────────────────────────────
            // mapPage.points contains named clickable spots for landmarks like
            // the Quarry entrance, Carpenter Shop, etc. If the cursor is over
            // one of these AND the tracker has forage data for that location,
            // show it even without a player-defined region rectangle.
            // The component .name is the internal location name in most cases.
            foreach (var point in mapPage.points.Values)
            {
                if (!point.containsPoint(mouseX, mouseY))
                    continue;

                // The component name may be a display label, not an internal
                // location name. Try it directly, then try tracker lookup.
                string pointName = point.name;

                if (Tracker != null && Tracker.IsTracked(pointName))
                    return pointName;

                // Fuzzy: find a tracked location whose name is contained in
                // or contains the point name (handles partial matches).
                if (Tracker != null)
                {
                    string lower = pointName.ToLowerInvariant();
                    foreach (var (lk, ok) in s_lowercaseKeyCache)
                    {
                        if (lower.Contains(lk) || lk.Contains(lower))
                            return ok;
                    }
                }
            }

            return null;
        }

        // When returning a vanilla point name directly (not a region name),
        // ResolveLocationNames needs to handle it — it resolves by treating
        // the name as a direct location name if it's not a region.
        // This already works via the result.Add(regionName) fallback below.

        // =========================================================================
        // Location resolution
        // =========================================================================

        public static void RebuildLocationKeyCache()
        {
            s_lowercaseKeyCache.Clear();
            if (Tracker == null) return;
            foreach (var name in Tracker.TrackedLocationNames)
                s_lowercaseKeyCache[name.ToLowerInvariant()] = name;
        }

        private static List<string> ResolveLocationNames(string regionName, MapPage mapPage)
        {
            var result = new List<string>(8);

            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey(liveMapKey);

            if (s_regionsByMap.TryGetValue(editorKey, out var regions))
            {
                var region = regions.FirstOrDefault(
                    r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

                if (region != null)
                {
                    result.AddRange(region.Locations);
                    return result;
                }
            }

            result.Add(regionName);
            return result;
        }

        private static Dictionary<string, ForageTracker.SummaryEntry> MergeSummaries(
            List<string> locationNames)
        {
            var merged = new Dictionary<string, ForageTracker.SummaryEntry>();
            foreach (var name in locationNames)
            {
                var summary = Tracker!.GetSummary(name);
                if (summary == null) continue;

                foreach (var (displayName, entry) in summary)
                {
                    if (!merged.TryGetValue(displayName, out var existing))
                    {
                        merged[displayName] = new ForageTracker.SummaryEntry
                        {
                            ItemId = entry.ItemId,
                            Total = entry.Total,
                            Remaining = entry.Remaining
                        };
                    }
                    else
                    {
                        existing.Total += entry.Total;
                        existing.Remaining += entry.Remaining;
                    }
                }
            }
            return merged;
        }

        // =========================================================================
        // Scale cache
        // =========================================================================

        private static void RefreshScaleCache()
        {
            float ui = Game1.options.uiScale;
            float icon = Config!.IconScale;
            if (ui == _cachedUiScale && icon == _cachedIconScale) return;

            _cachedUiScale = ui;
            _cachedIconScale = icon;
            _iconPx = MathF.Round(24f * ui * icon);
            _spriteScale = _iconPx / 16f;
            _lineHeight = _iconPx + 6f * ui;
            _padding = 20f * ui;
            _textScale = 0.75f * ui;
            _headerHeight = Game1.smallFont.MeasureString(DrawerUIStrings.Forage).Y * _textScale;
        }

        // =========================================================================
        // Drawing
        // =========================================================================

        /// <summary>
        /// Measures the rectangle SDV's own hover banner occupies so we can avoid it.
        /// SDV draws the banner centred above the mouse; its height is one dialogue-font
        /// line plus the menu-texture border padding (8px top + 8px bottom at scale 1).
        /// Everything is multiplied by uiScale so it works at any display size.
        /// </summary>
        private static Rectangle MeasureVanillaBannerRect(string areaLabel, int mouseX, int mouseY)
        {
            // SDV measures the banner with dialogueFont and adds a fixed border.
            // The border is 8px each side in the source texture, scaled by uiScale.
            float border = 8f * _cachedUiScale;
            var   sz     = Game1.dialogueFont.MeasureString(areaLabel);
            float bw     = sz.X + border * 4f;          // SDV adds ~2× border each side
            float bh     = sz.Y + border * 2f;

            // SDV centres the banner horizontally on the mouse and places it
            // just above the cursor (cursor is ~48px tall at scale 1).
            float cursorH = 48f * _cachedUiScale;
            float bx      = mouseX - bw / 2f;
            float by      = mouseY - cursorH - bh;

            // Clamp to screen so our avoidance rect is accurate.
            float sw = Game1.uiViewport.Width;
            float sh = Game1.uiViewport.Height;
            bx = Math.Clamp(bx, 0, sw - bw);
            by = Math.Clamp(by, 0, sh - bh);

            return new Rectangle((int)bx, (int)by, (int)bw, (int)bh);
        }

        private static string GetMapPageHoverText(MapPage mapPage)
        {
            
            try
            {
                return mapPage.hoverText;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Rectangle GetVanillaHoverRect( string hoverText, int mouseX, int mouseY)
        {
            if (string.IsNullOrEmpty(hoverText))
                return Rectangle.Empty;

            float uiScale = Game1.options.uiScale;

            Vector2 size = Game1.smallFont.MeasureString(hoverText);

            int width =
                (int)size.X +
                (int)(32 * uiScale);

            int height =
                (int)size.Y +
                (int)(32 * uiScale);

            int x = mouseX + 32;
            int y = mouseY + 32;

            if (x + width > Game1.uiViewport.Width)
                x = Game1.uiViewport.Width - width;

            if (y + height > Game1.uiViewport.Height)
                y = mouseY - height - 16;

            return new Rectangle(x, y, width, height);
        }
        private static void DrawForageSummary(SpriteBatch b, int mouseX, int mouseY, float vanillaH, Dictionary<string, ForageTracker.SummaryEntry> summary)
        {
            DisplayMode mode      = Config!.Display;
            bool showIcons  = mode is DisplayMode.Both or DisplayMode.IconOnly;
            bool showText   = mode is DisplayMode.Both or DisplayMode.TextOnly;
            bool remainOnly = Config.ShowRemainingOnly;

            s_lineBuffer.Clear();
            float maxLabelW = 0f;

            foreach (var (displayName, entry) in summary)
            {
                if (remainOnly && entry.Remaining == 0)
                    continue;

                string count = remainOnly
                    ? $"×{entry.Remaining}"
                    : $"×{entry.Remaining} / {entry.Total}";

                string label = showText ? $"{displayName}  {count}" : count;

                float w = Game1.smallFont.MeasureString(label).X * _textScale;
                if (w > maxLabelW) maxLabelW = w;

                s_lineBuffer.Add((entry.ItemId, label));
            }

            if (s_lineBuffer.Count == 0) return;

            float iconColW = showIcons ? _iconPx + 6f * _cachedUiScale : 0f;
            float headerW  = Game1.smallFont.MeasureString(DrawerUIStrings.Forage).X * _textScale;
            float contentW = Math.Max(headerW, iconColW + maxLabelW);
            float boxW     = contentW + _padding * 2f;
            float boxH     = _padding * 2f + _headerHeight + 4f * _cachedUiScale
                             + s_lineBuffer.Count * _lineHeight;

            float screenW = Game1.uiViewport.Width;
            float screenH = Game1.uiViewport.Height;

            // ── Position: avoid the vanilla hover banner ──────────────────────
            //
            // We try four candidate positions in preference order:
            //   1. Right of mouse, below banner
            //   2. Left of mouse, below banner
            //   3. Right of mouse, above banner
            //   4. Left of mouse, above banner
            //
            // A candidate is accepted when the box fits entirely on screen and
            // does not overlap the vanilla banner rectangle. The first candidate
            // that satisfies both constraints wins. If none fit perfectly we fall
            // back to the on-screen-clamped position least likely to cover the banner.

            // Measure where SDV will draw its own location-name banner.
            // We need hoverTitle which is set by SDV before our draw call.
            // We approximate it from regionName passed through vanillaH; the banner
            // rect is used only for overlap testing so a slight inaccuracy is fine.
            // Use _lastDisplayName (set just before this call) for banner measurement.
            Rectangle bannerRect = string.IsNullOrEmpty(_lastDisplayName)
                ? new Rectangle(mouseX - 100, mouseY - (int)vanillaH, 200, (int)vanillaH)
                : MeasureVanillaBannerRect(_lastDisplayName, mouseX, mouseY);

            float gap   = _padding * 0.5f;                // gap between our box and banner/cursor
            float cursorH = 48f * _cachedUiScale;         // approximate cursor height

            // Candidate x positions
            float xRight = mouseX + gap;
            float xLeft  = mouseX - boxW - gap;

            // Candidate y positions
            float yBelow = bannerRect.Bottom + gap;       // just below the banner
            float yAbove = bannerRect.Y - boxH - gap;     // just above the banner

            // Rect helpers
            Rectangle BoxAt(float bx, float by) =>
                new Rectangle((int)bx, (int)by, (int)boxW, (int)boxH);

            bool OnScreen(Rectangle r) =>
                r.X >= _padding && r.Y >= _padding
                && r.Right  <= screenW - _padding
                && r.Bottom <= screenH - _padding;

            bool Overlaps(Rectangle a, Rectangle b2) =>
                a.Intersects(b2);

            // Try candidates in preference order
            Rectangle[] candidates =
            {
                BoxAt(xRight, yBelow),
                BoxAt(xLeft,  yBelow),
                BoxAt(xRight, yAbove),
                BoxAt(xLeft,  yAbove),
            };

            Rectangle chosen = candidates[0]; // default
            foreach (var c in candidates)
            {
                if (OnScreen(c) && !Overlaps(c, bannerRect))
                {
                    chosen = c;
                    break;
                }
            }

            // Clamp chosen to screen regardless (safety net for any edge case)
            float finalX = Math.Clamp(chosen.X, _padding, screenW - boxW - _padding);
            float finalY = Math.Clamp(chosen.Y, _padding, screenH - boxH - _padding);

            // ── Draw ──────────────────────────────────────────────────────────
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                (int)finalX, (int)finalY, (int)boxW, (int)boxH,
                Color.White, drawShadow: true);

            float cx = finalX + _padding;
            float cy = finalY + _padding;

            b.DrawString(Game1.smallFont, DrawerUIStrings.Forage, new Vector2(cx, cy),
                Game1.textColor, 0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);
            cy += _headerHeight + 4f * _cachedUiScale;

            foreach (var (itemId, label) in s_lineBuffer)
            {
                if (showIcons && !string.IsNullOrEmpty(itemId))
                    TryDrawItemIcon(b, itemId, new Vector2(cx, cy));

                float textX = showIcons ? cx + _iconPx + 6f * _cachedUiScale : cx;
                float textH = Game1.smallFont.MeasureString(label).Y * _textScale;
                float textY = showIcons ? cy + (_iconPx - textH) / 2f : cy;

                b.DrawString(Game1.smallFont, label, new Vector2(textX, textY),
                    Game1.textColor, 0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

                cy += _lineHeight;
            }
        }
        private static void DrawForageSummary(SpriteBatch b, int mouseX, int mouseY, MapPage mapPage, Dictionary<string, ForageTracker.SummaryEntry> summary)
        {
            DisplayMode mode = Config!.Display;
            bool showIcons = mode is DisplayMode.Both or DisplayMode.IconOnly;
            bool showText = mode is DisplayMode.Both or DisplayMode.TextOnly;
            bool remainOnly = Config.ShowRemainingOnly;

            s_lineBuffer.Clear();
            float maxLabelW = 0f;

            foreach (var (displayName, entry) in summary)
            {
                if (remainOnly && entry.Remaining == 0)
                    continue;

                string count = remainOnly
                    ? $"×{entry.Remaining}"
                    : $"×{entry.Remaining} / {entry.Total}";

                string label = showText ? $"{displayName}  {count}" : count;

                float w = Game1.smallFont.MeasureString(label).X * _textScale;
                if (w > maxLabelW) maxLabelW = w;

                s_lineBuffer.Add((entry.ItemId, label));
            }

            if (s_lineBuffer.Count == 0) return;

            float iconColW = showIcons ? _iconPx + 6f * _cachedUiScale : 0f;
            float headerW = Game1.smallFont.MeasureString(DrawerUIStrings.Forage).X * _textScale;
            float contentW = Math.Max(headerW, iconColW + maxLabelW);
            float boxW = contentW + _padding * 2f;
            float boxH = _padding * 2f + _headerHeight + 4f * _cachedUiScale
                             + s_lineBuffer.Count * _lineHeight;

            float screenW = Game1.uiViewport.Width;
            float screenH = Game1.uiViewport.Height;

            string hoverText = GetMapPageHoverText(mapPage);

            var occupiedRects = new List<Rectangle>();

            Rectangle vanillaRect =
                GetVanillaHoverRect(
                    hoverText,
                    mouseX,
                    mouseY);

            if (!vanillaRect.IsEmpty)
                occupiedRects.Add(vanillaRect);

            Rectangle tooltipRect = FindBestTooltipPosition(
                mouseX,
                mouseY,
                (int)boxW,
                (int)boxH,
                occupiedRects,
                Game1.uiViewport.Width,
                Game1.uiViewport.Height);

            float finalX = tooltipRect.X;
            float finalY = tooltipRect.Y;

            // ── Draw ──────────────────────────────────────────────────────────
            IClickableMenu.drawTextureBox(b, Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                (int)finalX, (int)finalY, (int)boxW, (int)boxH,
                Color.White, drawShadow: true);

            float cx = finalX + _padding;
            float cy = finalY + _padding;

            b.DrawString(Game1.smallFont, DrawerUIStrings.Forage, new Vector2(cx, cy),
                Game1.textColor, 0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);
            cy += _headerHeight + 4f * _cachedUiScale;

            foreach (var (itemId, label) in s_lineBuffer)
            {
                if (showIcons && !string.IsNullOrEmpty(itemId))
                    TryDrawItemIcon(b, itemId, new Vector2(cx, cy));

                float textX = showIcons ? cx + _iconPx + 6f * _cachedUiScale : cx;
                float textH = Game1.smallFont.MeasureString(label).Y * _textScale;
                float textY = showIcons ? cy + (_iconPx - textH) / 2f : cy;

                b.DrawString(Game1.smallFont, label, new Vector2(textX, textY),
                    Game1.textColor, 0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

                cy += _lineHeight;
            }
        }
        private static float ScoreCandidate(
    Rectangle candidate,
    Point cursor,
    List<Rectangle> occupiedRects,
    int screenW,
    int screenH)
        {
            float score = 0f;

            // Huge penalty for overlap
            foreach (Rectangle occupied in occupiedRects)
            {
                Rectangle intersection =
                    Rectangle.Intersect(
                        candidate,
                        occupied);

                if (!intersection.IsEmpty)
                {
                    score -=
                        intersection.Width *
                        intersection.Height *
                        1000f;
                }
            }

            // Prefer fully visible positions
            if (candidate.Left < 0)
                score -= 50000;

            if (candidate.Top < 0)
                score -= 50000;

            if (candidate.Right > screenW)
                score -= 50000;

            if (candidate.Bottom > screenH)
                score -= 50000;

            // Prefer positions near cursor
            Point center =
                new(
                    candidate.Center.X,
                    candidate.Center.Y);

            float dx = center.X - cursor.X;
            float dy = center.Y - cursor.Y;

            float distance =
                MathF.Sqrt(dx * dx + dy * dy);

            score -= distance * 0.5f;

            // Small bonus for being to the right of cursor
            if (candidate.X > cursor.X)
                score += 250;

            // Small bonus for being below cursor
            if (candidate.Y > cursor.Y)
                score += 100;

            return score;
        }
        private static Rectangle FindBestTooltipPosition(
    int mouseX,
    int mouseY,
    int width,
    int height,
    List<Rectangle> occupiedRects,
    int screenW,
    int screenH)
        {
            Rectangle bestRect = Rectangle.Empty;
            float bestScore = float.MinValue;

            Point cursor = new(mouseX, mouseY);

            for (int radius = 48; radius <= 600; radius += 24)
            {
                for (int angle = 0; angle < 360; angle += 10)
                {
                    float rad = MathHelper.ToRadians(angle);

                    int x = mouseX + (int)(Math.Cos(rad) * radius);
                    int y = mouseY + (int)(Math.Sin(rad) * radius);

                    Rectangle candidate =
                        new Rectangle(
                            x,
                            y,
                            width,
                            height);

                    float score =
                        ScoreCandidate(
                            candidate,
                            cursor,
                            occupiedRects,
                            screenW,
                            screenH);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRect = candidate;
                    }
                }
            }

            if (bestRect == Rectangle.Empty)
            {
                bestRect = new Rectangle(
                    mouseX + 48,
                    mouseY + 48,
                    width,
                    height);
            }

            bestRect.X =
                Math.Clamp(
                    bestRect.X,
                    0,
                    screenW - width);

            bestRect.Y =
                Math.Clamp(
                    bestRect.Y,
                    0,
                    screenH - height);

            return bestRect;
        }
        private static void TryDrawItemIcon(SpriteBatch b, string itemId, Vector2 position)
        {
            if (!s_iconCache.TryGetValue(itemId, out var icon))
            {
                try
                {
                    var parsed = ItemRegistry.GetDataOrErrorItem(itemId);
                    var tex = parsed.GetTexture();
                    icon = tex != null
                        ? new IconData { Texture = tex, SourceRect = parsed.GetSourceRect() }
                        : null;
                }
                catch { icon = null; }
                s_iconCache[itemId] = icon;
            }

            if (icon == null) return;
            b.Draw(icon.Texture, position, icon.SourceRect,
                Color.White, 0f, Vector2.Zero, _spriteScale, SpriteEffects.None, 1f);
        }
    }
}
