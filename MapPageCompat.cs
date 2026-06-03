using StardewValley.Menus;

namespace ForageTrackerModSV.Compatibility
{
    /// <summary>
    /// Provides a compatibility layer around MapPage members used by the mod.
    ///
    /// Why this exists:
    /// Stardew Valley UI classes occasionally change between game versions.
    /// Rather than allowing the rest of the mod to directly access MapPage
    /// fields and properties, all MapPage-specific behavior is centralized
    /// here.
    ///
    /// This provides several benefits:
    /// • Reduces coupling between the mod and SDV internals.
    /// • Makes future SDV update fixes easier.
    /// • Prevents MapTooltipDrawer from depending on implementation details.
    /// • Allows defensive fallbacks if SDV changes behavior.
    ///
    /// If a future game update changes how hover text or map points are
    /// exposed, this should ideally be the only file requiring modification.
    /// </summary>
    internal static class MapPageCompat
    {
        /// <summary>
        /// Safely retrieves the current hover text displayed by a MapPage.
        ///
        /// This is used when positioning the forage tooltip so it can avoid
        /// overlapping Stardew Valley's own tooltip.
        ///
        /// The method returns an empty string if:
        /// • The page is null.
        /// • The hover text is unavailable.
        /// • A future SDV update changes the implementation.
        ///
        /// Returning an empty string allows callers to gracefully continue
        /// without risking crashes.
        /// </summary>
        /// <param name="page">
        /// The MapPage currently being displayed.
        /// </param>
        /// <returns>
        /// The active hover text, or an empty string if unavailable.
        /// </returns>
        public static string GetHoverText(MapPage page)
        {
            if(page == null) return string.Empty;

            try
            {
                // MapPage.hoverText is often internal; we catch exceptions
                // to avoid crashes if the field changes.
                return page.hoverText ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        /// <summary>
        /// Safely retrieves all clickable map points defined by the MapPage.
        ///
        /// These points represent vanilla map locations such as shops,
        /// landmarks, entrances, and other hoverable regions.
        ///
        /// The forage tooltip uses these points as a fallback when no custom
        /// region definition matches the cursor position.
        ///
        /// The returned collection is detached from the underlying MapPage
        /// collection to avoid issues if SDV modifies the collection during
        /// iteration.
        ///
        /// Returning an empty collection instead of throwing exceptions helps
        /// keep the tooltip system resilient to future SDV changes.
        /// </summary>
        /// <param name="page">
        /// The MapPage currently being displayed.
        /// </param>
        /// <returns>
        /// A collection of clickable map points. If none are available,
        /// an empty collection is returned.
        /// </returns>
        public static IEnumerable<ClickableComponent> GetPoints(MapPage page)
        {
            if (page == null) return Array.Empty<ClickableComponent>();

            try
            {
                if (page.points != null)
                {
                    var list = new List<ClickableComponent>();
                    foreach (var point in page.points.Values)
                        if (point != null) list.Add(point);

                    return list;
                }
            }
            catch { }   // Simply ignored

            return Array.Empty<ClickableComponent>();
        }
    }
}
