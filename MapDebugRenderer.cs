using ForageTrackerMod;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ForageTrackerModSV.Debug
{
    /// <summary>
    /// Debug-only renderer used to visualize map region data directly on the
    /// live Stardew Valley map page.
    ///
    /// Region definitions are stored as normalized fractions (0.0-1.0)
    /// relative to a map image. This renderer projects those regions back
    /// into screen-space using the live map's render rectangle so that the
    /// stored region data can be visually verified.
    ///
    /// The overlay is intended to help diagnose issues such as:
    /// • Region alignment problems.
    /// • Incorrect coordinate-space calculations.
    /// • Editor/live map projection mismatches.
    /// • Incorrect map bindings.
    /// • Region hit-test discrepancies.
    ///
    /// The overlay renders:
    /// • Region boundaries.
    /// • Region labels.
    /// • Map render information.
    /// • Mouse screen coordinates.
    /// • Mouse fractional coordinates within the map image.
    ///
    /// This class is purely a development tool and should never affect
    /// gameplay logic or region detection behavior.
    /// </summary>
    public static class MapDebugRenderer
    {
        const float RegionFillAlpha = 0.35f;
        const int BorderThickness = 2;
        const float FontScale = 0.6f;
        const float DebugTextScale = 0.65f;

        /// <summary>
        /// Draws the complete debug overlay for the currently displayed map.
        ///
        /// The method determines which editor map definition applies to the
        /// active live map, projects all region bounds into screen-space,
        /// renders region visualization overlays, and displays diagnostic
        /// coordinate information used to validate region placement.
        ///
        /// The coordinate calculations performed here intentionally mirror
        /// the calculations used by region hit-testing so that rendering and
        /// interaction can be compared directly.
        /// </summary>
        /// <param name="b">Sprite batch used for rendering.</param>
        /// <param name="mapPage">The currently active map page.</param>
        /// <param name="mapRect">
        /// Screen-space rectangle occupied by the rendered map image.
        /// Region fractions are evaluated relative to this rectangle.
        /// </param>
        /// <param name="regionsByMap">
        /// Region definitions grouped by editor map key.
        /// </param>
        /// <param name="bindings">
        /// Mapping of editor map keys to live Stardew Valley map keys.
        /// Used to determine which region collection should be displayed.
        /// </param>
        public static void Draw(SpriteBatch b, IMonitor monitor, MapPage mapPage, Rectangle mapRect, Dictionary<string, List<MapRegionData>> regionsByMap, Dictionary<string, string> bindings)
        {
            if (b == null || mapPage == null)
            {
                monitor?.Log($"[Warning] Misssing key parameters for debug to work.", LogLevel.Trace);
                return;              
            }

            // Determine which editor map definition applies to the currently displayed live SDV map.
            string liveKey = MapKeyHelper.GetMapKey(mapPage);
            string editorKey = ResolveEditorKey.Resolve(liveKey, bindings);
          
            // Calculate mouse position as normalized fractions relative to
            // the map image. These values are the same coordinate space used
            // by region storage and hit-testing.
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            float relX = (mouseX - mapRect.X) / (float)mapRect.Width;
            float relY = (mouseY - mapRect.Y) / (float)mapRect.Height;

            // Draw each region as a coloured rectangle
            if (regionsByMap.TryGetValue(editorKey, out var regions))
            {
                foreach (var region in regions)
                {
                    // Convert fractions to screen pixels using the LIVE map rect (same calculation as the hit-test uses)
                    int rx = mapRect.X + (int)(region.Left * mapRect.Width);
                    int ry = mapRect.Y + (int)(region.Top * mapRect.Height);
                    int rw = (int)((region.Right - region.Left) * mapRect.Width);
                    int rh = (int)((region.Bottom - region.Top) * mapRect.Height);
                    var sr = new Rectangle(rx, ry, Math.Max(rw, 2), Math.Max(rh, 2));

                    // Semi-transparent fill
                    b.Draw(Game1.fadeToBlackRect, sr, region.Color * RegionFillAlpha);

                    // Bright border
                    DrawDebugBorder(b, sr, region.Color, BorderThickness);

                    // Region name
                    if (sr.Width > 20)
                        b.DrawString(Game1.smallFont, region.Name, new Vector2(sr.X + 3, sr.Y + 3), Color.White, 0f, Vector2.Zero, FontScale, SpriteEffects.None, 1f);
                }
            }

            DrawDebugText(b,liveKey,editorKey,mapRect,mouseX,mouseY,relX,relY);
        }
        /// <summary>
        /// Draws a rectangular outline using solid-color quads.
        ///
        /// This helper is used by the debug overlay to make region boundaries
        /// easier to identify and compare against the underlying map image.
        /// </summary>
        /// <param name="b">Sprite batch used for rendering.</param>
        /// <param name="r">Rectangle to outline.</param>
        /// <param name="c">Border color.</param>
        /// <param name="t">Border thickness in pixels.</param>
        static void DrawDebugBorder(SpriteBatch b, Rectangle r, Color c, int t)
        {
            // Clamping avoid the border case where: rectangle width/height is smaller than the thickness so it could technically draw negative dimensions.
            int clampedWidth = Math.Max(r.Width, t);
            int clampedHeight = Math.Max(r.Height, t);

            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, clampedWidth, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Bottom - t, clampedWidth, t), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.X, r.Y, t, clampedHeight), c);
            b.Draw(Game1.fadeToBlackRect, new Rectangle(r.Right - t, r.Y, t, clampedHeight), c);
        }

        /// <summary>
        /// Draws diagnostic text describing the current map projection state.
        ///
        /// The displayed information includes:
        /// • The active live map key.
        /// • The resolved editor map key.
        /// • The current map render rectangle.
        /// • Mouse screen coordinates.
        /// • Mouse coordinates expressed as normalized map fractions.
        ///
        /// This information is useful when validating that editor-generated
        /// region data aligns correctly with the live in-game map.
        /// </summary>
        /// <param name="b">Sprite batch used for rendering.</param>
        /// <param name="liveKey">
        /// The active Stardew Valley map key.
        /// </param>
        /// <param name="editorKey">
        /// The editor map key resolved from the active live map.
        /// </param>
        /// <param name="mapRect">
        /// The screen-space rectangle occupied by the rendered map image.
        /// </param>
        /// <param name="mouseX">
        /// Current mouse X coordinate in screen-space.
        /// </param>
        /// <param name="mouseY">
        /// Current mouse Y coordinate in screen-space.
        /// </param>
        /// <param name="relX">
        /// Mouse X position expressed as a normalized map fraction.
        /// </param>
        /// <param name="relY">
        /// Mouse Y position expressed as a normalized map fraction.
        /// </param>
        static void DrawDebugText(SpriteBatch b, string liveKey, string editorKey, Rectangle mapRect, int mouseX, int mouseY, float relX, float relY)
        {
            // Draw fractional mouse position in the top-left corner of the map
            string debugText =
                $"DEBUG MAP: live={liveKey}  editor={editorKey}\n" +
                $"mapRect: x={mapRect.X} y={mapRect.Y} " +
                $"w={mapRect.Width} h={mapRect.Height}\n" +
                $"mouse: screen=({mouseX},{mouseY})  frac=({relX:F3},{relY:F3})";

            // Background panel for readability.
            var textSz = Game1.smallFont.MeasureString(debugText) * 0.6f;
            b.Draw(Game1.fadeToBlackRect, new Rectangle(mapRect.X + 4, mapRect.Y + 4, (int)textSz.X + 8, (int)textSz.Y + 8), Color.Black * 0.7f);

            b.DrawString(Game1.smallFont, debugText, new Vector2(mapRect.X + 8, mapRect.Y + 8), Color.Yellow, 0f, Vector2.Zero, DebugTextScale, SpriteEffects.None, 1f);
        }
    }
}
