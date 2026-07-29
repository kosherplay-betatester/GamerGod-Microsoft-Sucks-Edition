using System.Collections.Immutable;

namespace GodMode.Core.Policy;

/// <summary>
/// How aggressively a title's anti-cheat watches the process, which determines whether
/// GodMode may touch it at all.
/// </summary>
public enum AntiCheatTier
{
    /// <summary>
    /// Nothing conclusive was found.
    ///
    /// <para>
    /// This is deliberately <em>not</em> treated as "safe". Absence of evidence is not
    /// evidence of absence: anti-cheat components frequently load only once the game
    /// starts, and new vendors appear constantly. GodMode treats Unknown exactly like
    /// <see cref="Kernel"/> and stays ambient-only.
    /// </para>
    /// </summary>
    Unknown = 0,

    /// <summary>No anti-cheat detected and the title is explicitly known to have none.</summary>
    None = 1,

    /// <summary>User-mode anti-cheat only, e.g. VAC. No kernel driver involved.</summary>
    UserMode = 2,

    /// <summary>
    /// Kernel-mode anti-cheat: Javelin, Vanguard, EasyAntiCheat, BattlEye, XIGNCODE3.
    /// GodMode never opens a handle to a title protected by one of these.
    /// </summary>
    Kernel = 3,
}

/// <summary>
/// The observable fingerprints of one anti-cheat product.
///
/// <para>
/// Matching is by installed service name, loaded kernel driver, running process, and marker
/// files sitting beside the game executable. All four are read without ever opening a handle
/// to the game, so tier detection is itself an ambient operation.
/// </para>
/// </summary>
public sealed record AntiCheatSignature
{
    public required string Vendor { get; init; }

    public required AntiCheatTier Tier { get; init; }

    /// <summary>
    /// Service names. Matched even when the service is stopped: all three major kernel
    /// anti-cheats install as on-demand user-mode host services whose kernel component
    /// loads only when a protected game launches. Requiring them to be running would
    /// classify every title as unprotected right up until the moment it is too late.
    /// </summary>
    public ImmutableArray<string> Services { get; init; } = [];

    /// <summary>Kernel driver file names, matched case-insensitively.</summary>
    public ImmutableArray<string> Drivers { get; init; } = [];

    public ImmutableArray<string> Processes { get; init; } = [];

    /// <summary>File or directory names found alongside the game executable.</summary>
    public ImmutableArray<string> FileMarkers { get; init; } = [];
}

/// <summary>
/// Everything the detector is allowed to look at. Populated by <c>GodMode.Windows</c> from
/// the service control manager, <c>EnumDeviceDrivers</c>, the process list, and a directory
/// listing — never from the game's own memory or handles.
/// </summary>
public sealed record AntiCheatEnvironment
{
    /// <summary>Service names present in the SCM database, running or not.</summary>
    public ImmutableArray<string> InstalledServices { get; init; } = [];

    /// <summary>File names of currently loaded kernel drivers.</summary>
    public ImmutableArray<string> LoadedDrivers { get; init; } = [];

    /// <summary>Executable names of running processes.</summary>
    public ImmutableArray<string> RunningProcesses { get; init; } = [];

    /// <summary>File and directory names beside the game executable.</summary>
    public ImmutableArray<string> GameDirectoryEntries { get; init; } = [];
}

/// <summary>What the detector concluded, and why.</summary>
public sealed record AntiCheatAssessment
{
    public required AntiCheatTier Tier { get; init; }

    /// <summary>Every product matched, strongest tier first. Empty when nothing matched.</summary>
    public required ImmutableArray<AntiCheatFinding> Findings { get; init; }

    /// <summary>
    /// True when GodMode is confident no kernel anti-cheat is involved. Only ever true for
    /// <see cref="AntiCheatTier.None"/> and <see cref="AntiCheatTier.UserMode"/>.
    /// </summary>
    public bool ContactIsConsidered => Tier is AntiCheatTier.None or AntiCheatTier.UserMode;

    public string Explain() => Findings.IsEmpty
        ? "No anti-cheat signature matched. Treating as kernel-protected, because anti-cheat "
          + "components commonly load only once a game starts."
        : string.Join("; ", Findings.Select(f => $"{f.Vendor} ({f.Tier}) via {f.Evidence}"));
}

/// <param name="Evidence">The specific artefact that matched, for the audit log and the UI.</param>
public sealed record AntiCheatFinding(string Vendor, AntiCheatTier Tier, string Evidence);

