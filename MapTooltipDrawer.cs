using ForageTrackerModSV;
using ForageTrackerModSV.Compatibility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using xTile.Tiles;
using System.Text.RegularExpressions;
using StardewValley.Buildings;
using StardewValley.WorldMaps;





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
        // Separates multiple location names when a single hoverable manually
        // maps to more than one GameLocation (see ResolveAll/HoverMappingStore).
        const string VanillaMultiDelimiter = "\u0001";

        // ── Pre-allocated line buffer ─────────────────────────────────────────
        static readonly List<(string ItemId, string Label)> s_lineBuffer = new(16);

        // ── Location key cache ────────────────────────────────────────────────
        static readonly Dictionary<string, string> s_lowercaseKeyCache = new();


        // ── Tooltip positioner cache keys ─────────────────────────────────────
        static int _lastBoxW = -1;
        static int _lastBoxH = -1;
#if DEBUG
        // ── Performance debug ─────────────────────────────────────────────────
        // Measures elapsed time per OnRenderedActiveMenu call.
        // Logs a rolling average every 60 frames when DebugMode is on.
        static readonly System.Diagnostics.Stopwatch _perfWatch    = new();
        static long  _perfTotalUs    = 0;
        static int   _perfFrameCount = 0;
        static int   _perfSpiralRuns = 0;
        const  int   PerfLogInterval = 60;
#endif
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

#if DEBUG
            _perfWatch.Restart();
#endif
            Rectangle mapImageRect = rd.DestinationRect;

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

                Debugger.DebugLog(Monitor,$"Hovered region: {regionName}", LogLevel.Debug);

                var locationNames = ResolveLocationNames(regionName, mapPage);
                if (locationNames.Count == 0)
                {
                    Debugger.DebugLog(Monitor, $"[Tooltip] Region '{regionName}' has no locations declared.", LogLevel.Debug);
                    return;
                }

                // For user-defined regions, include all building descendants
                // (e.g. Carpenter Shop interior counted under Mountain region).
                // For vanilla fallback points, descendants are not included —
                // they already resolved to a specific location name.
                bool isVanilla = regionName.StartsWith(VanillaFallbackPrefix, StringComparison.Ordinal);
                var merged = MergeSummaries(locationNames, includeDescendants: !isVanilla);
                if (merged.Count == 0)
                {                  
                    Debugger.DebugLog(Monitor, $"[Tooltip] Region '{regionName}' has no forage today.", LogLevel.Debug);
                    return;
                }

                // Strip the internal vanilla-fallback prefix for display.
                // When a hoverable was manually mapped to multiple locations,
                // show them joined (e.g. "Mountain / Quarry") rather than just
                // the first — the tooltip body already aggregates forage from
                // all of them, so the header should reflect that.
                string displayName = regionName.StartsWith(VanillaFallbackPrefix, StringComparison.Ordinal)
                    ? string.Join(" / ", regionName[VanillaFallbackPrefix.Length..].Split(VanillaMultiDelimiter))
                    : regionName;

                RefreshScaleCache();
                float vanillaH = Game1.dialogueFont.MeasureString(displayName).Y
                                 + 16f * _cachedUiScale;
               // DrawForageSummary(b, mouseX, mouseY, vanillaH, merged);
                DrawForageSummary(b, mouseX, mouseY, mapPage, merged);
                Debugger.DebugLog(Monitor, $"it should draw: {displayName}", LogLevel.Debug);

            }
            catch (Exception ex)
            {
                Debugger.DebugLog(Monitor, $"[MapTooltipDrawer] Draw error: {ex}", LogLevel.Debug);
            }
#if DEBUG
            finally
            {
                _perfWatch.Stop();
                _perfTotalUs    += _perfWatch.ElapsedTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                _perfFrameCount++;

                if (_perfFrameCount >= PerfLogInterval)
                {
                    float avgUs     = (float)_perfTotalUs / _perfFrameCount;
                    float avgMs     = avgUs / 1000f;
                    int   spirals   = _perfSpiralRuns;
                    /*
                    Debugger.DebugLog(Monitor, $"[TooltipPerf] avg {avgMs:F3} ms/frame over {_perfFrameCount} frames | " +
                        $"spiral re-solves: {spirals} / {_perfFrameCount}", LogLevel.Debug);
                    */
                    _perfTotalUs    = 0;
                    _perfFrameCount = 0;
                    _perfSpiralRuns = 0;
                }
            }
