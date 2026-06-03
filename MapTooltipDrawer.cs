using ForageTrackerModSV;
using ForageTrackerModSV.Compatibility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
#if DEBUG
using ForageTrackerModSV.Debug;
#endif

namespace ForageTrackerMod
{
    public static class MapTooltipDrawer
    {
        // ── Injected dependencies ─────────────────────────────────────────────

        public static ForageTracker? Tracker { get; set; }
        public static ModConfig? Config { get; set; }
        public static IMonitor? Monitor { get; set; }

        // ── Region storage ────────────────────────────────────────────────────

        static Dictionary<string, List<MapRegionData>> s_regionsByMap = new();

        /// <summary>
        /// mapKey (editor tab) → SDV map key (e.g. "Town", "Island").
        /// Used to select which region set applies to the current live map.
        /// </summary>
        static Dictionary<string, string> s_bindings = new();

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

        sealed class IconData
        {
            public Texture2D Texture { get; init; } = null!;
            public Rectangle SourceRect { get; init; }
        }
        static readonly Dictionary<string, IconData?> s_iconCache = new();

        // ── Scale cache ───────────────────────────────────────────────────────

        static float _cachedUiScale = -1f;
        static float _cachedIconScale = -1f;
        static float _iconPx;
        static float _spriteScale;
        static float _lineHeight;
        static float _padding;
        static float _textScale;
        static float _headerHeight;

        // Prefix for synthetic region names created from vanilla map hover-points.
        // Stripped before display; used to route resolution in ResolveLocationNames.
        const string VanillaFallbackPrefix = "__vanilla__";

        // ── Pre-allocated line buffer ─────────────────────────────────────────
        static readonly List<(string ItemId, string Label)> s_lineBuffer = new(16);

        // ── Location key cache ────────────────────────────────────────────────
        static readonly Dictionary<string, string> s_lowercaseKeyCache = new();

        // =========================================================================
        // Public entry point
        // =========================================================================

        public static void OnRenderedActiveMenu(SpriteBatch b)
        {
            // Avoid executing the expensive logic if proper conditions havent been met.
            if (Config == null || !Config.Enabled || Tracker == null) return;
            if (Game1.activeClickableMenu is not GameMenu gameMenu) return;
            if (gameMenu.currentTab != GameMenu.mapTab) return;
            if (gameMenu.GetCurrentPage() is not MapPage mapPage) return;
            if (!MapRenderUtility.TryGetMapRenderData(mapPage, out var rd)) return;

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
#if DEBUG
            // ── Debug overlay — draws BEFORE tooltip so tooltip is on top ─────
            // BEGIN DEBUG BLOCK — remove or set DebugMode = false to disable
            if (DebugMode) MapDebugRenderer.Draw(b,Monitor,mapPage, mapImageRect, s_regionsByMap, s_bindings);
#endif
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
        static string? GetRegionAtPoint( Rectangle mapImageRect, int mouseX, int mouseY, MapPage mapPage)
        {
            float relX = (mouseX - mapImageRect.X) / (float)mapImageRect.Width;
            float relY = (mouseY - mapImageRect.Y) / (float)mapImageRect.Height;

            if (relX < 0f || relX > 1f || relY < 0f || relY > 1f) return null;

            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey.Resolve(liveMapKey, s_bindings);

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
            var points = MapPageCompat.GetPoints(mapPage);
            foreach (var point in points)
            {
                if (!point.containsPoint(mouseX, mouseY))  continue;

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
                        if (lower.Contains(lk) || lk.Contains(lower)) return ok;
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

        static List<string> ResolveLocationNames(string regionName, MapPage mapPage)
        {
            var result = new List<string>(8);

            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey.Resolve(liveMapKey, s_bindings);

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

        static Dictionary<string, ForageTracker.SummaryEntry> MergeSummaries(List<string> locationNames)
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

        static void RefreshScaleCache()
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

        static Rectangle GetVanillaHoverRect( string hoverText, int mouseX, int mouseY)
        {
            if (string.IsNullOrEmpty(hoverText))
                return Rectangle.Empty;

            float uiScale = Game1.options.uiScale;

            Vector2 size = Game1.smallFont.MeasureString(hoverText);

            int width = (int)size.X + (int)(32 * uiScale);

            int height =(int)size.Y + (int)(32 * uiScale);

            int x = mouseX + 32;
            int y = mouseY + 32;

            if (x + width > Game1.uiViewport.Width)
                x = Game1.uiViewport.Width - width;

            if (y + height > Game1.uiViewport.Height) y = mouseY - height - 16;

            return new Rectangle(x, y, width, height);
        }
        
        static void DrawForageSummary(SpriteBatch b, int mouseX, int mouseY, MapPage mapPage, Dictionary<string, ForageTracker.SummaryEntry> summary)
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

            string hoverText = MapPageCompat.GetHoverText(mapPage);

            var occupiedRects = new List<Rectangle>();

            Rectangle vanillaRect = GetVanillaHoverRect( hoverText, mouseX, mouseY);

            if (!vanillaRect.IsEmpty) occupiedRects.Add(vanillaRect);

            Rectangle tooltipRect = FindBestTooltipPosition( mouseX, mouseY,(int)boxW, (int)boxH, occupiedRects, Game1.uiViewport.Width, Game1.uiViewport.Height);

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
        static float ScoreCandidate( Rectangle candidate, Point cursor, List<Rectangle> occupiedRects,int screenW,int screenH)
        {
            float score = 0f;

            // Huge penalty for overlap
            foreach (Rectangle occupied in occupiedRects)
            {
                Rectangle intersection = Rectangle.Intersect(candidate,occupied);

                if (!intersection.IsEmpty)
                {
                    score -= intersection.Width * intersection.Height * 1000f;
                }
            }

            // Prefer fully visible positions
            if (candidate.Left < 0) score -= 50000;

            if (candidate.Top < 0) score -= 50000;

            if (candidate.Right > screenW) score -= 50000;

            if (candidate.Bottom > screenH) score -= 50000;

            // Prefer positions near cursor
            Point center = new( candidate.Center.X, candidate.Center.Y);

            float dx = center.X - cursor.X;
            float dy = center.Y - cursor.Y;

            float distance =  MathF.Sqrt(dx * dx + dy * dy);

            score -= distance * 0.5f;

            // Small bonus for being to the right of cursor
            if (candidate.X > cursor.X) score += 250;

            // Small bonus for being below cursor
            if (candidate.Y > cursor.Y) score += 100;

            return score;
        }
        static Rectangle FindBestTooltipPosition( int mouseX, int mouseY, int width, int height, List<Rectangle> occupiedRects, int screenW, int screenH)
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

                    Rectangle candidate = new Rectangle( x,y, width, height);

                    float score =  ScoreCandidate( candidate, cursor, occupiedRects, screenW,screenH);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRect = candidate;
                    }
                }
            }

            if (bestRect == Rectangle.Empty)
            {
                bestRect = new Rectangle( mouseX + 48, mouseY + 48, width, height);
            }

            bestRect.X = Math.Clamp( bestRect.X, 0,screenW - width);

            bestRect.Y = Math.Clamp(bestRect.Y,0,screenH - height);

            return bestRect;
        }
        static void TryDrawItemIcon(SpriteBatch b, string itemId, Vector2 position)
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
            b.Draw(icon.Texture, position, icon.SourceRect, Color.White, 0f, Vector2.Zero, _spriteScale, SpriteEffects.None, 1f);
        }
    }
}
