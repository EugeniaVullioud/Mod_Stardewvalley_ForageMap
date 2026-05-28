using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

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
        // Icon cache
        // -------------------------------------------------------------------------

        private sealed class IconData
        {
            public Texture2D Texture { get; init; } = null!;
            public Rectangle SourceRect { get; init; }
        }

        private static readonly Dictionary<string, IconData?> s_iconCache = new();

        // -------------------------------------------------------------------------
        // Aux constants
        // -------------------------------------------------------------------------
        private static string _headerText = "Today:";

        // -------------------------------------------------------------------------
        // Scale cache — recomputed only when uiScale or IconScale changes
        // -------------------------------------------------------------------------

        private static float _cachedUiScale = -1f;
        private static float _cachedIconScale = -1f;

        private static float _iconPx;        // rendered icon size in screen pixels
        private static float _spriteScale;   // iconPx / 16  (sprite source is 16×16)
        private static float _lineHeight;    // height of one item row
        private static float _padding;       // inner padding from box edge to content
        private static float _textScale;     // DrawString scale factor
        private static float _headerHeight;  // measured height of the header line

        // -------------------------------------------------------------------------
        // Line buffer — pre-allocated, cleared each frame
        // -------------------------------------------------------------------------

        private static readonly List<(string ItemId, string Label)> s_lineBuffer = new(16);

        // -------------------------------------------------------------------------
        // Location key cache — pre-lowercased, rebuilt once per day
        // -------------------------------------------------------------------------

        private static readonly Dictionary<string, string> s_lowercaseKeyCache = new();

        // -------------------------------------------------------------------------
        // Public entry point
        // -------------------------------------------------------------------------

        public static void OnRenderedActiveMenu(SpriteBatch b)
        {
            try
            {
                if (Config == null || !Config.Enabled || Tracker == null)
                    return;

                if (Game1.activeClickableMenu is not GameMenu gameMenu)
                    return;
                if (gameMenu.currentTab != GameMenu.mapTab)
                    return;
                if (gameMenu.GetCurrentPage() is not MapPage mapPage)
                    return;

                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();


                // ── Find hovered area ─────────────────────────────────────────────
                // mapPage.points contains clickable zones for specific landmarks.
                // Broader regions (Forest, Desert, Ginger Island) have NO clickable
                // component — we detect them via our own manually-defined rectangles
                // expressed as fractions of the map image bounds, so they scale
                // correctly at any resolution.
                string? hoveredName = null;

                foreach (var point in mapPage.points)
                {
                    if (point.Value.containsPoint(mouseX, mouseY))
                    {
                        hoveredName = point.Value.name;
                        break;
                    }
                }

                if (hoveredName == null)
                    hoveredName = GetRegionAtPoint(mapPage, mouseX, mouseY);

                if (hoveredName == null) return;

                // ── Resolve all location names for this map area ──────────────────
                var locationNames = ResolveLocationNames(hoveredName);
                if (locationNames.Count == 0) return;

                var merged = MergeSummaries(locationNames);
                if (merged.Count == 0) return;

                RefreshScaleCache();

                float vanillaTooltipH = MeasureVanillaTooltipHeight(hoveredName);
                DrawForageSummary(b, mouseX, mouseY, vanillaTooltipH, merged);
            }
            catch (Exception ex)
            {
                Monitor?.Log($"[MapTooltipDrawer] Draw error: {ex}", LogLevel.Error);
            }
        }

        // -------------------------------------------------------------------------
        // Vanilla tooltip height estimation
        // -------------------------------------------------------------------------

        /// <summary>
        /// Estimates the pixel height of the vanilla map area tooltip so our box
        /// can be docked directly below it instead of using a hardcoded offset.
        ///
        /// The vanilla tooltip draws:
        ///   • The area name in Game1.dialogueFont
        ///   • A blank line gap
        ///   • The description in Game1.smallFont (may be empty)
        ///   • Top + bottom padding of ~32px each (the drawTextureBox border)
        ///
        /// All of this is in UI-space pixels (already scaled by the game's UI zoom).
        /// </summary>
        private static float MeasureVanillaTooltipHeight(string areaLabel)
        {
            // The game draws the tooltip at a fixed position near the cursor.
            // We only need its HEIGHT to know where our box should start.
            // dialogueFont line height ≈ 64px at uiScale 1.0
            float titleH = Game1.dialogueFont.MeasureString(areaLabel).Y;

            // Top border + bottom border of the nine-patch box (16px each side at scale 1)
            float border = 32f * _cachedUiScale;

            // Small gap between vanilla box and ours
            float gap = 8f * _cachedUiScale;

            return titleH + border + gap;
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
            _lineHeight = _iconPx + 6f * ui;

            // Padding must be large enough to clear the drawTextureBox border (16px
            // per side at uiScale 1.0) plus a comfortable inner margin.
            _padding = 20f * ui;

            _textScale = 0.75f * ui;
            _headerHeight = Game1.smallFont.MeasureString(_headerText).Y * _textScale;
        }

        // -------------------------------------------------------------------------
        // Drawing
        // -------------------------------------------------------------------------

        private static void DrawForageSummary(
            SpriteBatch b,
            int mouseX,
            int mouseY,
            float vanillaTooltipH,
            Dictionary<string, ForageTracker.SummaryEntry> summary)
        {
            DisplayMode mode = Config!.Display;
            bool showIcons = mode is DisplayMode.Both or DisplayMode.IconOnly;
            bool showText = mode is DisplayMode.Both or DisplayMode.TextOnly;
            bool remainOnly = Config.ShowRemainingOnly;

            // ── Build line buffer and measure ─────────────────────────────────────
            s_lineBuffer.Clear();
            float maxLabelWidth = 0f;

            foreach (var (displayName, entry) in summary)
            {
                // Count is always shown — it's useful even when the name is hidden.
                // In IconOnly mode we suppress the item name but keep the number.
                string count = remainOnly
                    ? $"×{entry.Remaining}"
                    : $"×{entry.Remaining} / {entry.Total}";

                string label = showText
                    ? $"{displayName}  {count}"
                    : count;                         // IconOnly: just "×2 / 5"

                float w = Game1.smallFont.MeasureString(label).X * _textScale;
                if (w > maxLabelWidth) maxLabelWidth = w;

                s_lineBuffer.Add((entry.ItemId, label));
            }

            // ── Size the box ──────────────────────────────────────────────────────
            float iconColumnW = showIcons ? _iconPx + 6f * _cachedUiScale : 0f;
            float headerWidth = Game1.smallFont.MeasureString(_headerText).X * _textScale;
            float contentWidth = Math.Max(headerWidth, iconColumnW + maxLabelWidth);
            float lineH = _lineHeight;

            float boxW = contentWidth + _padding * 2f;
            float boxH = _padding * 2f
                       + _headerHeight + 4f * _cachedUiScale  // header row
                       + s_lineBuffer.Count * lineH;

            // ── Position: directly below the vanilla tooltip ───────────────────────
            // Vanilla tooltip appears just above and to the right of the cursor.
            // Its top is approximately at mouseY - vanillaTooltipH.
            // We want our box to start just below its bottom edge.
            float screenW = Game1.uiViewport.Width;
            float screenH = Game1.uiViewport.Height;

            // Horizontal: match the vanilla tooltip's x (cursor + small offset)
            float bx = mouseX + 28f * _cachedUiScale;
            // Vertical: vanilla tooltip bottom + small gap
            float by = mouseY + vanillaTooltipH;

            // Clamp to screen edges
            if (bx + boxW > screenW - _padding)
                bx = screenW - boxW - _padding;
            if (bx < _padding)
                bx = _padding;
            if (by + boxH > screenH - _padding)
                by = mouseY - vanillaTooltipH - boxH - 4f * _cachedUiScale;
            if (by < _padding)
                by = _padding;

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
                Game1.smallFont, _headerText,
                new Vector2(cx, cy),
                Game1.textColor,
                0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

            cy += _headerHeight + 4f * _cachedUiScale;

            // ── Item lines ────────────────────────────────────────────────────────
            foreach (var (itemId, label) in s_lineBuffer)
            {
                // Icon
                if (showIcons && !string.IsNullOrEmpty(itemId))
                    TryDrawItemIcon(b, itemId, new Vector2(cx, cy));

                // Label (count always present; name only in Both/TextOnly modes)
                float textX = showIcons ? cx + _iconPx + 6f * _cachedUiScale : cx;
                float textH = Game1.smallFont.MeasureString(label).Y * _textScale;
                float textY = showIcons ? cy + (_iconPx - textH) / 2f : cy;

                b.DrawString(
                    Game1.smallFont, label,
                    new Vector2(textX, textY),
                    Game1.textColor,
                    0f, Vector2.Zero, _textScale, SpriteEffects.None, 1f);

                cy += lineH;
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

            b.Draw(icon.Texture, position, icon.SourceRect,
                Color.White, 0f, Vector2.Zero, _spriteScale, SpriteEffects.None, 1f);
        }

        // -------------------------------------------------------------------------
        // Location resolution — one area label → multiple location names
        // -------------------------------------------------------------------------

        /// <summary>
        /// Returns all internal location names that correspond to the given map
        /// area component name.  A single map area can contain forageables from
        /// several internal locations (e.g. "Pelican Town" covers both "Town" and
        /// nearby sub-areas).
        /// </summary>
        private static List<string> ResolveLocationNames(string areaComponentName)
        {
            var result = new List<string>(4);

            // Direct match first
            if (Tracker!.IsTracked(areaComponentName))
                result.Add(areaComponentName);

            // Known multi-location areas
            if (s_areaToLocations.TryGetValue(areaComponentName, out var known))
            {
                foreach (var loc in known)
                    if (Tracker.IsTracked(loc) && !result.Contains(loc))
                        result.Add(loc);
            }

            // Fuzzy fallback for modded locations
            if (result.Count == 0)
            {
                string lower = areaComponentName.ToLowerInvariant();
                foreach (var (lowerKey, originalKey) in s_lowercaseKeyCache)
                {
                    if (lower.Contains(lowerKey) || lowerKey.Contains(lower))
                        result.Add(originalKey);
                }
            }

            return result;
        }

        /// <summary>
        /// Merges summaries from multiple locations into one dictionary.
        /// If the same item appears in both Town and a sub-location, counts are combined.
        /// </summary>
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
                        // First occurrence — add a new mutable entry
                        merged[displayName] = new ForageTracker.SummaryEntry
                        {
                            ItemId = entry.ItemId,
                            Total = entry.Total,
                            Remaining = entry.Remaining
                        };
                    }
                    else
                    {
                        // Same item type found in another sub-location — accumulate
                        existing.Total += entry.Total;
                        existing.Remaining += entry.Remaining;
                    }
                }
            }

            return merged;
        }

        // -------------------------------------------------------------------------
        // Location key cache
        // -------------------------------------------------------------------------

        public static void RebuildLocationKeyCache()
        {
            s_lowercaseKeyCache.Clear();
            if (Tracker == null) return;
            foreach (var name in Tracker.TrackedLocationNames)
                s_lowercaseKeyCache[name.ToLowerInvariant()] = name;
        }

        // -------------------------------------------------------------------------
        // Area → location(s) map
        // Each map area label maps to one OR MORE internal location names.
        // -------------------------------------------------------------------------

        private static readonly Dictionary<string, string[]> s_areaToLocations =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // ── Points (named clickable zones) ────────────────────────────────────
                ["Pelican Town"] = new[] { "Town", "SeedShop", "Saloon", "Hospital",
                                            "ManorHouse", "JoshHouse", "HaleyHouse",
                                            "SamHouse", "Blacksmith", "Museum",
                                            "ElliottHouse", "ScienceHouse" },
                ["Cindersap Forest"] = new[] { "Forest", "LeahHouse", "WizardHouse",
                                            "WizardHouseBasement", "Tent" },
                ["The Mountain"] = new[] { "Mountain", "ScienceHouse", "AdventureGuild",
                                            "Carpenter" },
                ["The Beach"] = new[] { "Beach", "FishShop", "ElliottHouse" },
                ["The Farm"] = new[] { "Farm", "FarmHouse", "Cellar", "Greenhouse" },
                ["Mines Entrance"] = new[] { "Mine" },
                ["Secret Woods"] = new[] { "Woods" },
                ["Railroad"] = new[] { "Railroad", "BathHouseEntry",
                                            "BathHousePool", "BathHouseLocker",
                                            "WitchSwamp", "WitchHut", "WitchWarpCave" },
                ["Desert"] = new[] { "Desert", "SandyHouse", "SkullCave" },
                ["Ginger Island"] = new[] { "IslandSouth", "IslandNorth", "IslandEast",
                                            "IslandWest", "IslandFarmHouse",
                                            "IslandFieldOffice", "IslandShrine",
                                            "Caldera", "VolcanoDungeon0" },
                ["Mutant Bug Lair"] = new[] { "BugLand" },
                ["Witch's Swamp"] = new[] { "WitchSwamp", "WitchHut" },
                ["Sewer"] = new[] { "Sewer" },
                ["Bathhouse"] = new[] { "BathHousePool", "BathHouseEntry",
                                            "BathHouseLocker" },
                ["Marnie's Ranch"] = new[] { "AnimalShop" },
                ["Skull Cavern Entrance"] = new[] { "SkullCave" },
                ["Volcano"] = new[] { "Caldera" },

                // ── Descriptions (broader hover areas — name strings from mapPage.descriptions) ──
                // These are the names stored on the ClickableComponent inside descriptions[].
                // They may differ from the human-readable label shown in the tooltip.
                ["forest"] = new[] { "Forest", "LeahHouse", "WizardHouse",
                                            "WizardHouseBasement", "Tent" },
                ["town"] = new[] { "Town" },
                ["mountain"] = new[] { "Mountain" },
                ["beach"] = new[] { "Beach", "FishShop" },
                ["farm"] = new[] { "Farm", "FarmHouse", "Greenhouse" },
                ["woods"] = new[] { "Woods" },
                ["railroad"] = new[] { "Railroad" },
                ["desert"] = new[] { "Desert", "SandyHouse" },
                ["island"] = new[] { "IslandSouth", "IslandNorth", "IslandEast",
                                            "IslandWest", "IslandFarmHouse",
                                            "IslandFieldOffice", "IslandShrine" },
                ["islandsouth"] = new[] { "IslandSouth" },
                ["islandnorth"] = new[] { "IslandNorth" },
                ["islandeast"] = new[] { "IslandEast" },
                ["islandwest"] = new[] { "IslandWest", "IslandFarmHouse" },
                ["volcano"] = new[] { "Caldera", "VolcanoDungeon0" },
                ["sewer"] = new[] { "Sewer" },
                ["witchswamp"] = new[] { "WitchSwamp", "WitchHut" },
                ["bugland"] = new[] { "BugLand" },
            };

        // -------------------------------------------------------------------------
        // Manual region detection
        // -------------------------------------------------------------------------

        /// <summary>
        /// Checks whether the mouse is inside any of our manually-defined map
        /// regions. Returns the region key (matched against s_areaToLocations) or
        /// null if the cursor is not in any region.
        ///
        /// Regions are defined as fractions of the map image rectangle so they
        /// scale automatically with every resolution and UI zoom level.
        ///
        /// How to read the tuples: (left%, top%, right%, bottom%) where 0,0 is the
        /// top-left corner of the map image and 1,1 is the bottom-right.
        /// These values were measured against the vanilla Stardew Valley world map.
        /// </summary>
        private static string? GetRegionAtPoint(MapPage mapPage, int mouseX, int mouseY)
        {
            // The map image is drawn inside mapPage's bounds.
            // mapPage.xPositionOnScreen / yPositionOnScreen give the top-left corner;
            // mapPage.width / height give the total dimensions.
            float mapLeft = mapPage.xPositionOnScreen;
            float mapTop = mapPage.yPositionOnScreen;
            float mapW = mapPage.width;
            float mapH = mapPage.height;

            float relX = (mouseX - mapLeft) / mapW;
            float relY = (mouseY - mapTop) / mapH;

            // Early out if cursor is outside the map entirely.
            if (relX < 0f || relX > 1f || relY < 0f || relY > 1f)
                return null;

            foreach (var (name, region) in s_mapRegions)
            {
                if (relX >= region.Left && relX <= region.Right &&
                    relY >= region.Top && relY <= region.Bottom)
                    return name;
            }

            return null;
        }

        // Region name -> (Left, Top, Right, Bottom) as fractions of the map image.
        // These cover areas that have no ClickableComponent in mapPage.points.
        // Points-based zones (Pelican Town, buildings, etc.) are handled first so
        // there is no overlap conflict — these regions are the "background" areas.
        private static readonly (string Name, RectangleF Region)[] s_mapRegions =
        {
            // Broader areas — checked after points, so buildings inside them
            // don't need to be excluded here.
            ("Cindersap Forest",  new RectangleF(0.00f, 0.52f, 0.30f, 0.88f)),
            ("The Mountain",      new RectangleF(0.35f, 0.00f, 0.75f, 0.28f)),
            ("The Beach",         new RectangleF(0.35f, 0.72f, 0.75f, 1.00f)),
            ("The Farm",          new RectangleF(0.00f, 0.20f, 0.35f, 0.55f)),
            ("Secret Woods",      new RectangleF(0.00f, 0.42f, 0.14f, 0.55f)),
            ("Railroad",          new RectangleF(0.52f, 0.00f, 0.80f, 0.18f)),
            ("Desert",            new RectangleF(0.00f, 0.00f, 0.30f, 0.20f)),
            ("Ginger Island",     new RectangleF(0.78f, 0.00f, 1.00f, 0.55f)),
            ("Pelican Town",      new RectangleF(0.30f, 0.42f, 0.60f, 0.72f)),
        };

        // Minimal RectangleF struct — avoids a System.Drawing dependency.
        private readonly struct RectangleF
        {
            public float Left { get; }
            public float Top { get; }
            public float Right { get; }
            public float Bottom { get; }

            public RectangleF(float left, float top, float right, float bottom)
            {
                Left = left; Top = top; Right = right; Bottom = bottom;
            }
        }
    }
}