using System.Collections.Immutable;
using GamerGod.Core.FreeGames;
using Xunit;

namespace GamerGod.Core.Tests.FreeGames;

/// <summary>
/// Reading and ordering the free-game catalogue.
///
/// <para>
/// Two things drive these. Every URL in the response comes from a remote server and becomes a
/// navigation or a download, so none is taken on trust. And every ordering has to be stable
/// with a defined tiebreak — a grid that reshuffles between two views of the same data reads as
/// a bug even when nothing is wrong.
/// </para>
/// </summary>
public sealed class FreeGameFeedTests
{
    private static string Entry(
        int id = 540,
        string title = "Overwatch",
        string page = "https://www.freetogame.com/open/overwatch",
        string? thumbnail = "https://www.freetogame.com/g/540/thumbnail.jpg",
        string release = "2022-10-04",
        string genre = "Shooter") =>
        "{"
        + $"\"id\":{id},"
        + $"\"title\":\"{title}\","
        + (thumbnail is null ? string.Empty : $"\"thumbnail\":\"{thumbnail}\",")
        + "\"short_description\":\"A hero shooter.\","
        + $"\"game_url\":\"{page}\","
        + $"\"genre\":\"{genre}\","
        + "\"platform\":\"PC (Windows)\","
        + "\"publisher\":\"Activision Blizzard\","
        + "\"developer\":\"Blizzard\","
        + $"\"release_date\":\"{release}\""
        + "}";

    [Fact]
    public void A_catalogue_entry_is_read_in_full()
    {
        var games = FreeGameFeed.Parse($"[{Entry()}]");

        var game = Assert.Single(games);
        Assert.Equal("Overwatch", game.Title);
        Assert.Equal("Shooter", game.Genre);
        Assert.Equal(new DateOnly(2022, 10, 4), game.Released);
        Assert.NotNull(game.ThumbnailUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html>rate limited</html>")]
    [InlineData("{ not json")]
    [InlineData("{\"message\":\"error\"}")]
    public void A_response_that_is_not_a_catalogue_yields_nothing(string? json) =>
        Assert.Empty(FreeGameFeed.Parse(json));

    [Theory]
    [InlineData("https://evil.example.com/game")]
    [InlineData("http://www.freetogame.com/open/x")]
    [InlineData("https://freetogame.com.evil.example.com/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void An_entry_whose_page_is_not_the_catalogue_is_dropped(string page)
    {
        // This becomes a browser navigation. A compromised response must not be able to send
        // somebody to an arbitrary site from inside GamerGod.
        Assert.Empty(FreeGameFeed.Parse($"[{Entry(page: page)}]"));
        Assert.False(FreeGameFeed.IsCataloguePage(page));
    }

    [Fact]
    public void An_entry_whose_artwork_is_elsewhere_survives_without_it()
    {
        // The game is still real and its page is still valid; only the image is refused, and
        // the tile falls back to its generated mark.
        var game = Assert.Single(FreeGameFeed.Parse($"[{Entry(thumbnail: "https://evil.example.com/a.jpg")}]"));

        Assert.Null(game.ThumbnailUrl);
        Assert.Equal("Overwatch", game.Title);
    }

    [Fact]
    public void An_entry_with_no_title_is_dropped_rather_than_shown_blank() =>
        Assert.Empty(FreeGameFeed.Parse($"[{Entry(title: "")}]"));

    [Theory]
    [InlineData("0000-00-00")]
    [InlineData("not a date")]
    [InlineData("1969-01-01")]
    public void A_placeholder_date_becomes_unknown_rather_than_ancient(string release)
    {
        // Defaulting these to DateOnly.MinValue would put every game with an unknown date at
        // one end of a date sort, which looks like a real ordering and is not.
        var game = Assert.Single(FreeGameFeed.Parse($"[{Entry(release: release)}]"));

        Assert.Null(game.Released);
    }

    // ---- ordering -------------------------------------------------------

    private static ImmutableArray<FreeGame> Sample() => FreeGameFeed.Parse(
        "["
        + Entry(1, "Zebra Quest", "https://www.freetogame.com/open/z", release: "2020-01-01", genre: "Strategy") + ","
        + Entry(2, "Alpha Strike", "https://www.freetogame.com/open/a", release: "2024-06-01", genre: "Shooter") + ","
        + Entry(3, "Middle Ground", "https://www.freetogame.com/open/m", release: "0000-00-00", genre: "MMORPG")
        + "]");

    [Fact]
    public void Popularity_keeps_the_order_the_catalogue_returned()
    {
        // The rank is a position in the source's list, not a download count — nothing here
        // reorders it, because there is no number to reorder by.
        var sorted = FreeGameFeed.Sort(Sample(), FreeGameSort.Popularity);

        Assert.Equal(["Zebra Quest", "Alpha Strike", "Middle Ground"], sorted.Select(g => g.Title));
    }

    [Fact]
    public void Newest_puts_undated_games_last_rather_than_first()
    {
        var sorted = FreeGameFeed.Sort(Sample(), FreeGameSort.Newest);

        Assert.Equal(["Alpha Strike", "Zebra Quest", "Middle Ground"], sorted.Select(g => g.Title));
    }

    [Fact]
    public void Alphabetical_is_case_insensitive()
    {
        var sorted = FreeGameFeed.Sort(Sample(), FreeGameSort.Alphabetical);

        Assert.Equal(["Alpha Strike", "Middle Ground", "Zebra Quest"], sorted.Select(g => g.Title));
    }

    [Fact]
    public void Genre_groups_and_then_falls_back_to_the_catalogue_order()
    {
        var sorted = FreeGameFeed.Sort(Sample(), FreeGameSort.Genre);

        Assert.Equal(["MMORPG", "Shooter", "Strategy"], sorted.Select(g => g.Genre));
    }

    [Fact]
    public void Every_ordering_keeps_every_game()
    {
        // A sort that silently dropped an entry would look like the catalogue was smaller.
        foreach (var sort in Enum.GetValues<FreeGameSort>())
        {
            Assert.Equal(3, FreeGameFeed.Sort(Sample(), sort).Length);
        }
    }

    [Fact]
    public void Sorting_an_empty_list_is_not_an_error()
    {
        foreach (var sort in Enum.GetValues<FreeGameSort>())
        {
            Assert.Empty(FreeGameFeed.Sort([], sort));
        }
    }

    [Fact]
    public void Every_ordering_has_a_label()
    {
        foreach (var sort in Enum.GetValues<FreeGameSort>())
        {
            Assert.False(string.IsNullOrWhiteSpace(FreeGameFeed.Describe(sort)));
        }
    }

    [Fact]
    public void There_is_no_ordering_for_data_the_catalogue_does_not_publish()
    {
        // The catalogue publishes no download count and no file size. Offering a sort for
        // either would mean inventing a number or mislabelling one, so neither exists — this
        // asserts that a later edit does not quietly add one.
        var names = Enum.GetNames<FreeGameSort>();

        Assert.DoesNotContain("Downloads", names);
        Assert.DoesNotContain("Size", names);
        Assert.Equal(4, names.Length);
    }

    [Fact]
    public void The_only_address_this_feature_contacts_is_the_public_catalogue()
    {
        var uri = new Uri(FreeGameFeed.CatalogueUrl);

        Assert.Equal("https", uri.Scheme);
        Assert.EndsWith("freetogame.com", uri.Host, StringComparison.Ordinal);

        // Filtered at the source; a browser or console title has no business in an application
        // that partitions a Windows CPU.
        Assert.Contains("platform=pc", uri.Query, StringComparison.Ordinal);
    }
}
