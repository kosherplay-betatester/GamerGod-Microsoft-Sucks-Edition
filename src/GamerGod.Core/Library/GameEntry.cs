using System.Collections.Immutable;

namespace GamerGod.Core.Library;

/// <summary>Where a title came from, which also decides how it is launched.</summary>
public enum GameSource
{
    Steam,
    Epic,
    Gog,
    Xbox,
    Emulator,
    Manual,
}

/// <summary>
/// One launchable thing in the library.
/// </summary>
public sealed record GameEntry
{
    public required string Name { get; init; }

    public required GameSource Source { get; init; }

    /// <summary>
    /// How to start it. For a store title this is a protocol URI — <c>steam://rungameid/…</c> —
    /// because launching the store's own bootstrapper is the only way a game gets the
    /// environment, cloud saves and anti-cheat setup it expects. Starting the executable
    /// directly is the classic way third-party launchers break games.
    /// </summary>
    public required string LaunchTarget { get; init; }

    /// <summary>Install directory, when known. Used to detect anti-cheat before launching.</summary>
    public string? InstallPath { get; init; }

    /// <summary>Store-specific identifier, for display and for de-duplication.</summary>
    public string? Id { get; init; }

    /// <summary>Console or platform, for emulators. Empty for PC titles.</summary>
    public ImmutableArray<string> Systems { get; init; } = [];

    public string SourceLabel => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic",
        GameSource.Gog => "GOG",
        GameSource.Xbox => "Xbox",
        GameSource.Emulator => "Emulator",
        _ => "Added by you",
    };
}

/// <summary>Discovers installed games and emulators.</summary>
public interface IGameLibrary
{
    ValueTask<ImmutableArray<GameEntry>> ScanAsync(CancellationToken cancellationToken);
}
