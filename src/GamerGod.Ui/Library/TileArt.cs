using System;
using System.Windows;
using System.Windows.Media;

namespace GamerGod.Ui.Library;

/// <summary>
/// The generated mark shown when a game has no cover and a program has no icon.
///
/// <para>
/// Shared by both grids on purpose. A tile with real artwork and a tile without have to belong
/// to the same page, and two implementations of "designed stand-in" would drift into two
/// different-looking pages the first time either was touched.
/// </para>
/// </summary>
public static class TileArt
{
    /// <summary>
    /// A two-stop gradient whose hue is derived from the name, so a given title always looks
    /// the same and the grid becomes learnable rather than arbitrary. Saturation and lightness
    /// stay low so these sit in the same world as the instrument chrome instead of shouting
    /// over the real artwork beside them.
    /// </summary>
    public static Brush Gradient(string name)
    {
        var hue = StableHue(name);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0.6, 1),
        };

        brush.GradientStops.Add(new GradientStop(FromHsl(hue, 0.28, 0.20), 0));
        brush.GradientStops.Add(new GradientStop(FromHsl((hue + 24) % 360, 0.32, 0.10), 1));
        brush.Freeze();

        return brush;
    }

    public static string Initial(string name)
    {
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                return char.ToUpperInvariant(c).ToString();
            }
        }

        return "?";
    }

    /// <summary>
    /// FNV-1a rather than string.GetHashCode, which .NET randomises per process — a colour
    /// that changed every launch would make the grid feel unstable.
    /// </summary>
    private static double StableHue(string value)
    {
        var hash = 2166136261u;

        foreach (var c in value)
        {
            hash ^= char.ToUpperInvariant(c);
            hash *= 16777619u;
        }

        return hash % 360;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs((hue / 60 % 2) - 1));
        var m = lightness - (c / 2);

        var (r, g, b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}
