using System.Collections.Immutable;

namespace GamerGod.Core.Catalogue;

public enum CatalogueKind
{
    /// <summary>A store or a library manager.</summary>
    Launcher,

    /// <summary>An emulator.</summary>
    Emulator,
}

/// <summary>
/// One piece of software GamerGod can point you at.
///
/// <para>
/// A catalogue entry is a recommendation and an address, never a payload. GamerGod hosts
/// nothing, mirrors nothing, and patches nothing — Charter Article IX again, and here it is
/// also the only defensible position: the moment this project redistributes somebody else's
/// binary it becomes responsible for that binary's integrity, and it has no way to be.
/// </para>
/// </summary>
public sealed record CatalogueEntry
{
    /// <summary>Stable slug. Used by tests and settings; never shown.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required CatalogueKind Kind { get; init; }

    /// <summary>Section heading. Emulator groups are ordered oldest hardware first.</summary>
    public required string Group { get; init; }

    /// <summary>What it runs, in the words somebody would use to search for it.</summary>
    public required string Systems { get; init; }

    /// <summary>
    /// The Windows Package Manager identifier, or null when winget does not carry it.
    ///
    /// <para>
    /// Null is common and is not a defect in the entry. Several of the best emulators publish
    /// only from their own site or a GitHub release, and inventing an id for them would produce
    /// an install button that fails. Those entries open their official page instead, and say
    /// so on the button.
    /// </para>
    /// </summary>
    public string? WingetId { get; init; }

    /// <summary>The project's own page. Always present — it is the fallback and the citation.</summary>
    public required string Homepage { get; init; }

    /// <summary>
    /// What somebody needs to know before installing, or null.
    ///
    /// <para>
    /// Mostly the BIOS problem. A PS2 emulator with no BIOS boots to a black screen, and a user
    /// who was not told that concludes the emulator is broken or that GamerGod installed it
    /// wrong. Saying it up front costs one line and prevents the whole confusion.
    /// </para>
    /// </summary>
    public string? Note { get; init; }

    public bool CanInstall => WingetId is not null;
}

/// <summary>How an entry relates to the machine right now.</summary>
public enum CatalogueState
{
    /// <summary>Not looked up yet.</summary>
    Unknown,

    Installed,

    NotInstalled,

    /// <summary>winget does not carry it; the official page is the only route.</summary>
    Unavailable,
}

public sealed record CatalogueGroup
{
    public required string Title { get; init; }

    public required ImmutableArray<CatalogueEntry> Entries { get; init; }
}
