using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamerGod.Core.FreeGames;
using GamerGod.Windows;

namespace GamerGod.Ui.Library;

/// <summary>
/// One free game in the grid.
///
/// <para>
/// Landscape rather than the 2:3 of the owned-games grid, because that is the shape the
/// catalogue publishes — 365x206 key art. Forcing it into a portrait frame would crop away most
/// of every image to make two unrelated grids match.
/// </para>
/// </summary>
public sealed class FreeGameTile : INotifyPropertyChanged
{
    private ImageSource? _art;

    private FreeGameTile(FreeGame game)
    {
        Game = game;
        Fallback = TileArt.Gradient(game.Title);
        Initial = TileArt.Initial(game.Title);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FreeGame Game { get; }

    public string Title => Game.Title;

    public string Genre => Game.Genre.ToUpperInvariant();

    public string Summary => Game.Summary;

    /// <summary>Year only. A full date is more precision than a browsing grid needs.</summary>
    public string Released => Game.Released is { } date
        ? date.Year.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : string.Empty;

    public string Publisher => Game.Publisher;

    public ImageSource? Art
    {
        get => _art;
        private set
        {
            _art = value;
            Raise();
            Raise(nameof(HasArt));
        }
    }

    public bool HasArt => Art is not null;

    public Brush Fallback { get; }

    public string Initial { get; }

    public static FreeGameTile From(FreeGame game) => new(game);

    /// <summary>
    /// Loads this tile's key art, downloading it once if it is not already on disk.
    ///
    /// <para>
    /// Returns true only when the tile actually gained artwork, so a caller can report a real
    /// count rather than an optimistic one.
    /// </para>
    /// </summary>
    public async Task<bool> TryLoadArtAsync(CancellationToken token)
    {
        if (HasArt)
        {
            return false;
        }

        var path = await FreeGameSource.TryFetchThumbnailAsync(Game, token).ConfigureAwait(true);

        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();

            // Decoded at display width rather than native. Three hundred of these held at full
            // size would be hundreds of megabytes for no visible gain.
            image.DecodePixelWidth = 320;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();

            Art = image;
            return true;
        }
        catch (Exception)
        {
            // A partially written or malformed file. The generated mark stays.
            return false;
        }
    }

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
