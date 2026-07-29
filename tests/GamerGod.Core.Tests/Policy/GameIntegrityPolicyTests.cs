using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using Xunit;

namespace GamerGod.Core.Tests.Policy;

/// <summary>
/// Charter Article I says GamerGod will never break a game, and that the guarantee is
/// architectural rather than a matter of care. These are the tests that make that true.
///
/// <para>
/// The headline case is <see cref="Battlefield6_gets_ambient_only_treatment"/>, whose
/// fixture is taken verbatim from a real machine with Battlefield 6 installed — the service
/// name, the marker files and the executable are what was actually on disk, not what a wiki
/// says should be there.
/// </para>
/// </summary>
public sealed class GameIntegrityPolicyTests
{
    // ------------------------------------------------------------------
    // Environments captured from a real Windows 11 25H2 machine.
    // ------------------------------------------------------------------

    /// <summary>
    /// Battlefield 6 as actually installed: EA's Javelin host service is registered but
    /// stopped (its kernel component loads with the game), and the anti-cheat marker files
    /// sit beside bf6.exe.
    /// </summary>
    private static AntiCheatEnvironment Battlefield6() => new()
    {
        InstalledServices = ["EAAntiCheatService", "BEService", "EasyAntiCheat", "WSearch", "SysMain"],
        LoadedDrivers = ["ntoskrnl.exe", "amd3dvcache.sys", "AmdPPM.sys", "RTCore64.sys"],
        RunningProcesses = ["explorer.exe", "steam.exe", "msedge.exe"],
        GameDirectoryEntries =
        [
            "bf6.exe",
            "EAAntiCheat",
            "EAAntiCheat.cfg",
            "EAAntiCheat.GameServiceLauncher.exe",
            "EAJavelinInstaller_installscript.vdf",
            "steam_api64.dll",
            "nvngx_dlss.dll",
        ],
    };

    private static AntiCheatEnvironment Valorant() => new()
    {
        InstalledServices = ["vgc"],
        LoadedDrivers = ["vgk.sys", "ntoskrnl.exe"],
        RunningProcesses = ["vgtray.exe"],
        GameDirectoryEntries = ["VALORANT-Win64-Shipping.exe"],
    };

    private static AntiCheatEnvironment FortniteStyle() => new()
    {
        InstalledServices = ["EasyAntiCheat_EOS"],
        GameDirectoryEntries = ["FortniteClient-Win64-Shipping.exe", "EasyAntiCheat_EOS"],
    };

    private static AntiCheatEnvironment BattlEyeTitle() => new()
    {
        InstalledServices = ["BEService"],
        LoadedDrivers = ["BEDaisy.sys"],
        GameDirectoryEntries = ["R6-Siege.exe", "BattlEye"],
    };

    /// <summary>A single-player title on Steam: VAC only, no kernel component anywhere.</summary>
    private static AntiCheatEnvironment SinglePlayerSteamTitle() => new()
    {
        InstalledServices = ["WSearch", "SysMain", "Spooler"],
        LoadedDrivers = ["ntoskrnl.exe", "amd3dvcache.sys"],
        RunningProcesses = ["explorer.exe", "steam.exe"],
        GameDirectoryEntries = ["Cyberpunk2077.exe", "steam_api64.dll"],
    };

    /// <summary>A DRM-free game with no anti-cheat markers at all.</summary>
    private static AntiCheatEnvironment BareEnvironment() => new()
    {
        InstalledServices = ["WSearch"],
        GameDirectoryEntries = ["game.exe"],
    };

    // ------------------------------------------------------------------
    // The headline guarantee.
    // ------------------------------------------------------------------

