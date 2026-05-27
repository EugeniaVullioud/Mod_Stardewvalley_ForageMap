using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System.Reflection;

namespace ForageTrackerMod
{
    /// <summary>
    /// Draws the forage tooltip overlay on top of the map menu.
    /// No Harmony required — hooks into SMAPI's Display.RenderedActiveMenu event,
    /// which fires after any menu finishes drawing, including the map.
    /// </summary>
    public static class MapTooltipDrawer
    {
        // -------------------------------------------------------------------------
        // Injected dependencies
        // -------------------------------------------------------------------------

        public static ForageTracker? Tracker { get; set; }
        public static ModConfig? Config { get; set; }
        public static IMonitor? Monitor { get; set; }

        // -------------------------------------------------------------------------
        // Cached icon data — resolved once per item id, reused every frame
        // -------------------------------------------------------------------------

        private sealed class IconData
        {
            public Texture2D Texture { get; init; } = null!;
            public Rectangle SourceRect { get; init; }
        }

        private static readonly Dictionary<string, IconData?> s_iconCache = new();

        // -------------------------------------------------------------------------
        // Cached scale-dependent measurements
        // Recomputed only when uiScale or IconScale actually changes
        // -------------------------------------------------------------------------

        private static float _cachedUiScale = -1f;
        private static float _cachedIconScale = -1f;

        private static float _iconPx;
        private static float _spriteScale;
        private static float _lineHeight;
        private static float _padding;
        private static float _textScale;
        private static float _headerHeight;

        // -------------------------------------------------------------------------
        // Pre-allocated line buffer — reused every frame, never triggers GC
        // -------------------------------------------------------------------------

        private static readonly List<(string ItemId, string Label)> s_lineBuffer = new(16);

        // -------------------------------------------------------------------------
        // Location key cache — pre-lowercased, rebuilt once per day
        // -------------------------------------------------------------------------

        private static readonly Dictionary<string, string> s_lowercaseKeyCache = new();

        // -------------------------------------------------------------------------
        // Public entry point — call this from Display.RenderedActiveMenu
        // -------------------------------------------------------------------------

        /// <summary>
        /// Main draw call. Wire this to helper.Events.Display.RenderedActiveMenu.
        /// Exits immediately if the active menu is not the map tab.
        /// </summary>
        public static void OnRenderedActiveMenu(SpriteBatch b)
        {
            try
            {
                if (Config == null || !Config.Enabled || Tracker == null)
                    return;

                // ── 1. Confirm we are on the map tab ─────────────────────────────
                // The map lives inside GameMenu as one of its tabs.
                if (Game1.activeClickableMenu is not GameMenu gameMenu)
                    return;

                if (gameMenu.currentTab != GameMenu.mapTab)
                    return;

                // Get the MapPage from the current tab's page list
                if (gameMenu.GetCurrentPage() is not MapPage mapPage)
                    return;

                // ── 2. Find which area is hovered ─────────────────────────────────
                // MapPage exposes a public list of ClickableComponent points,
                // each representing a named area on the map.
                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();

                ClickableComponent? hovered = null;
                foreach (var point in mapPage.points)
                {
                    if (point.Value.containsPoint(mouseX, mouseY))
                    {
                        hovered = point.Value;
                        break;
                    }
                }

                if (hovered == null)
                    return;

                // ── 3. Match to a tracked location ────────────────────────────────
                string? locationName = FindBestLocationMatch(hovered.name);
                if (locationName == null)
                    return;

                // Direct reference to pre-built cache — zero allocation
                var summary = Tracker.GetSummary(locationName);
                if (summary == null)
                    return;

                // ── 4. Draw ───────────────────────────────────────────────────────
                RefreshScaleCache();
                DrawForageSummary(b, mouseX, mouseY, summary);
            }
            catch (Exception ex)
            {
                Monitor?.Log($"[MapTooltipDrawer] Draw error: {ex}", LogLevel.Error);
            }
        }

        // -------------------------------------------------------------------------
        // Location key cache
        // -------------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the pre-lowercased location key cache.
        /// Call this after every ScanAllLocations() in ModEntry.
        /// </summary>
        public static void RebuildLocationKeyCache()
        {
            s_lowercaseKeyCache.Clear();
            if (Tracker == null) return;

            foreach (var name in Tracker.TrackedLocationNames)
                s_lowercaseKeyCache[name.ToLowerInvariant()] = name;
        }

        // -------------------------------------------------------------------------
        // Scale cache
        // -------------------------------------------------------------------------

        private static void RefreshScaleCache()
        {
            float ui = Game1.options.uiScale;
            float icon = Config!.IconScale;

            if (ui == _cachedUiScale && icon == _cachedIconScale)
                return;

            _cachedUiScale = ui;
            _cachedIconScale = icon;

            _iconPx = MathF.Round(24f * ui * icon);
            _spriteScale = _iconPx / 16f;
            _lineHeight = _iconPx + 4f * ui;
            _padding = 8f * ui;
            _textScale = 0.75f * ui;
            _headerHeight = Game1.smallFont.MeasureString("Forage today:").Y * _textScale;
        }

