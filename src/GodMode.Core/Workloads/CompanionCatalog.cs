using System.Collections.Immutable;

namespace GodMode.Core.Workloads;

/// <summary>What a companion tool does for GodMode, so the offer can explain itself.</summary>
public enum CompanionRole
{
    /// <summary>Controller-navigable library frontend.</summary>
    Frontend,

    /// <summary>Frametime and telemetry capture.</summary>
    Measurement,

    /// <summary>Frame limiting and on-screen display.</summary>
    FrameControl,

    /// <summary>Controller and input device management.</summary>
    Input,

    /// <summary>An additional platform GodMode can then optimise.</summary>
    Platform,
}

/// <summary>
/// An optional third-party tool GodMode can integrate with.
///
/// <para>
/// Charter Article IX: build the conductor, not every instrument. GodMode does not bundle,
/// silently install, or fork any of these. It detects whether they are present, explains
/// what each one would add, and — only if the user says yes — hands the install to winget.
/// Nothing here is required; GodMode works without every one of them.
/// </para>
/// </summary>
public sealed record Companion
{
    public required string Name { get; init; }

    public required CompanionRole Role { get; init; }

    /// <summary>winget package identifier, used only on explicit user request.</summary>
    public required string WingetId { get; init; }

    /// <summary>Process or service names that indicate it is already installed.</summary>
    public required ImmutableArray<string> DetectBy { get; init; }

    /// <summary>What GodMode gains. Shown verbatim in the offer, so it must be honest.</summary>
    public required string Benefit { get; init; }

    /// <summary>True when GodMode is substantially less useful without it.</summary>
    public bool StronglyRecommended { get; init; }

    public string? Caveat { get; init; }
}

public static class CompanionCatalog
{
    public static readonly ImmutableArray<Companion> All =
    [
        new()
        {
            Name = "Intel PresentMon",
            Role = CompanionRole.Measurement,
            WingetId = "Intel.PresentMon",
            DetectBy = ["PresentMon", "PresentMonService"],
            StronglyRecommended = true,
            Benefit = "Supplies the frametime data behind every measured claim GodMode makes, "
                    + "and identifies which process is presenting frames without ever opening "
                    + "a handle to it. Without this, GodMode cannot prove anything it says.",
        },
        new()
        {
            Name = "Playnite",
            Role = CompanionRole.Frontend,
            WingetId = "Playnite.Playnite",
            DetectBy = ["Playnite.DesktopApp", "Playnite.FullscreenApp"],
            StronglyRecommended = true,
            Benefit = "Controller-navigable fullscreen library across Steam, Epic, GOG, Xbox "
                    + "and every emulator in GodMode's catalogue. This is the console shell.",
        },
        new()
        {
            Name = "RivaTuner Statistics Server",
            Role = CompanionRole.FrameControl,
            WingetId = "Guru3D.RTSS",
            DetectBy = ["RTSS", "RTSSHooksLoader64"],
            Benefit = "Precise frame limiting, used to sit the frame cap at the variable "
                    + "refresh sweet spot — the single largest latency win available on most "
                    + "systems. Also provides an on-screen display in exclusive fullscreen, "
                    + "where GodMode's own non-injecting overlay cannot draw.",
            Caveat = "RTSS works by injecting into the game. That is its design, not "
                   + "GodMode's, and GodMode never asks it to inject into a title protected "
                   + "by kernel anti-cheat.",
        },
        new()
        {
            Name = "HidHide",
            Role = CompanionRole.Input,
            WingetId = "Nefarius.HidHide",
            DetectBy = ["HidHideClient", "HidHideCLI"],
            Benefit = "Hides duplicate controller devices so games see exactly one gamepad. "
                    + "Fixes the classic double-input problem with DualSense and third-party "
                    + "pads.",
        },
        new()
        {
            Name = "Google Play Games on PC",
            Role = CompanionRole.Platform,
            WingetId = "Google.PlayGames",
            DetectBy = ["crosvm", "GooglePlayGames"],
            Benefit = "Google's official Android runtime for Windows, and the supported way to "
                    + "play Android titles on a PC. Once installed, GodMode routes its virtual "
                    + "machine worker threads like any other workload.",
            Caveat = "Windows Subsystem for Android was retired by Microsoft on 5 March 2025, "
                   + "so this is the current official path rather than a third-party emulator. "
                   + "Like all hypervisor-backed runtimes it should not be left running while "
                   + "playing a title with kernel anti-cheat.",
        },
    ];

    /// <summary>
    /// Companions not yet present. The caller decides whether to offer them; GodMode never
    /// installs anything on its own initiative.
    /// </summary>
    public static ImmutableArray<Companion> Missing(IEnumerable<string> installedProcessNames)
    {
        ArgumentNullException.ThrowIfNull(installedProcessNames);

        var present = new HashSet<string>(
            installedProcessNames.Select(Path.GetFileNameWithoutExtension).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        return All
            .Where(c => !c.DetectBy.Any(present.Contains))
            .ToImmutableArray();
    }

    /// <summary>
    /// The exact command a user would run. Surfaced in the UI next to the button so nothing
    /// happens that the user could not have typed themselves.
    /// </summary>
    public static string InstallCommand(Companion companion)
    {
        ArgumentNullException.ThrowIfNull(companion);
        return $"winget install --id {companion.WingetId} --accept-package-agreements";
    }
}
