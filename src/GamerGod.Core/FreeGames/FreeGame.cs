namespace GamerGod.Core.FreeGames;

/// <summary>
/// A game that costs nothing, as the catalogue publishes it.
/// </summary>
public sealed record FreeGame
{
    public required int Id { get; init; }

    public required string Title { get; init; }

    public required string Summary { get; init; }

    public required string Genre { get; init; }

    public required string Publisher { get; init; }

    /// <summary>Where to go to get it. Always the catalogue's own page, never a direct download.</summary>
    public required string PageUrl { get; init; }

    /// <summary>Landscape key art, or null when the entry published none.</summary>
    public string? ThumbnailUrl { get; init; }

    /// <summary>Release date, or null when the catalogue recorded something unparseable.</summary>
    public DateOnly? Released { get; init; }

    /// <summary>
    /// Rank in the catalogue's own popularity ordering, lowest first.
    ///
    /// <para>
    /// The position the source returned, not a download count — no free-game catalogue publishes
    /// one. Recorded as a rank rather than presented as a number so nothing here implies a
    /// measurement that does not exist.
    /// </para>
    /// </summary>
    public int PopularityRank { get; init; }
}

/// <summary>
/// The orderings this data can actually support.
///
/// <para>
/// Deliberately shorter than the list somebody would ask for. Download counts are published by
/// no free-game catalogue, and install size appears only on a per-game page — sorting 347
/// entries by it would mean 347 requests to sort by a number that is the minimum storage
/// requirement rather than a download size. Offering either would mean inventing data or
/// mislabelling it, so neither is offered and the interface says why.
/// </para>
/// </summary>
public enum FreeGameSort
{
    /// <summary>The catalogue's own ranking.</summary>
    Popularity,

    Newest,

    Alphabetical,

    Genre,
}
