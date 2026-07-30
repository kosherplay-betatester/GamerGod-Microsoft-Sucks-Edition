using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerGod.Core.Library;
using GamerGod.Windows;

namespace GamerGod.Ui.Library;

/// <summary>
/// One tile in the library grid.
///
/// <para>
/// Cover art comes from the cache the store already keeps on disk. It is frequently absent —
/// Steam fetches art lazily, so a game you own but have never scrolled past in the grid has
/// only a 32-pixel icon — and that case has to look deliberate rather than broken. A missing
/// cover is the normal state for a fresh install, not an error, so the fallback is designed
/// rather than apologised for.
/// </para>
///
/// <para>
/// If the user has switched cover art downloading on, <see cref="TryFetchCoverAsync"/> can
/// fill the gap afterwards and the tile updates in place. That is why this raises change
/// notifications: art arrives after the grid is already on screen, and the alternative — a
/// blank grid until every image resolves — would make the page feel broken while it worked.
/// </para>
/// </summary>
public sealed class GameTile : INotifyPropertyChanged
{
    private ImageSource? _cover;
    private bool _isSelected;

    private GameTile(GameEntry entry)
    {
        Entry = entry;
        _cover = Load(LocalArtPath(entry));
        Fallback = BuildFallback(entry.Name);
        Initial = InitialOf(entry.Name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GameEntry Entry { get; }

    public string Name => Entry.Name;

    public string SourceLabel => Entry.SourceLabel.ToUpperInvariant();

    public bool IsEmulator => Entry.Source == GameSource.Emulator;

    /// <summary>Systems line for emulators; empty for PC titles.</summary>
    public string Subtitle => Entry.Systems.IsDefaultOrEmpty
        ? string.Empty
        : string.Join("  ·  ", Entry.Systems);

    /// <summary>Real box art, or null when none is available.</summary>
    public ImageSource? Cover
    {
        get => _cover;
        private set
        {
            _cover = value;
            Raise();
            Raise(nameof(HasCover));
            Raise(nameof(IsBanner));
        }
    }

    public bool HasCover => Cover is not null;

    /// <summary>
    /// True when the only art available is landscape, which changes how the tile draws it.
    ///
    /// <para>
    /// Plenty of publishers never upload a portrait capsule — Battlefield 6 is one — so all
    /// that exists is a 460x215 header. Cropping that to fill a 2:3 frame keeps about a third
    /// of its width and throws away the title treatment, which is the part worth seeing. Shown
    /// whole as a band across the tile instead, it reads as a designed card rather than a
    /// botched crop.
    /// </para>
    /// </summary>
    public bool IsBanner => Cover is { } art && art.Width > art.Height;

    /// <summary>Shown when <see cref="Cover"/> is null.</summary>
    public Brush Fallback { get; }

    public string Initial { get; }

    /// <summary>Drives the tile's selected chrome. Only one tile in the grid holds this.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            Raise();
        }
    }

    public static GameTile From(GameEntry entry) => new(entry);

    /// <summary>
    /// Downloads art for a tile that has none, if the user has opted in. Returns true when the
    /// tile actually gained a cover, so the caller can report a real number rather than a
    /// hopeful one.
    /// </summary>
    public async Task<bool> TryFetchCoverAsync(CancellationToken token)
    {
        if (HasCover || Entry.Source != GameSource.Steam || Entry.Id is null)
        {
            return false;
        }

        var path = await CoverArtCache.TryFetchAsync(Entry.Id, token).ConfigureAwait(true);
        var image = Load(path);

        if (image is null)
        {
            return false;
        }

        Cover = image;
        return true;
    }

    /// <summary>
    /// Art already on disk: whatever the store cached, else whatever a previous opt-in fetch
    /// left behind. Neither touches the network.
    /// </summary>
    private static string? LocalArtPath(GameEntry entry)
    {
        if (entry.Source != GameSource.Steam || entry.Id is null)
        {
            return null;
        }

        // Portrait first: it is the only shape that fits a 2:3 tile. The landscape header is a
        // poor substitute but far better than nothing, and it is what Steam itself falls back
        // to.
        return SteamArtwork.FindCover(entry.Id)
               ?? CoverArtCache.FindCached(entry.Id)
               ?? SteamArtwork.FindHeader(entry.Id);
    }

    private static ImageSource? Load(string? path)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();

            // Decoded at display size rather than full resolution. A library of eighty 600x900
            // covers held at native size is hundreds of megabytes for no visible gain.
            image.DecodePixelWidth = 360;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // A partially written file — Steam replaces art in place — is not worth failing
            // over. The tile falls back and looks fine.
            return null;
        }
    }

    /// <summary>
    /// A generated tile for titles with no art anywhere.
    ///
    /// <para>
    /// The hue is derived from the name so a given game always looks the same, which makes the
    /// grid learnable rather than arbitrary. Saturation and lightness are held low so these sit
    /// in the same world as the instrument chrome instead of shouting over the real covers next
    /// to them.
    /// </para>
    /// </summary>
    private static Brush BuildFallback(string name)
    {
        var hue = StableHue(name);

        var top = FromHsl(hue, 0.28, 0.20);
        var bottom = FromHsl((hue + 24) % 360, 0.32, 0.10);

        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0.6, 1),
        };

        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        brush.Freeze();

        return brush;
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

    private static string InitialOf(string name)
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

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
