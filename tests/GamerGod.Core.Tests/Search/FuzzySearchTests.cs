using GamerGod.Core.Search;
using Xunit;

namespace GamerGod.Core.Tests.Search;

/// <summary>
/// Finding what somebody meant.
///
/// <para>
/// Every misspelling here is one a person would actually type — transpositions, doubled letters,
/// dropped letters, a space in the wrong place. The failure this guards against is a user
/// looking straight at a game in the list while the search box tells them it is not there.
/// </para>
/// </summary>
public sealed class FuzzySearchTests
{
    private static bool Finds(string query, string candidate) =>
        FuzzySearch.Score(query, candidate) is not null;

    // ---- the ordinary cases --------------------------------------------

    [Theory]
    [InlineData("overwatch", "Overwatch")]
    [InlineData("OVERWATCH", "Overwatch")]
    [InlineData("over", "Overwatch")]
    [InlineData("watch", "Overwatch")]
    [InlineData("tanks", "World of Tanks: HEAT")]
    [InlineData("exile", "Path of Exile")]
    public void Exact_prefix_and_substring_all_work(string query, string title) =>
        Assert.True(Finds(query, title), $"'{query}' should find '{title}'");

    [Theory]
    [InlineData("counter strike", "Counter-Strike")]
    [InlineData("counterstrike", "Counter-Strike")]
    [InlineData("counter-strike", "Counter Strike")]
    [InlineData("battlefield6", "Battlefield™ 6")]
    [InlineData("battlefield 6", "Battlefield™ 6")]
    public void Punctuation_and_spacing_stop_mattering(string query, string title) =>
        Assert.True(Finds(query, title), $"'{query}' should find '{title}'");

    // ---- the reason this exists ----------------------------------------

    [Theory]
    [InlineData("battlefeild", "Battlefield 6")]
    [InlineData("battelfield", "Battlefield 6")]
    [InlineData("overwacht", "Overwatch")]
    [InlineData("owervatch", "Overwatch")]
    [InlineData("warfrmae", "Warframe")]
    [InlineData("warfarme", "Warframe")]
    [InlineData("fortnight", "Fortnite")]
    [InlineData("dolpin", "Dolphin")]
    [InlineData("retroach", "RetroArch")]
    [InlineData("afterburnner", "MSI Afterburner")]
    [InlineData("emualtor", "Emulator")]
    public void A_typo_still_finds_the_game(string query, string title) =>
        Assert.True(Finds(query, title), $"'{query}' should still find '{title}'");

    [Theory]
    [InlineData("wot", "World of Tanks")]
    [InlineData("cs", "Counter-Strike")]
    [InlineData("poe", "Path of Exile")]
    [InlineData("rtss", "RivaTuner Statistics Server")]
    [InlineData("gpg", "Google Play Games")]
    public void An_acronym_finds_the_title_it_stands_for(string query, string title) =>
        Assert.True(Finds(query, title), $"'{query}' should find '{title}'");

    [Theory]
    [InlineData("exile path", "Path of Exile")]
    [InlineData("tanks world", "World of Tanks")]
    [InlineData("6 battlefield", "Battlefield 6")]
    public void Words_in_any_order_still_match(string query, string title) =>
        Assert.True(Finds(query, title), $"'{query}' should find '{title}'");

    // ---- and the things it must not match ------------------------------

    [Theory]
    [InlineData("minecraft", "Overwatch")]
    [InlineData("zzzzzz", "Battlefield 6")]
    [InlineData("solitaire", "Path of Exile")]
    [InlineData("warframe", "War Thunder")]
    public void Something_genuinely_different_is_not_a_match(string query, string title)
    {
        // The tolerance has to stop somewhere. "warframe" and "War Thunder" share a prefix and
        // a length, and matching them would make every search return half the catalogue.
        Assert.False(Finds(query, title), $"'{query}' should NOT find '{title}'");
    }

    [Fact]
    public void A_very_short_query_does_not_fuzzy_match()
    {
        // At two characters almost everything is within one edit of everything, so short
        // queries only ever match literally.
        Assert.True(Finds("ov", "Overwatch"));
        Assert.False(Finds("xq", "Overwatch"));
    }

    // ---- ordering ------------------------------------------------------

    [Fact]
    public void A_literal_match_always_outranks_a_corrected_one()
    {
        // The property that makes the score usable for sorting: if typing "war" put a
        // typo-corrected result above "Warframe", the list would look broken.
        var literal = FuzzySearch.Score("war", "Warframe")!.Value;
        var corrected = FuzzySearch.Score("warfrmae", "Warframe")!.Value;

        Assert.True(literal > corrected);
    }

    [Fact]
    public void An_exact_match_outranks_everything()
    {
        var exact = FuzzySearch.Score("overwatch", "Overwatch")!.Value;

        Assert.True(exact > FuzzySearch.Score("overwatch", "Overwatch 2")!.Value);
        Assert.True(exact > FuzzySearch.Score("over", "Overwatch")!.Value);
    }

    [Fact]
    public void A_shorter_title_wins_the_same_prefix()
    {
        // Typing "war" should offer War Thunder before Warhammer 40,000: Darktide.
        var shorter = FuzzySearch.Score("war", "War Thunder")!.Value;
        var longer = FuzzySearch.Score("war", "Warhammer 40,000: Darktide")!.Value;

        Assert.True(shorter > longer);
    }

    // ---- searching several fields --------------------------------------

    [Fact]
    public void A_search_can_cover_more_than_the_title()
    {
        // Typing a genre or a publisher is a normal thing to do in a games list.
        Assert.NotNull(FuzzySearch.Best("shooter", "Overwatch", "Shooter", "Blizzard"));
        Assert.NotNull(FuzzySearch.Best("blizzard", "Overwatch", "Shooter", "Blizzard"));
        Assert.Null(FuzzySearch.Best("racing", "Overwatch", "Shooter", "Blizzard"));
    }

    [Fact]
    public void An_empty_query_matches_everything()
    {
        // An empty search box is not a filter that excludes the whole list.
        Assert.True(FuzzySearch.Matches("", "Overwatch"));
        Assert.True(FuzzySearch.Matches("   ", "Overwatch"));
        Assert.True(FuzzySearch.Matches(null, "Overwatch"));
    }

    [Fact]
    public void Missing_and_empty_fields_are_survivable()
    {
        Assert.Null(FuzzySearch.Score("x", null));
        Assert.Null(FuzzySearch.Score(null, "x"));
        Assert.Null(FuzzySearch.Best("x", null, null));
        Assert.Null(FuzzySearch.Score("!!!", "Overwatch"));
    }

    [Fact]
    public void Searching_stays_fast_enough_for_every_keystroke()
    {
        // Runs over the whole free-games catalogue on each character typed. The early
        // abandonment in the distance calculation is what keeps this cheap.
        var titles = Enumerable.Range(0, 400).Select(i => $"Some Game Title Number {i}").ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        for (var i = 0; i < 10; i++)
        {
            foreach (var title in titles)
            {
                FuzzySearch.Best("battlefeild", title, "Shooter", "Publisher");
            }
        }

        Assert.True(
            watch.ElapsedMilliseconds < 500,
            $"4,000 scored comparisons took {watch.ElapsedMilliseconds}ms");
    }
}
