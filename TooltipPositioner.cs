using Microsoft.Xna.Framework;

namespace ForageTrackerMod
{
    /// <summary>
    /// Computes and caches the best tooltip offset from the mouse cursor.
    ///
    /// CACHING STRATEGY
    /// ────────────────
    /// The spiral search is expensive (~864 trig + scoring ops). But the result
    /// only depends on variables that rarely change:
    ///
    ///   • Box size (width × height)   — changes when forage content changes
    ///   • Screen size                  — changes on window resize
    ///   • UI scale                     — changes in options
    ///   • Vanilla hover rect size      — changes when hovered location changes
    ///   • Vanilla hover rect position  — changes with MOUSE (so we cannot cache
    ///                                    its absolute position, only its SIZE
    ///                                    and its offset relative to the mouse)
    ///
    /// Because the vanilla hover rect moves with the mouse, we store its
    /// MOUSE-RELATIVE offset and size, rebuild an "occupied" rect at query time
    /// using the current mouse position, then apply the cached offset.
    ///
    /// This means the spiral runs only when the box size, screen, uiScale, or
    /// hovered location changes — typically zero times per second while the
    /// player holds the mouse still over a region.
    ///
    /// THREAD SAFETY: all access is from the game update thread; no locking needed.
    /// </summary>
    public static class TooltipPositioner
    {
        // ── Cached state ──────────────────────────────────────────────────────

        /// <summary>Offset to add to mouse position to get top-left of tooltip.</summary>
        private static Vector2 _cachedOffset;

        // Cache keys — invalidate when any of these change.
        private static int _cachedBoxW;
        private static int _cachedBoxH;
        private static int _cachedScreenW;
        private static int _cachedScreenH;
        private static float _cachedUiScale;
        // Vanilla hover rect in mouse-relative coords (offset + size).
        private static int _cachedVanillaRelX;
        private static int _cachedVanillaRelY;
        private static int _cachedVanillaW;
        private static int _cachedVanillaH;

        private static bool _hasCached;

        /// <summary>
        /// Incremented each time the spiral actually runs (cache miss).
        /// Read by MapTooltipDrawer for the perf debug log.
        /// Reset to 0 when read externally — caller owns the reset window.
        /// </summary>
        public static int SpiralRunCount { get; private set; }

        // ── Spiral search parameters (same as before, centralised here) ───────

        private const int SpiralStartRadius = 48;
        private const int SpiralEndRadius = 600;
        private const int SpiralRadiusStep = 24;
        private const int SpiralAngleStep = 10;   // degrees

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the screen-space top-left position for the tooltip box.
        ///
        /// If the display variables haven't changed since the last call, this
        /// is a single addition (mousePos + cachedOffset) — no trig, no scoring.
        ///
        /// When anything relevant changes the spiral runs once and the new
        /// offset is cached for all subsequent frames with the same state.
        /// </summary>
        public static Point GetPosition(int mouseX, int mouseY, int boxW, int boxH, Rectangle vanillaHoverRect,   // absolute screen rect, may be Empty
            int screenW, int screenH, float uiScale)
        {
            // Compute vanilla rect in mouse-relative coords.
            // We store relative coords so the cache stays valid as the mouse moves.
            int vanRelX = 0, vanRelY = 0, vanW = 0, vanH = 0;
            if (!vanillaHoverRect.IsEmpty)
            {
                vanRelX = vanillaHoverRect.X - mouseX;
                vanRelY = vanillaHoverRect.Y - mouseY;
                vanW = vanillaHoverRect.Width;
                vanH = vanillaHoverRect.Height;
            }

            bool dirty =
                !_hasCached ||
                boxW != _cachedBoxW ||
                boxH != _cachedBoxH ||
                screenW != _cachedScreenW ||
                screenH != _cachedScreenH ||
                uiScale != _cachedUiScale ||
                vanRelX != _cachedVanillaRelX ||
                vanRelY != _cachedVanillaRelY ||
                vanW != _cachedVanillaW ||
                vanH != _cachedVanillaH;

            if (dirty)
            {
                SpiralRunCount++;
                var occupied = new List<Rectangle>(2);
                if (vanW > 0 && vanH > 0)
                    occupied.Add(new Rectangle(vanRelX, vanRelY, vanW, vanH));

                Vector2 offset = RunSpiral(originX: 0, originY: 0, boxW, boxH, occupied, screenW, screenH);

                _cachedOffset = offset;
                _cachedBoxW = boxW;
                _cachedBoxH = boxH;
                _cachedScreenW = screenW;
                _cachedScreenH = screenH;
                _cachedUiScale = uiScale;
                _cachedVanillaRelX = vanRelX;
                _cachedVanillaRelY = vanRelY;
                _cachedVanillaW = vanW;
                _cachedVanillaH = vanH;
                _hasCached = true;
            }

            // Apply cached offset to actual mouse position, then clamp to screen.
            int x = Math.Clamp(mouseX + (int)_cachedOffset.X, 0, screenW - boxW);
            int y = Math.Clamp(mouseY + (int)_cachedOffset.Y, 0, screenH - boxH);
            return new Point(x, y);
        }