    [Fact]
    public void Battlefield6_gets_ambient_only_treatment()
    {
        var assessment = AntiCheatDetector.Assess(Battlefield6());

        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
        Assert.Contains(assessment.Findings, f => f.Vendor == "EA Javelin");
        Assert.False(assessment.ContactIsConsidered);

        // Even with the user explicitly opting in, contact is refused.
        var permit = GameIntegrityPolicy.Evaluate(
            "Battlefield 6", assessment, ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly);
        Assert.Null(permit.Contact);
        Assert.Contains("kernel anti-cheat", permit.Explain(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Battlefield6_is_detected_even_though_the_service_is_stopped()
    {
        // EA's Javelin host service is type 16 and start type 3 (on demand): it sits
        // stopped until a protected game launches. Requiring it to be running would report
        // "no anti-cheat" right up until the moment that answer becomes dangerous.
        var environment = Battlefield6() with { RunningProcesses = [] };

        var assessment = AntiCheatDetector.Assess(environment);

        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
        Assert.Contains(assessment.Findings, f => f.Vendor == "EA Javelin");
    }

    [Fact]
    public void Battlefield6_is_still_detected_from_files_alone_on_a_clean_machine()
    {
        // A fresh install where the service has not been registered yet: the marker files
        // beside bf6.exe are enough on their own.
        var environment = new AntiCheatEnvironment
        {
            GameDirectoryEntries = ["bf6.exe", "EAAntiCheat.cfg", "EAAntiCheat"],
        };

        Assert.Equal(AntiCheatTier.Kernel, AntiCheatDetector.Assess(environment).Tier);
    }

    [Theory]
    [InlineData("Valorant")]
    [InlineData("Fortnite")]
    [InlineData("Rainbow Six Siege")]
    public void Every_kernel_anticheat_title_is_ambient_only(string title)
    {
        var environment = title switch
        {
            "Valorant" => Valorant(),
            "Fortnite" => FortniteStyle(),
            _ => BattlEyeTitle(),
        };

        var permit = GameIntegrityPolicy.Evaluate(
            title, environment, ContactPreference.WhenProvablySafe);

        Assert.Equal(AntiCheatTier.Kernel, permit.Assessment.Tier);
        Assert.True(permit.IsAmbientOnly);
    }

    // ------------------------------------------------------------------
    // Fail-safe: unknown must behave exactly like kernel.
    // ------------------------------------------------------------------

    [Fact]
    public void An_unrecognised_environment_is_treated_as_protected_not_as_safe()
    {
        var assessment = AntiCheatDetector.Assess(BareEnvironment());

        Assert.Equal(AntiCheatTier.Unknown, assessment.Tier);
        Assert.False(assessment.ContactIsConsidered);

        var permit = GameIntegrityPolicy.Evaluate(
            "Unknown Game", assessment, ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly);
    }

    [Fact]
    public void A_contact_grant_cannot_be_forged_for_a_protected_tier()
    {
        // Defence in depth: even calling the internal factory directly is refused.
        Assert.Throws<InvalidOperationException>(
            () => ContactGrant.Issue("Battlefield 6", AntiCheatTier.Kernel));
        Assert.Throws<InvalidOperationException>(
            () => ContactGrant.Issue("Anything", AntiCheatTier.Unknown));
    }

    // ------------------------------------------------------------------
    // Contact is opt-in, never a default.
    // ------------------------------------------------------------------

    /// <summary>
    /// A workload positively identified as carrying no anti-cheat — an emulator, which is
    /// the honest source of <see cref="AntiCheatTier.None"/>. Nothing GamerGod can observe
    /// about an arbitrary game folder establishes this; a positive identification does.
    /// </summary>
    private static AntiCheatAssessment PositivelyIdentifiedEmulator() => new()
    {
        Tier = AntiCheatTier.None,
        Findings = [],
    };

    [Fact]
    public void Being_on_steam_is_not_evidence_that_a_game_is_safe_to_touch()
    {
        // steam_api64.dll ships with nearly every Steam game. It establishes that the game
        // uses Steam, not that Valve's user-mode anti-cheat is the only protection present.
        // Treating it as conclusive would let an uncatalogued kernel anti-cheat pass as
        // user-mode - see SteamMarkerRegressionTests for the defect this replaced.
        var assessment = AntiCheatDetector.Assess(SinglePlayerSteamTitle());

        Assert.Equal(AntiCheatTier.Unknown, assessment.Tier);
        Assert.False(assessment.ContactIsConsidered);
        Assert.Contains(assessment.Findings, f => f.Vendor == "Valve Anti-Cheat");

        var permit = GameIntegrityPolicy.Evaluate(
            "Cyberpunk 2077", assessment, ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly);
    }

    [Fact]
    public void Contact_is_refused_by_default_even_when_provably_safe()
    {
        var assessment = PositivelyIdentifiedEmulator();

        Assert.Equal(AntiCheatTier.None, assessment.Tier);
        Assert.True(assessment.ContactIsConsidered);

        // No preference supplied - the default must be Never, even here.
        var permit = GameIntegrityPolicy.Evaluate("PCSX2", assessment);

        Assert.True(permit.IsAmbientOnly);
    }

    [Fact]
    public void Contact_is_granted_only_when_opted_in_and_provably_safe()
    {
        var assessment = PositivelyIdentifiedEmulator();

        var permit = GameIntegrityPolicy.Evaluate(
            "PCSX2", assessment, ContactPreference.WhenProvablySafe);

        Assert.False(permit.IsAmbientOnly);
        Assert.NotNull(permit.Contact);
        Assert.Equal("PCSX2", permit.Contact!.Title);
        Assert.Equal(AntiCheatTier.None, permit.Contact.ObservedTier);
    }

    // ------------------------------------------------------------------
    // Filtering: ambient always survives, contact never does when refused.
    // ------------------------------------------------------------------

    [Fact]
    public void Filtering_keeps_every_ambient_mutation_and_drops_contact_ones()
    {
        var permit = GameIntegrityPolicy.Evaluate(
            "Battlefield 6", Battlefield6(), ContactPreference.WhenProvablySafe);

        var proposed = new IMutation[]
        {
            new FakeMutation("power:scheme", MutationTier.Power, MutationVisibility.Ambient),
            new FakeMutation("service:WSearch", MutationTier.Service, MutationVisibility.Ambient),
            new FakeMutation("ecoqos:msedge", MutationTier.ProcessDemotion, MutationVisibility.Ambient),
            new FakeMutation("job:ambient-domain", MutationTier.ProcessDemotion, MutationVisibility.Ambient),
            new FakeMutation("shell:explorer", MutationTier.Shell, MutationVisibility.Ambient),
            new FakeMutation("cpuset:bf6.exe", MutationTier.CpuRouting, MutationVisibility.Contact),
            new FakeMutation("layers:bf6.exe", MutationTier.Registry, MutationVisibility.Contact),
        };

        var allowed = permit.Filter(proposed);

        Assert.Equal(5, allowed.Length);
        Assert.All(allowed, m => Assert.Equal(MutationVisibility.Ambient, m.Visibility));
        Assert.DoesNotContain(allowed, m => m.Key.Contains("bf6.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void An_ambient_only_session_still_keeps_the_levers_that_matter_most()
    {
        // The point of the Ambient/Contact split is that refusing to touch the game costs
        // very little: domain confinement of everything else, EcoQoS and service
        // suppression all survive. This test documents that intent so a future change that
        // reclassifies one of them as Contact fails loudly.
        var permit = GameIntegrityPolicy.Evaluate("Battlefield 6", Battlefield6());

        var levers = new IMutation[]
        {
            new FakeMutation("job:ambient-domain", MutationTier.ProcessDemotion, MutationVisibility.Ambient),
            new FakeMutation("ecoqos:background", MutationTier.ProcessDemotion, MutationVisibility.Ambient),
            new FakeMutation("service:WSearch", MutationTier.Service, MutationVisibility.Ambient),
            new FakeMutation("irq:steering", MutationTier.Registry, MutationVisibility.Ambient),
            new FakeMutation("power:scheme", MutationTier.Power, MutationVisibility.Ambient),
        };

        Assert.Equal(levers.Length, permit.Filter(levers).Length);
    }

    [Fact]
    public void Every_builtin_signature_that_is_kernel_tier_blocks_contact()
    {
        foreach (var signature in AntiCheatDetector.BuiltInSignatures
                     .Where(s => s.Tier == AntiCheatTier.Kernel))
        {
            var marker = signature.Services.FirstOrDefault()
                         ?? signature.Drivers.FirstOrDefault()
                         ?? signature.Processes.FirstOrDefault()
                         ?? signature.FileMarkers.FirstOrDefault();

            Assert.NotNull(marker);

            var environment = new AntiCheatEnvironment
            {
                InstalledServices = signature.Services.IsDefaultOrEmpty ? [] : [signature.Services[0]],
                LoadedDrivers = signature.Drivers.IsDefaultOrEmpty ? [] : [signature.Drivers[0]],
                RunningProcesses = signature.Processes.IsDefaultOrEmpty ? [] : [signature.Processes[0]],
                GameDirectoryEntries = signature.FileMarkers.IsDefaultOrEmpty ? [] : [signature.FileMarkers[0]],
            };

            var permit = GameIntegrityPolicy.Evaluate(
                signature.Vendor, environment, ContactPreference.WhenProvablySafe);

            Assert.True(
                permit.IsAmbientOnly,
                $"{signature.Vendor} is kernel tier but did not force ambient-only mode.");
        }
    }

    [Fact]
    public void Detection_reports_the_strongest_tier_when_several_products_are_present()
    {
        // This machine has Javelin, BattlEye and EasyAntiCheat all installed, plus Steam's
        // user-mode VAC markers. The strictest one must win.
        var assessment = AntiCheatDetector.Assess(Battlefield6());

        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
        Assert.True(assessment.Findings.Length > 1);
        Assert.Equal(AntiCheatTier.Kernel, assessment.Findings[0].Tier);
    }

    /// <summary>Minimal stand-in; the ledger's own behaviour is tested separately.</summary>
    private sealed class FakeMutation(string key, MutationTier tier, MutationVisibility visibility)
        : IMutation
    {
        public string Key => key;

        public MutationTier Tier => tier;

        public MutationVisibility Visibility => visibility;

        public bool IsBootPersistent => false;

        public string Describe() => key;

        public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);

        public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
