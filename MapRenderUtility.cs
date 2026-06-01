using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ForageTrackerModSV
{
    /// <summary>
    /// Represents the texture and destination information required to draw
    /// overlays on top of the in-game world map.
    ///
    /// The destination rectangle corresponds to the actual rendered map area
    /// within the active <see cref="MapPage"/>, not necessarily the page's
    /// full menu bounds.
    /// </summary>
    public readonly struct MapRenderData
    {
        /// <summary>
        /// Texture used as the rendering reference for map overlays.
        /// </summary>
        public readonly Texture2D Texture;

        /// <summary>
        /// Source region within <see cref="Texture"/>.
        /// May be empty when the full texture is implied.
        /// </summary>
        public readonly Rectangle SourceRect;

        /// <summary>
        /// Screen-space rectangle occupied by the rendered world map.
        /// Overlay elements should be positioned relative to this area.
        /// </summary>
        public readonly Rectangle DestinationRect;

        public MapRenderData(Texture2D texture, Rectangle sourceRect, Rectangle destinationRect)
        {
            Texture         = texture;
            SourceRect      = sourceRect;
            DestinationRect = destinationRect;
        }
    }

    /// <summary>
    /// Helper methods for determining where the Stardew Valley world map
    /// is rendered on screen.
    ///
    /// Rather than relying on menu bounds or private MapPage fields,
    /// this utility calculates the true visible map area by examining
    /// the map region's base texture and all overlay textures currently
    /// used by the page.
    ///
    /// This produces a rectangle that matches the actual rendered map
    /// contents and can be used for accurate overlay positioning,
    /// tooltip placement, and editor calibration.
    /// </summary>
    public static class MapRenderUtility
    {
        /// <summary>
        /// Computes the bounding rectangle of all textures that make up the
        /// currently displayed map.
        ///
        /// The returned rectangle is in screen coordinates and represents
        /// the union of:
        /// <list type="bullet">
        /// <item><description>All map-area textures.</description></item>
        /// <item><description>The region's base map texture.</description></item>
        /// </list>
        ///
        /// This rectangle corresponds to the actual visible map area rather
        /// than the surrounding menu frame.
        /// </summary>
        public static Rectangle ComputeActualMapRect(MapPage page)
        {
            Rectangle result = Rectangle.Empty;

            bool first = true;

            var mapAreas = page.mapAreas;

            foreach (var area in mapAreas)
            {
                foreach (var texture in area.GetTextures())
                {
                    Rectangle r =
                        texture.GetOffsetMapPixelArea(
                            page.mapBounds.X,
                            page.mapBounds.Y);

                    if (first)
                    {
                        result = r;
                        first = false;
                    }
                    else
                    {
                        result = Rectangle.Union(result, r);
                    }
                }
            }

            var baseTexture = page.mapRegion.GetBaseTexture();

            if (baseTexture != null)
            {
                Rectangle r =
                    baseTexture.GetOffsetMapPixelArea(
                        page.mapBounds.X,
                        page.mapBounds.Y);

                result = first
                    ? r
                    : Rectangle.Union(result, r);
            }

            return result;
        }
        /// <summary>
        /// Retrieves rendering information describing the currently displayed
        /// world map.
        ///
        /// The returned <see cref="MapRenderData"/> contains:
        /// <list type="bullet">
        /// <item><description>The reference texture used for overlay rendering.</description></item>
        /// <item><description>The calculated on-screen map bounds.</description></item>
        /// </list>
        ///
        /// This method currently always succeeds and returns the computed
        /// map rectangle derived from <see cref="ComputeActualMapRect"/>.
        /// </summary>
        public static bool TryGetMapRenderData(MapPage page, out MapRenderData data)
        {
            Rectangle actualMapRect =
             ComputeActualMapRect(page);

            data = new MapRenderData(
                Game1.mouseCursors,
                Rectangle.Empty,
                actualMapRect);

            return true;
        }
    }
}
