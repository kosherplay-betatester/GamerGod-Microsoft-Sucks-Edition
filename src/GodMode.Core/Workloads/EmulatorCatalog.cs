using System.Collections.Immutable;

namespace GodMode.Core.Workloads;

/// <summary>
/// A tuned profile for one emulator.
///
/// <para>
/// Every field here is a <em>starting hypothesis</em>, not a claim. Charter Article VII
/// forbids asserting a benefit that has not been measured, so these values seed the
/// autotuner rather than being applied as gospel — the measured result on the user's own
/// machine always wins. <see cref="Verified"/> records whether anyone has actually measured
/// this profile yet.
/// </para>
/// </summary>
public sealed record EmulatorProfile
{
    public required string Name { get; init; }

    /// <summary>Consoles this emulator targets, for display and library grouping.</summary>
    public required ImmutableArray<string> Systems { get; init; }

    /// <summary>Process names, without extension, matched case-insensitively.</summary>
    public required ImmutableArray<string> ProcessNames { get; init; }

    public required ThreadModel ThreadModel { get; init; }

    /// <summary>
    /// Physical cores this emulator can actually keep busy. Reserving more than this
    /// achieves nothing and needlessly shrinks the ambient domain, which hurts everything
    /// else on the machine.
    /// </summary>
    public required int UsefulPhysicalCores { get; init; }

    /// <summary>
    /// The console's exact native refresh rate. This is the number that matters: an
    /// emulator is not trying to run fast, it is trying to reproduce a fixed cadence.
    /// A title running at a steady 60.00 Hz when the console ran at 59.94 Hz produces a
    /// visible judder beat roughly every sixteen seconds, and no average-FPS readout will
    /// ever show it.
    /// </summary>
    public required double NativeRefreshHz { get; init; }

    /// <summary>
    /// True when this emulator historically responds better to cache than to clock speed —
    /// the case for interpreter-heavy and recompiler-heavy cores with large working sets.
    /// </summary>
    public required bool PrefersCacheOverFrequency { get; init; }

    /// <summary>
    /// True when the emulator is known to schedule poorly across SMT siblings, so routing
    /// should prefer one logical processor per physical core.
    /// </summary>
    public bool PreferPhysicalCoresOnly { get; init; }

