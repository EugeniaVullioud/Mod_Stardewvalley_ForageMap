using Microsoft.Xna.Framework;
using System.Collections.Generic;

namespace ForageTrackerMod;

/// <summary>
/// A single named map region defined by the player.
/// Stored as fractions of the map image (0.0–1.0) so it scales with any resolution.
/// </summary>
public sealed class MapRegionData
{
    /// <summary>Display name shown in the forage tooltip header (e.g. "Cindersap Forest").</summary>
    public string Name { get; set; } = "New Area";

    /// <summary>
    /// Internal SDV location names that belong to this region.
    /// Forage from ALL of these is merged and shown when hovering this region.
    /// </summary>
    public List<string> Locations { get; set; } = new();

    /// <summary>Left edge as a fraction of map width (0.0 = left, 1.0 = right).</summary>
    public float Left { get; set; } = 0.3f;

    /// <summary>Top edge as a fraction of map height (0.0 = top, 1.0 = bottom).</summary>
    public float Top { get; set; } = 0.3f;

    /// <summary>Right edge as a fraction of map width.</summary>
    public float Right { get; set; } = 0.6f;

    /// <summary>Bottom edge as a fraction of map height.</summary>
    public float Bottom { get; set; } = 0.6f;

    /// <summary>ARGB color used to draw this region's rectangle in the editor.</summary>
    public uint ColorPacked { get; set; } = 0x8000FF00; // semi-transparent green

    /// <summary>Helper — converts the packed color to XNA Color.</summary>
    public Color Color
    {

        get => new Color(
            (byte)((ColorPacked >> 16) & 0xFF),
            (byte)((ColorPacked >> 8) & 0xFF),
            (byte)(ColorPacked & 0xFF),
            (byte)((ColorPacked >> 24) & 0xFF));
        set => ColorPacked =
            ((uint)value.A << 24) |
            ((uint)value.R << 16) |
            ((uint)value.G << 8) |
             (uint)value.B;
    }

    /// <summary>
    /// Opacity (0–255) — stored as the alpha byte of ColorPacked.
    /// Getting or setting this is identical to reading/writing Color.A.
    /// Exists as a named property so the editor slider can reference it clearly.
    public byte Opacity { get; set; } = 160;
}