#endif
        }

        // =========================================================================
        // Region hit test
        // =========================================================================
        static string ExtractLocationPart(string input, char separator = '/', char extension = '_')
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            var slashIndex = input.IndexOf(separator);
            if (slashIndex < 0 || slashIndex == input.Length - 1)
                return string.Empty;

            var afterSlash = input[(slashIndex + 1)..];

            var underscoreIndex = afterSlash.IndexOf(extension);
            return underscoreIndex >= 0
                ? afterSlash[..underscoreIndex]
                : afterSlash;
        }
        static string ExtractBeforeSeparator(string input, char separator = '/')
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Find the separator
            var sepIndex = input.IndexOf(separator);
            if (sepIndex < 0) // separator not found
                sepIndex = input.Length; // take the whole string

            var beforeSep = input[..sepIndex];
           
            return beforeSep;
        }
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
        /// 
        static string? GetRegionAtPoint(Rectangle mapImageRect, int mouseX, int mouseY, MapPage mapPage)
        {
            float relX = (mouseX - mapImageRect.X) / (float)mapImageRect.Width;
            float relY = (mouseY - mapImageRect.Y) / (float)mapImageRect.Height;

            if (relX < 0f || relX > 1f || relY < 0f || relY > 1f) return null;

            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey  = ResolveEditorKey.Resolve(liveMapKey, s_bindings);

            // ── Resolve the map point under the cursor ───────────────────
            // ResolveAll checks the user's manual mapping (editor's "Edit Map
            // Hover Data Relationships" panel) first; only hoverables that
            // have not been manually mapped fall back to the old WorldPositions
            // scoring heuristic. A single hoverable may resolve to several
            // GameLocation names when manually mapped to more than one.
            MapPointResolver.EnsureBuilt(mapPage);
            var points = MapPageCompat.GetPoints(mapPage);

            List<string> hoveredLocations = new();
            foreach (var point in points)
            {
                if (!point.containsPoint(mouseX, mouseY)) continue;
                hoveredLocations = MapPointResolver.ResolveAll(editorKey, point.name, Tracker);
                Debugger.DebugLog(Monitor,
                    $"[HoverPoint] point.name='{point.name}' → resolved=[{string.Join(", ", hoveredLocations)}]",
                    LogLevel.Debug);
                break;
            }

            // ── Building icon intercept — always takes priority ───────────────
            // IndoorMap and FarmBuilding show only their own forageables.
            // Has forage → standalone building tooltip (aggregated across all
            // manually-mapped locations, summed). No forage → null (silent,
            // never falls back to region).
            //
            // Only treated as a "building intercept" when EVERY resolved
            // location is non-Outdoor — a manual mapping that mixes an
            // outdoor location with an indoor one falls through to the
            // region/point logic below instead of silently dropping.
            if (hoveredLocations.Count > 0 &&
                hoveredLocations.All(n => LocationHierarchy.GetLocationType(n) != LocationType.Outdoor))
            {
                bool hasForage = Tracker != null &&
                    hoveredLocations.Any(n => Tracker.IsTracked(n) && (Tracker.GetSummary(n)?.Count ?? 0) > 0);
                return hasForage ? EncodeVanilla(hoveredLocations) : null;
            }

            // ── Player-defined region rectangles ──────────────────────────────
            // Only reached when cursor is NOT over a building icon.
            if (s_regionsByMap.TryGetValue(editorKey, out var regions))
            {
                foreach (var region in regions)
                {
                    if (relX >= region.Left && relX <= region.Right &&
                        relY >= region.Top   && relY <= region.Bottom)
                        return region.Name;
                }
            }

            // ── Outdoor vanilla point fallback (no region matched) ────────────
            if (hoveredLocations.Count > 0
                && Tracker != null
                && hoveredLocations.Any(n => Tracker.IsTracked(n)))
                return EncodeVanilla(hoveredLocations);

            return null;
        }

        // Encodes one or more resolved GameLocation names into the
        // VanillaFallbackPrefix-tagged region string consumed by
        // ResolveLocationNames. Multiple names are NUL-joined so the prefix
        // check and split logic stay simple at the decode site.
        static string EncodeVanilla(List<string> locationNames) =>
            VanillaFallbackPrefix + string.Join(VanillaMultiDelimiter, locationNames);
        // Extract the base name: everything before the first known suffix
        static string GetBaseName(string name)
        {
            // Assuming suffixes are capitalized words at the end
            // We take the first word as base (everything up to the first capitalized suffix)
            var match = Regex.Match(name, @"^[A-Z][a-z]+");
            return match.Success ? match.Value : name;
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

            // ── Vanilla point (no user region) ────────────────────────────────
            // The regionName IS the location name (or NUL-joined names, when a
            // hoverable was manually mapped to several). If it is a building
            // interior we show only that interior's forage — never the outer
            // area's data. This is already handled: result contains only the
            // interior name(s), and MergeSummaries will return nothing if it
            // has no forage.
            if (regionName.StartsWith(VanillaFallbackPrefix, StringComparison.Ordinal))
            {
                result.AddRange(regionName[VanillaFallbackPrefix.Length..].Split(VanillaMultiDelimiter));
                return result;
            }

            // ── User-defined region ───────────────────────────────────────────
            string liveMapKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey  = ResolveEditorKey.Resolve(liveMapKey, s_bindings);

            if (s_regionsByMap.TryGetValue(editorKey, out var regions))
            {
                var region = regions.FirstOrDefault(
                    r => string.Equals(r.Name, regionName, StringComparison.OrdinalIgnoreCase));

                if (region != null)
                {
                    // Filter out building interiors that are hovered as vanilla
                    // points — they have their own hover path above and should
                    // not bleed forage into the parent region tooltip.
                    // Only exclude them if the mouse is directly over a vanilla
                    // point for that location (checked in GetRegionAtPoint first).
                    result.AddRange(region.Locations);
                    return result;
                }
            }

            result.Add(regionName);
            return result;
        }

        // IsStandaloneInterior replaced by LocationHierarchy.IsIndoor — no hardcoded names.

        /// <summary>
        /// Merges forageable summaries for the given location names.
        ///
        /// AGGREGATION MODEL — strictly downward, two contexts:
        ///
        /// ── Building hover (includeDescendants = false from caller) ──────────
        ///   AccumulateSubtree(building):
        ///     Recursively includes ALL descendants of the building regardless
        ///     of type. A building's subareas (Sebastian's room, Science House,
        ///     Cellar, etc.) are always part of that building's forage count.
        ///     Stops at the boundary — never walks UP to the parent area.
        ///
        /// ── Region hover (includeDescendants = true from caller) ─────────────
        ///   For each location explicitly listed in region.Locations:
        ///     AccumulateSubtree(location) — collects that location AND its full
        ///     subtree (all buildings + their subareas).
        ///   Locations NOT in the list are never touched → no bleed from
        ///   unrelated areas.
        ///
        /// ── Outdoor point (includeDescendants = false) ───────────────────────
        ///   Same as building hover but for an outdoor location. Only that
        ///   location's own forage (outdoor locations have no indoor subareas
        ///   in the hierarchy that need to be shown here — those show via
        ///   building hover or region hover).
        ///
        /// The visited set prevents double-counting if the same location appears
        /// as both an explicit entry and a descendant of another entry.
        /// </summary>
        static Dictionary<string, ForageTracker.SummaryEntry> MergeSummaries(
            List<string> locationNames, bool includeDescendants = false)
        {
            var merged  = new Dictionary<string, ForageTracker.SummaryEntry>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Accumulate forage for a single location into merged.
            void AccumulateOne(string name)
            {
                var summary = Tracker?.GetSummary(name);
                if (summary == null) return;
                Debugger.DebugLog(Monitor, $"[Merge] accumulate: {name}", LogLevel.Trace);

                foreach (var (displayName, entry) in summary)
                {
                    if (!merged.TryGetValue(displayName, out var existing))
                        merged[displayName] = new ForageTracker.SummaryEntry
                            { ItemId = entry.ItemId, Total = entry.Total, Remaining = entry.Remaining };
                    else
                    {
                        existing.Total     += entry.Total;
                        existing.Remaining += entry.Remaining;
                    }
                }
            }

            // Recursively accumulate a location AND its full descendant subtree.
            // Used for both building hovers (collect all subareas) and region
            // entries (collect all buildings + their subareas).
            void AccumulateSubtree(string name)
            {
                if (!visited.Add(name)) return;  // cycle / duplicate guard
                AccumulateOne(name);
                foreach (var child in LocationHierarchy.GetChildren(name))
                    AccumulateSubtree(child);
            }

            if (includeDescendants)
            {
                // Region hover: walk full subtree of each explicitly listed location.
                // Only locations in the list (and their descendants) contribute —
                // nothing outside this set is ever visited.
                foreach (var name in locationNames)
                    AccumulateSubtree(name);
            }
            else
            {
                // Building or outdoor point hover:
                // Still walk the full subtree of the resolved location so that
                // subareas (Sebastian's room, Science House, Cellar, etc.) are
                // included. The subtree is bounded by the hierarchy — it never
                // climbs to the parent area.
                foreach (var name in locationNames)
                    AccumulateSubtree(name);
            }

            Debugger.DebugLog(Monitor,
                $"[Merge] {locationNames.Count} root(s), {visited.Count} total locations " +
                $"→ {merged.Count} unique item(s)",
                LogLevel.Trace);
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

            Rectangle vanillaRect = GetVanillaHoverRect(hoverText, mouseX, mouseY);

            if ((int)boxW != _lastBoxW || (int)boxH != _lastBoxH)
            {
                TooltipPositioner.Invalidate();
                _lastBoxW = (int)boxW;
                _lastBoxH = (int)boxH;
            }

#if DEBUG
            int spiralsBefore = TooltipPositioner.SpiralRunCount;
#endif

            Point pos = TooltipPositioner.GetPosition(
                mouseX, mouseY,
                (int)boxW, (int)boxH,
                vanillaRect,
                Game1.uiViewport.Width, Game1.uiViewport.Height,
                _cachedUiScale);

#if DEBUG
            if (TooltipPositioner.SpiralRunCount != spiralsBefore) _perfSpiralRuns++;
#endif

            float finalX = pos.X;
            float finalY = pos.Y;

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
    public static class LocationHelper
    {
        /// <summary>
        /// Builds a map from all interiors and sub-areas to their parent "big" location.
        /// Must be called after all locations are loaded (e.g., DayStarted).
        /// </summary>
        public static Dictionary<string, string> BuildInteriorToParentMap(IEnumerable<MapArea> mapAreas)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var area in mapAreas)
            {
                if (area?.Data?.WorldPositions == null)
                    continue;

                foreach (var pos in area.Data.WorldPositions)
                {
                    string parent = pos.LocationName ?? pos.Id ?? area.Id;

                    if (pos.LocationNames != null)
                    {
                        foreach (var sub in pos.LocationNames)
                        {
                            map[sub] = parent; // <- This gives "ScienceHouse" => "CarpenterShop"
                        }
                    }
                }
            }

            return map;
            return map;
        }

        /// <summary>
        /// Adds an interior and recursively maps its sub-locations.
        /// </summary>
        private static void AddInterior(GameLocation interior, string parentName, Dictionary<string, string> map)
        {
            if (interior == null) return;

            string interiorName = interior.Name;
            if (!string.IsNullOrEmpty(interiorName) && !map.ContainsKey(interiorName))
                map[interiorName] = parentName;

            // Recursively handle modded interiors or sub-areas if any
            // For example, some interiors may contain sub GameLocation instances
            if (interior is IContainsSubLocations container)
            {
                foreach (var sub in container.GetSubLocations())
                {
                    string subName = sub.Name ?? parentName; // fallback if null
                    if (!map.ContainsKey(subName))
                        map[subName] = parentName;

                    // Recursive call for nested sub-locations
                    AddInterior(sub, parentName, map);
                }
            }
        }

        /// <summary>
        /// Helper interface you can implement for modded interiors that contain sub-locations.
        /// If no mods do this, this section is safe to leave empty.
        /// </summary>
        public interface IContainsSubLocations
        {
            IEnumerable<GameLocation> GetSubLocations();
        }
    }

}