    /// <summary>
    /// Whether this profile's routing has been validated by measurement. Everything ships
    /// false; the autotuner and community submissions flip it to true with evidence.
    /// </summary>
    public bool Verified { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// Known emulators, from 1980s consoles to current-generation.
///
/// <para>
/// Emulators are the single most favourable workload GodMode has. They carry no anti-cheat,
/// so they are <see cref="Policy.AntiCheatTier.None"/> and every contact lever is available.
/// They are also dominated by a handful of latency-critical threads that respond strongly to
/// a large cache and to not being interrupted — which is precisely what domain partitioning
/// provides.
/// </para>
/// </summary>
public static class EmulatorCatalog
{
    public static readonly ImmutableArray<EmulatorProfile> All =
    [
        // ---------------- Current and recent generation ----------------
        new()
        {
            Name = "RPCS3",
            Systems = ["PlayStation 3"],
            ProcessNames = ["rpcs3"],
            ThreadModel = ThreadModel.ManyThreads,
            UsefulPhysicalCores = 8,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = true,
            Notes = "PPU thread plus up to six SPU threads. One of the few emulators that "
                  + "genuinely scales wide, and a strong beneficiary of a large cache domain.",
        },
        new()
        {
            Name = "shadPS4",
            Systems = ["PlayStation 4"],
            ProcessNames = ["shadps4"],
            ThreadModel = ThreadModel.ManyThreads,
            UsefulPhysicalCores = 8,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = true,
            Notes = "Early-stage. Characteristics likely to change substantially.",
        },
        new()
        {
            Name = "Xenia",
            Systems = ["Xbox 360"],
            ProcessNames = ["xenia", "xenia_canary"],
            ThreadModel = ThreadModel.ManyThreads,
            UsefulPhysicalCores = 6,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = true,
            Notes = "Emulates three PowerPC cores with two hardware threads each.",
        },
        new()
        {
            Name = "Ryujinx",
            Systems = ["Nintendo Switch"],
            ProcessNames = ["ryujinx"],
            ThreadModel = ThreadModel.ManyThreads,
            UsefulPhysicalCores = 6,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = true,
            Notes = "Managed .NET host. Shader compilation is the dominant stutter source, "
                  + "which Stutter Forensics can attribute directly.",
        },
        new()
        {
            Name = "Vita3K",
            Systems = ["PlayStation Vita"],
            ProcessNames = ["vita3k"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = true,
        },
        new()
        {
            Name = "Cemu",
            Systems = ["Wii U"],
            ProcessNames = ["cemu"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = true,
            Notes = "Historically single-thread bound; later builds spread wider.",
        },

        // ---------------- Sixth and seventh generation ----------------
        new()
        {
            Name = "PCSX2",
            Systems = ["PlayStation 2"],
            ProcessNames = ["pcsx2", "pcsx2-qt", "pcsx2x64", "pcsx2x64-avx2"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = true,
            Notes = "Emotion Engine, VU and GS threads. Strongly single-thread sensitive and "
                  + "a textbook beneficiary of a large last-level cache.",
        },
        new()
        {
            Name = "Dolphin",
            Systems = ["GameCube", "Wii"],
            ProcessNames = ["dolphin", "dolphinqt2", "dolphinwx"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 3,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = true,
            Notes = "CPU thread plus GPU thread. Dual-core mode is the common configuration.",
        },
        new()
        {
            Name = "xemu",
            Systems = ["Xbox"],
            ProcessNames = ["xemu"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 3,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = true,
        },
        new()
        {
            Name = "Flycast",
            Systems = ["Dreamcast", "Naomi", "Atomiswave"],
            ProcessNames = ["flycast"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 2,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "DuckStation",
            Systems = ["PlayStation"],
            ProcessNames = ["duckstation-qt-x64-releaseltcg", "duckstation-qt", "duckstation"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 2,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = false,
            Notes = "Light on CPU. Frame pacing, not throughput, is the entire game here.",
        },
        new()
        {
            Name = "PPSSPP",
            Systems = ["PlayStation Portable"],
            ProcessNames = ["ppssppwindows", "ppssppwindows64", "ppsspp"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 2,
            NativeRefreshHz = 59.94,
            PrefersCacheOverFrequency = false,
        },

        // ---------------- Old school ----------------
        new()
        {
            Name = "melonDS",
            Systems = ["Nintendo DS", "Nintendo DSi"],
            ProcessNames = ["melonds"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 2,
            NativeRefreshHz = 59.8261,
            PrefersCacheOverFrequency = false,
            Notes = "The DS refresh is 59.8261 Hz, far enough from 60 to beat visibly against "
                  + "an uncorrected 60 Hz display.",
        },
        new()
        {
            Name = "DeSmuME",
            Systems = ["Nintendo DS"],
            ProcessNames = ["desmume"],
            ThreadModel = ThreadModel.SingleThreadBound,
            UsefulPhysicalCores = 1,
            NativeRefreshHz = 59.8261,
            PrefersCacheOverFrequency = true,
        },
        new()
        {
            Name = "bsnes / higan",
            Systems = ["Super Nintendo"],
            ProcessNames = ["bsnes", "higan", "ares"],
            ThreadModel = ThreadModel.SingleThreadBound,
            UsefulPhysicalCores = 1,
            NativeRefreshHz = 60.0988,
            PrefersCacheOverFrequency = true,
            PreferPhysicalCoresOnly = true,
            Notes = "Cycle-accurate cores are aggressively single-thread bound. This is the "
                  + "purest case in the catalogue for one fast, uninterrupted core.",
        },
        new()
        {
            Name = "Snes9x",
            Systems = ["Super Nintendo"],
            ProcessNames = ["snes9x", "snes9x-x64"],
            ThreadModel = ThreadModel.SingleThreadBound,
            UsefulPhysicalCores = 1,
            NativeRefreshHz = 60.0988,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "mGBA",
            Systems = ["Game Boy Advance", "Game Boy", "Game Boy Color"],
            ProcessNames = ["mgba"],
            ThreadModel = ThreadModel.SingleThreadBound,
            UsefulPhysicalCores = 1,
            NativeRefreshHz = 59.7275,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "Mesen",
            Systems = ["NES", "Super Nintendo", "Game Boy", "PC Engine"],
            ProcessNames = ["mesen"],
            ThreadModel = ThreadModel.SingleThreadBound,
            UsefulPhysicalCores = 1,
            NativeRefreshHz = 60.0988,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "RetroArch",
            Systems = ["Multi-system"],
            ProcessNames = ["retroarch"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 2,
            NativeRefreshHz = 0, // Determined by the loaded core, not the frontend.
            PrefersCacheOverFrequency = true,
            Notes = "Characteristics depend entirely on the loaded core, so routing is "
                  + "resolved per-core rather than per-frontend. Run-ahead multiplies CPU "
                  + "cost by the frame count it predicts.",
        },
    ];

    /// <summary>
    /// Android and other virtualised guests.
    ///
    /// <para>
    /// These run their workload inside a hypervisor, so the levers apply to the host's
    /// virtual machine worker processes rather than to the guest. Windows Subsystem for
    /// Android is deliberately absent: Microsoft ended support on 5 March 2025, and
    /// recommending a dead runtime would be dishonest.
    /// </para>
    /// </summary>
    public static readonly ImmutableArray<EmulatorProfile> VirtualizedGuests =
    [
        new()
        {
            Name = "Google Play Games on PC",
            Systems = ["Android"],
            ProcessNames = ["crosvm", "service", "googleplaygames"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
            Notes = "Google's official Android runtime for Windows. The most legitimate "
                  + "Android path now that WSA is gone.",
        },
        new()
        {
            Name = "BlueStacks",
            Systems = ["Android"],
            ProcessNames = ["hd-player", "bluestacks", "bstksvc"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
            Notes = "Hypervisor-backed. Can conflict with kernel anti-cheat titles; the "
                  + "hazard scan warns when both are present.",
        },
        new()
        {
            Name = "LDPlayer",
            Systems = ["Android"],
            ProcessNames = ["dnplayer", "ldplayer"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "MEmu",
            Systems = ["Android"],
            ProcessNames = ["memu", "memuheadless"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "GameLoop",
            Systems = ["Android"],
            ProcessNames = ["androidemulator", "aowexec"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
        },
        new()
        {
            Name = "NoxPlayer",
            Systems = ["Android"],
            ProcessNames = ["nox", "noxvmhandle"],
            ThreadModel = ThreadModel.FewHeavyThreads,
            UsefulPhysicalCores = 4,
            NativeRefreshHz = 60.0,
            PrefersCacheOverFrequency = false,
        },
    ];

    /// <summary>
    /// Identifies a process. Returns null when it is not a known emulator or guest.
    /// </summary>
    public static EmulatorProfile? Identify(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(processName.Trim());

        foreach (var profile in All)
        {
            if (profile.ProcessNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        foreach (var profile in VirtualizedGuests)
        {
            if (profile.ProcessNames.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase)))
            {
                return profile;
            }
        }

        return null;
    }

    public static WorkloadClass ClassifyProcess(string processName)
    {
        var profile = Identify(processName);
        if (profile is null)
        {
            return WorkloadClass.Unknown;
        }

        return VirtualizedGuests.Contains(profile) ? WorkloadClass.VirtualizedGuest : WorkloadClass.Emulator;
    }

    /// <summary>Every distinct console or platform GodMode has a tuned profile for.</summary>
    public static ImmutableArray<string> SupportedSystems =>
        All.Concat(VirtualizedGuests)
            .SelectMany(p => p.Systems)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
}
