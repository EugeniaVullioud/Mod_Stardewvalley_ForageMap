namespace ForageTrackerMod
{
    public sealed class ModConfig
    {
        /// <summary>Whether the forage tracker overlay is enabled.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Show forage icons on the map tooltip in addition to text.</summary>
        public bool ShowIcons { get; set; } = true;

        /// <summary>Show a count of remaining (un-picked) forageables per area.</summary>
        public bool ShowRemainingOnly { get; set; } = false;

        /// <summary>Scale multiplier for icons drawn in the tooltip (1.0 = auto-scale to display).</summary>
        public float IconScale { get; set; } = 1.0f;
    }
}