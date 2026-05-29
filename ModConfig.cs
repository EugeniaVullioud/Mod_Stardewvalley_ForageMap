namespace ForageTrackerMod
{
    /// <summary>Controls what is shown per forage line in the tooltip.</summary>
    public enum DisplayMode
    {
        /// <summary>Show item icon and text label.</summary>
        Both,
        /// <summary>Show item icon only — compact view.</summary>
        IconOnly,
        /// <summary>Show text label only — no icons.</summary>
        TextOnly
    }
    public sealed class ModConfig
    {
        /// <summary>Whether the forage tracker overlay is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>What to render per forage entry: Both, IconOnly, or TextOnly.</summary>
        public DisplayMode Display { get; set; } = DisplayMode.Both;

        /// <summary>Show a count of remaining (un-picked) forageables per area.</summary>
        public bool ShowRemainingOnly { get; set; } = true;

        /// <summary>Scale multiplier for icons drawn in the tooltip (1.0 = auto-scale to display).</summary>
        public float IconScale { get; set; } = 1.0f;
    }
}