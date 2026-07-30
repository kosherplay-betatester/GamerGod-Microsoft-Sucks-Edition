using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace GamerGod.Core.FreeGames;

/// <summary>
/// Reads the free-game catalogue and orders it.
///
/// <para>
/// Pure, so every decision — which entries survive, which addresses are safe to open, how each
/// ordering behaves — is testable without a network. The source is FreeToGame's public API,
/// which needs no key and no account, and returns the same list its website shows.
/// </para>
///
/// <para>
/// GamerGod never hosts, mirrors or downloads a game. Each entry opens its catalogue page, and
/// the actual acquiring happens at the publisher's own store, in a browser, with the user
/// deciding. Article IX again: this knows the names, somebody else does the distributing.
/// </para>
/// </summary>
public static class FreeGameFeed
{
    /// <summary>
    /// The single address this feature contacts.
    ///
    /// <para>
    /// Filtered to Windows at the source rather than locally: a browser game or an Xbox title
    /// listed in an application that partitions a Windows CPU is noise.
    /// </para>
    /// </summary>
    public const string CatalogueUrl = "https://www.freetogame.com/api/games?platform=pc";

    /// <summary>
    /// Parses the catalogue response. Returns empty for anything that is not one — a rate-limit
    /// body, an HTML error page, a truncated download.
    /// </summary>
    public static ImmutableArray<FreeGame> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var games = ImmutableArray.CreateBuilder<FreeGame>();
            var rank = 0;

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (ReadGame(element, rank) is { } game)
                {
                    games.Add(game);
                    rank++;
                }
            }

            return games.ToImmutable();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static FreeGame? ReadGame(JsonElement element, int rank)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var title = Text(element, "title");
        var page = Text(element, "game_url");

        // Without a title there is nothing to show, and without a page there is nowhere to
        // send anybody — an entry missing either is dropped rather than displayed broken.
        if (string.IsNullOrWhiteSpace(title) || !IsCataloguePage(page))
        {
            return null;
        }

        var thumbnail = Text(element, "thumbnail");

        return new FreeGame
        {
            Id = element.TryGetProperty("id", out var id) && id.TryGetInt32(out var value) ? value : 0,
            Title = title.Trim(),
            Summary = Text(element, "short_description")?.Trim() ?? string.Empty,
            Genre = Text(element, "genre")?.Trim() ?? "Unknown",
            Publisher = Text(element, "publisher")?.Trim() ?? string.Empty,
            PageUrl = page!,
            ThumbnailUrl = IsCatalogueAsset(thumbnail) ? thumbnail : null,
            Released = ParseDate(Text(element, "release_date")),
            PopularityRank = rank,
        };
    }

    /// <summary>
    /// Dates arrive as yyyy-MM-dd, and some entries carry a placeholder.
    ///
    /// <para>
    /// Unparseable and placeholder dates become null rather than a default, so an unknown date
    /// sorts as unknown instead of pretending to be January 1st of year one — which would put
    /// every one of them at the top of an oldest-first list.
    /// </para>
    /// </summary>
    private static DateOnly? ParseDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && date.Year > 1970
            ? date
            : null;

    /// <summary>
    /// Whether a page address may be opened in the user's browser.
    ///
    /// <para>
    /// This URL arrives from a remote server and becomes a navigation. Pinned to the catalogue's
    /// own domain, which is where its outbound links live — so a compromised response cannot
    /// send somebody to an arbitrary site from inside GamerGod.
    /// </para>
    /// </summary>
    public static bool IsCataloguePage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("freetogame.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".freetogame.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Same rule for artwork, which becomes a download rather than a navigation.</summary>
    public static bool IsCatalogueAsset(string? url) => IsCataloguePage(url);

    /// <summary>
    /// Applies an ordering.
    ///
    /// <para>
    /// Every one is a stable sort with a defined tiebreak, so the grid does not reshuffle
    /// between two views of the same data — which reads as a bug even when nothing is wrong.
    /// </para>
    /// </summary>
    public static ImmutableArray<FreeGame> Sort(ImmutableArray<FreeGame> games, FreeGameSort by)
    {
        if (games.IsDefaultOrEmpty)
        {
            return [];
        }

        return by switch
        {
            // Newest first, and entries with no date go last rather than pretending to be old.
            FreeGameSort.Newest =>
            [
                .. games
                    .OrderByDescending(g => g.Released.HasValue)
                    .ThenByDescending(g => g.Released ?? DateOnly.MinValue)
                    .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            ],

            FreeGameSort.Alphabetical =>
            [
                .. games.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            ],

            FreeGameSort.Genre =>
            [
                .. games
                    .OrderBy(g => g.Genre, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(g => g.PopularityRank),
            ],

            _ => [.. games.OrderBy(g => g.PopularityRank)],
        };
    }

    public static string Describe(FreeGameSort sort) => sort switch
    {
        FreeGameSort.Newest => "NEWEST",
        FreeGameSort.Alphabetical => "A–Z",
        FreeGameSort.Genre => "GENRE",
        _ => "POPULAR",
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