        // -------------------------------------------------------------------------
        // Drawing
        // -------------------------------------------------------------------------

        private static void DrawForageSummary(
            SpriteBatch b,
            int mouseX,
            int mouseY,
            Dictionary<string, ForageTracker.SummaryEntry> summary)
        {
            bool remainingOnly = Config!.ShowRemainingOnly;
            bool showIcons = Config.ShowIcons;

            // ── Build lines and measure ───────────────────────────────────────────
            s_lineBuffer.Clear();
            float maxLabelWidth = 0f;

            foreach (var (displayName, entry) in summary)
            {
                string label = remainingOnly
                    ? $"{displayName}  ×{entry.Remaining}"
                    : $"{displayName}  ×{entry.Remaining} / {entry.Total}";

                float w = Game1.smallFont.MeasureString(label).X * _textScale;
                if (w > maxLabelWidth) maxLabelWidth = w;

                s_lineBuffer.Add((entry.ItemId, label));
            }

            // ── Size the box ──────────────────────────────────────────────────────
            float headerWidth = Game1.smallFont.MeasureString("Forage today:").X * _textScale;
            float contentWidth = Math.Max(headerWidth,
                (showIcons ? _iconPx + 6f * _cachedUiScale : 0f) + maxLabelWidth);

            float boxW = contentWidth + _padding * 2f;
            float boxH = _padding * 2f
                       + _headerHeight + 4f * _cachedUiScale
                       + s_lineBuffer.Count * _lineHeight;

            // ── Position the box ──────────────────────────────────────────────────
            float screenW = Game1.uiViewport.Width;
            float screenH = Game1.uiViewport.Height;

            float bx = mouseX + 36f * _cachedUiScale;
            float by = mouseY + 220f * _cachedUiScale;

            if (bx + boxW > screenW - _padding)
                bx = mouseX - boxW - 8f * _cachedUiScale;
            if (by + boxH > screenH - _padding)
                by = screenH - boxH - _padding;

            // ── Background ────────────────────────────────────────────────────────
            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                (int)bx, (int)by, (int)boxW, (int)boxH,
                Color.White,
                drawShadow: true);

            // ── Header ────────────────────────────────────────────────────────────
            float cx = bx + _padding;
            float cy = by + _padding;

            b.DrawString(
                Game1.smallFont, "Forage today:",
                new Vector2(cx, cy),
                Game1.textColor,
                0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

            cy += _headerHeight + 4f * _cachedUiScale;

            // ── Item lines ────────────────────────────────────────────────────────
            foreach (var (itemId, label) in s_lineBuffer)
            {
                if (showIcons && !string.IsNullOrEmpty(itemId))
                    TryDrawItemIcon(b, itemId, new Vector2(cx, cy));

                float textX = showIcons ? cx + _iconPx + 6f * _cachedUiScale : cx;
                float textY = cy + (_iconPx - Game1.smallFont.MeasureString(label).Y * _textScale) / 2f;

                b.DrawString(
                    Game1.smallFont, label,
                    new Vector2(textX, textY),
                    Game1.textColor,
                    0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

                cy += _lineHeight;
            }
        }

        // -------------------------------------------------------------------------
        // Icon drawing
        // -------------------------------------------------------------------------

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

            b.Draw(
                icon.Texture,
                position,
                icon.SourceRect,
                Color.White,
                0f, Vector2.Zero,
                _spriteScale,
                SpriteEffects.None,
                1f);
        }

        // -------------------------------------------------------------------------
        // Location matching
        // -------------------------------------------------------------------------

        private static string? FindBestLocationMatch(string areaComponentName)
        {
            if (Tracker == null) return null;

            if (Tracker.IsTracked(areaComponentName))
                return areaComponentName;

            if (s_knownAreaMap.TryGetValue(areaComponentName, out string? known)
                && Tracker.IsTracked(known))
                return known;

            string lower = areaComponentName.ToLowerInvariant();
            foreach (var (lowerKey, originalKey) in s_lowercaseKeyCache)
            {
                if (lower.Contains(lowerKey) || lowerKey.Contains(lower))
                    return originalKey;
            }

            return null;
        }

        private static readonly Dictionary<string, string> s_knownAreaMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Pelican Town"] = "Town",
            ["Cindersap Forest"] = "Forest",
            ["The Mountain"] = "Mountain",
            ["The Beach"] = "Beach",
            ["The Farm"] = "Farm",
            ["Mines Entrance"] = "Mine",
            ["Marnie's Ranch"] = "AnimalShop",
            ["Leah's Cottage"] = "LeahHouse",
            ["Sewer"] = "Sewer",
            ["Mutant Bug Lair"] = "BugLand",
            ["Witch's Swamp"] = "WitchSwamp",
            ["Secret Woods"] = "Woods",
            ["Railroad"] = "Railroad",
            ["Desert"] = "Desert",
            ["Ginger Island"] = "IslandSouth",
            ["Volcano"] = "Caldera",
            ["Skull Cavern Entrance"] = "SkullCave",
            ["Bathhouse"] = "BathHousePool",
        };
    }
}