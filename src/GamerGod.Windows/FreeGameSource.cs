using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using GamerGod.Core.FreeGames;

namespace GamerGod.Windows;

/// <summary>What a load produced, and where it came from.</summary>
public sealed record FreeGameResult
{
    public required ImmutableArray<FreeGame> Games { get; init; }

    /// <summary>When this list was fetched, or null when nothing has ever been fetched.</summary>
    public DateTimeOffset? RetrievedUtc { get; init; }

    /// <summary>True when the list came off disk rather than the network.</summary>
    public bool FromCache { get; init; }

    public string? Problem { get; init; }
}

/// <summary>
/// Fetches the free-game catalogue, and keeps the last copy.
///
/// <para>
/// The third and last outbound connection in GamerGod, and like the others it happens only
/// after somebody presses something. The user asked for the confirmation explicitly, which is
/// also exactly what Charter Article IV requires — a list of free games is worth having and not
/// worth fetching behind anybody's back.
/// </para>
///
/// <para>
/// Nothing is downloaded but text and thumbnails. GamerGod never fetches a game: each entry
/// opens its catalogue page in a browser, and acquiring it happens at the publisher's store.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class FreeGameSource
{
    /// <summary>The catalogue is a few hundred entries of JSON; this is generous.</summary>
    private const int MaxBytes = 8 * 1024 * 1024;

    private const int MaxThumbnailBytes = 3 * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerGod", "freegames");

    private static string CachePath => Path.Combine(Folder, "catalogue.json");

    private static string ThumbnailFolder => Path.Combine(Folder, "thumbnails");

    /// <summary>
    /// The last list fetched, without touching the network.
    ///
    /// <para>
    /// Lets the page open instantly with whatever it had, so the refresh prompt is a choice
    /// rather than a toll gate on ever seeing anything.
    /// </para>
    /// </summary>
    public static FreeGameResult LoadCached()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return new FreeGameResult { Games = [], FromCache = true };
            }

            return new FreeGameResult
            {
                Games = FreeGameFeed.Parse(File.ReadAllText(CachePath)),
                RetrievedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(CachePath), TimeSpan.Zero),
                FromCache = true,
            };
        }
        catch (Exception ex)
        {
            return new FreeGameResult { Games = [], FromCache = true, Problem = ex.Message };
        }
    }

    /// <summary>Fetches a fresh list. Only ever called after the user has agreed to it.</summary>
    public static async Task<FreeGameResult> RefreshAsync(CancellationToken token)
    {
        try
        {
            using var response = await Http
                .GetAsync(FreeGameFeed.CatalogueUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return Failed($"the catalogue answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                return Failed("the catalogue response was implausibly large");
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var games = FreeGameFeed.Parse(json);

            // An empty parse means the body was not the catalogue — an error page, or a shape
            // change. Overwriting a good cache with that would lose the last working list.
            if (games.IsEmpty)
            {
                return Failed("the response did not contain a game list");
            }

            Directory.CreateDirectory(Folder);
            File.WriteAllText(CachePath, json);

            return new FreeGameResult
            {
                Games = games,
                RetrievedUtc = DateTimeOffset.UtcNow,
                FromCache = false,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(ex.Message);
        }
    }

    /// <summary>
    /// Key art for one entry, cached after the first fetch.
    ///
    /// <para>
    /// Returns the local path, or null. A missing thumbnail is drawn as the same generated mark
    /// the rest of the interface uses, so it looks designed rather than broken.
    /// </para>
    /// </summary>
    public static async Task<string?> TryFetchThumbnailAsync(FreeGame game, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(game);

        var path = Path.Combine(ThumbnailFolder, $"{game.Id}.jpg");

        if (File.Exists(path))
        {
            return path;
        }

        // Validated in Core before it is fetched: this address arrived from a remote server.
        if (game.ThumbnailUrl is not { } url || !FreeGameFeed.IsCatalogueAsset(url))
        {
            return null;
        }

        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength is > MaxThumbnailBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);

            // Sniffed rather than trusted, for the reason every other image fetch here is: a
            // cached error page would leave the tile permanently blank.
            if (bytes.Length is < 1024 or > MaxThumbnailBytes
                || bytes is not [0xFF, 0xD8, 0xFF, ..])
            {
                return null;
            }

            Directory.CreateDirectory(ThumbnailFolder);

            var temp = path + ".part";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);

            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FreeGameResult Failed(string problem)
    {
        // Falls back to whatever was already on disk. A failed refresh should leave the page
        // showing the last good list, not an empty one.
        var cached = LoadCached();

        return cached with { Problem = problem, FromCache = true };
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        return client;
    }
}