/// <summary>
/// Identifies anti-cheat protection from ambient observation alone.
/// </summary>
public static class AntiCheatDetector
{
    /// <summary>
    /// Built-in signatures. Verified against a real machine with Battlefield 6,
    /// EasyAntiCheat and BattlEye installed rather than transcribed from forum posts.
    ///
    /// <para>
    /// This table is additive-only in spirit: removing or downgrading an entry can only ever
    /// make GodMode touch a game it previously left alone. Users may extend it from
    /// <c>profiles/anticheat-policy.json</c>; they cannot weaken a built-in entry.
    /// </para>
    /// </summary>
    public static readonly ImmutableArray<AntiCheatSignature> BuiltInSignatures =
    [
        new AntiCheatSignature
        {
            // Battlefield 6 and other current EA titles. The service is a user-mode host
            // (type 16) whose kernel component loads with the game, which is precisely why
            // installed-but-stopped still counts as protected.
            Vendor = "EA Javelin",
            Tier = AntiCheatTier.Kernel,
            Services = ["EAAntiCheatService"],
            Processes = ["eaanticheat.gameservice.exe", "eaanticheat.gameservicelauncher.exe"],
            FileMarkers =
            [
                "eaanticheat",
                "eaanticheat.cfg",
                "eaanticheat.gameservicelauncher.exe",
                "eajavelininstaller_installscript.vdf",
            ],
        },
        new AntiCheatSignature
        {
            Vendor = "Riot Vanguard",
            Tier = AntiCheatTier.Kernel,
            Services = ["vgc", "vgk"],
            Drivers = ["vgk.sys"],
            Processes = ["vgtray.exe", "vgc.exe"],
        },
        new AntiCheatSignature
        {
            Vendor = "EasyAntiCheat",
            Tier = AntiCheatTier.Kernel,
            Services = ["easyanticheat", "easyanticheat_eos"],
            Drivers = ["easyanticheat.sys", "easyanticheat_eos.sys"],
            Processes = ["easyanticheat.exe", "start_protected_game.exe"],
            FileMarkers = ["easyanticheat", "easyanticheat_eos"],
        },
        new AntiCheatSignature
        {
            Vendor = "BattlEye",
            Tier = AntiCheatTier.Kernel,
            Services = ["beservice"],
            Drivers = ["bedaisy.sys"],
            Processes = ["beservice.exe"],
            FileMarkers = ["battleye", "beclient_x64.dll"],
        },
        new AntiCheatSignature
        {
            Vendor = "XIGNCODE3",
            Tier = AntiCheatTier.Kernel,
            Services = ["xhunter1"],
            Drivers = ["xhunter1.sys"],
            FileMarkers = ["xigncode"],
        },
        new AntiCheatSignature
        {
            Vendor = "nProtect GameGuard",
            Tier = AntiCheatTier.Kernel,
            Services = ["npggsvc"],
            Drivers = ["npggnt.des"],
            Processes = ["gamemon.des"],
        },
        new AntiCheatSignature
        {
            Vendor = "FACEIT Anti-Cheat",
            Tier = AntiCheatTier.Kernel,
            Services = ["faceit"],
            Drivers = ["faceit.sys"],
        },
        new AntiCheatSignature
        {
            Vendor = "Ricochet",
            Tier = AntiCheatTier.Kernel,
            Drivers = ["ricochet.sys"],
            Services = ["ricochet"],
            FileMarkers = ["ricochet"],
        },
        new AntiCheatSignature
        {
            Vendor = "mhyprot (miHoYo)",
            Tier = AntiCheatTier.Kernel,
            Drivers = ["mhyprot2.sys", "mhyprot3.sys"],
            Services = ["mhyprot2", "mhyprot3"],
        },
        new AntiCheatSignature
        {
            Vendor = "Tencent ACE",
            Tier = AntiCheatTier.Kernel,
            Drivers = ["ace-base.sys", "acegmer.sys"],
            Services = ["aceammo", "acegame"],
            FileMarkers = ["anticheatexpert"],
        },
        new AntiCheatSignature
        {
            Vendor = "Hyperion (Roblox)",
            Tier = AntiCheatTier.Kernel,
            FileMarkers = ["robloxplayerbeta.exe"],
        },
        new AntiCheatSignature
        {
            Vendor = "Denuvo Anti-Cheat",
            Tier = AntiCheatTier.Kernel,
            Drivers = ["denuvo.sys"],
            Services = ["denuvo"],
            FileMarkers = ["anticheat_launcher.exe"],
        },
        new AntiCheatSignature
        {
            Vendor = "Valve Anti-Cheat",
            Tier = AntiCheatTier.UserMode,
            FileMarkers = ["steam_api64.dll", "steam_api.dll"],
        },
    ];