        /// <summary>
        /// Call when display variables that affect box size change
        /// (e.g. uiScale, config, or forage content) to force a re-solve
        /// on the next frame. Not required for mouse movement.
        /// </summary>
        public static void Invalidate() => _hasCached = false;

        // ── Spiral solver ─────────────────────────────────────────────────────

        /// <summary>
        /// Runs the spiral search with the given origin treated as the mouse
        /// position and occupied rects expressed relative to that origin.
        /// Returns the best offset (candidate top-left relative to origin).
        /// </summary>
        private static Vector2 RunSpiral(int originX, int originY, int boxW, int boxH, List<Rectangle> occupied, int screenW, int screenH)
        {
            Rectangle best = new Rectangle(originX + SpiralStartRadius, originY + SpiralStartRadius, boxW, boxH);
            float bestScore = float.MinValue;
            Point cursor = new(originX, originY);

            for (int radius = SpiralStartRadius; radius <= SpiralEndRadius; radius += SpiralRadiusStep)
            {
                for (int angle = 0; angle < 360; angle += SpiralAngleStep)
                {
                    float rad = MathF.PI * angle / 180f;
                    int cx = originX + (int)(MathF.Cos(rad) * radius);
                    int cy = originY + (int)(MathF.Sin(rad) * radius);

                    var candidate = new Rectangle(cx, cy, boxW, boxH);
                    float score = Score(candidate, cursor, occupied, screenW, screenH);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }
            }

            return new Vector2(best.X - originX, best.Y - originY);
        }

        private static float Score(Rectangle candidate, Point cursor, List<Rectangle> occupied, int screenW, int screenH)
        {
            float score = 0f;

            foreach (var r in occupied)
            {
                // Rebuild occupied at absolute coords relative to cursor as origin.
                var abs = new Rectangle( cursor.X + r.X, cursor.Y + r.Y, r.Width, r.Height);
                var isect = Rectangle.Intersect(candidate, abs);
                if (!isect.IsEmpty)
                    score -= isect.Width * isect.Height * 1000f;
            }

            if (candidate.Left < 0) score -= 50000f;
            if (candidate.Top < 0) score -= 50000f;
            if (candidate.Right > screenW) score -= 50000f;
            if (candidate.Bottom > screenH) score -= 50000f;

            float dx = candidate.Center.X - cursor.X;
            float dy = candidate.Center.Y - cursor.Y;
            score -= MathF.Sqrt(dx * dx + dy * dy) * 0.5f;

            if (candidate.X > cursor.X) score += 250f;
            if (candidate.Y > cursor.Y) score += 100f;

            return score;
        }
    }
}