    /// <summary>
    /// Naming fragments that suggest an anti-cheat nobody has catalogued yet.
    ///
    /// <para>
    /// The named signature list can only ever describe products that existed when it was
    /// written, and new anti-cheats ship constantly. This heuristic catches the rest by
    /// name, and it is deliberately generous: a false positive costs a few contact levers
    /// on one title, while a false negative is the failure this project cannot afford.
    /// </para>
    ///
    /// <para>
    /// Matched only against kernel drivers and service names — never against arbitrary
    /// files in a game folder, where these fragments appear far too often to be meaningful.
    /// </para>
    /// </summary>
    private static readonly ImmutableArray<string> HeuristicFragments =
        ["anticheat", "anti-cheat", "anti_cheat", "cheatprot", "gameguard",
         "gameprotect", "antihack", "anti-hack", "hackshield", "battleye",
         "easyanti", "vanguard", "faceit", "esportal", "cheatblocker"];

    public static AntiCheatAssessment Assess(AntiCheatEnvironment environment) =>
        Assess(environment, BuiltInSignatures);

    public static AntiCheatAssessment Assess(
        AntiCheatEnvironment environment,
        ImmutableArray<AntiCheatSignature> signatures)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var services = ToSet(environment.InstalledServices);
        var drivers = ToSet(environment.LoadedDrivers);
        var processes = ToSet(environment.RunningProcesses);
        var files = ToSet(environment.GameDirectoryEntries);

        var findings = ImmutableArray.CreateBuilder<AntiCheatFinding>();

        foreach (var signature in signatures)
        {
            var evidence =
                FirstMatch(signature.Services, services, "service")
                ?? FirstMatch(signature.Drivers, drivers, "driver")
                ?? FirstMatch(signature.Processes, processes, "process")
                ?? FirstMatch(signature.FileMarkers, files, "file");

            if (evidence is not null)
            {
                findings.Add(new AntiCheatFinding(signature.Vendor, signature.Tier, evidence));
            }
        }

        // Nothing named matched. Before concluding anything, look for an anti-cheat this
        // catalogue has never heard of — new products ship constantly, and a list written
        // today cannot describe one released tomorrow.
        if (findings.Count == 0)
        {
            var heuristic = FindByNamingHeuristic(environment);
            if (heuristic is not null)
            {
                findings.Add(heuristic);
            }
        }

        var ordered = findings
            .OrderByDescending(f => f.Tier)
            .ThenBy(f => f.Vendor, StringComparer.Ordinal)
            .ToImmutableArray();

        // Fail safe. No match means Unknown, not None — and Unknown is treated exactly as
        // strictly as Kernel everywhere downstream.
        var tier = ordered.IsEmpty ? AntiCheatTier.Unknown : ordered[0].Tier;

        return new AntiCheatAssessment { Tier = tier, Findings = ordered };
    }

    /// <summary>
    /// Looks for an uncatalogued anti-cheat by name among kernel drivers and services.
    /// Reported as <see cref="AntiCheatTier.Kernel"/>, because something calling itself an
    /// anti-cheat and living in that layer should be treated as one until proven otherwise.
    /// </summary>
    private static AntiCheatFinding? FindByNamingHeuristic(AntiCheatEnvironment environment)
    {
        foreach (var (values, kind) in new[]
                 {
                     (environment.LoadedDrivers, "driver"),
                     (environment.InstalledServices, "service"),
                 })
        {
            if (values.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                foreach (var fragment in HeuristicFragments)
                {
                    if (value.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    {
                        return new AntiCheatFinding(
                            "Unrecognised anti-cheat",
                            AntiCheatTier.Kernel,
                            $"{kind} '{value}' matches the naming pattern '{fragment}'");
                    }
                }
            }
        }

        return null;
    }

    private static string? FirstMatch(
        ImmutableArray<string> candidates,
        HashSet<string> present,
        string kind)
    {
        if (candidates.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (present.Contains(candidate))
            {
                return $"{kind} '{candidate}'";
            }
        }

        return null;
    }

    private static HashSet<string> ToSet(ImmutableArray<string> values)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (values.IsDefaultOrEmpty)
        {
            return set;
        }

        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                set.Add(value.Trim());
            }
        }

        return set;
    }
}
