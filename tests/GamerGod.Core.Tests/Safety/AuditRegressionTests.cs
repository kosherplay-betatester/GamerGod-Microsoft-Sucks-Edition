using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Safety;

/// <summary>
/// Regressions for two defects found by an adversarial audit, both of which had survived the
/// existing suite because the tests were written by the same person who wrote the code.
/// </summary>
public sealed class LaptopThermalRegressionTests
{
    /// <summary>
    /// Laptop thermal management software. None of this was on the denylist, which meant
    /// GamerGod would happily suspend the only thing standing between a gaming laptop and
    /// its thermal limit.
    ///
    /// <para>
    /// Laptops matter more here than desktops, not less: they run hotter, have far less
    /// headroom, and depend on software for far more of their thermal behaviour. They are
    /// also the machines GamerGod helps most, because they are the most contended.
    /// </para>
    /// </summary>
    public static TheoryData<string, string> LaptopThermalSoftware() => new()
    {
        { "esif_uf.exe", "Intel DPTF - thermal throttling on most Intel laptops" },
        { "ipf_uf.exe", "Intel Innovation Platform Framework" },
        { "NoteBookFanControl.exe", "NoteBook FanControl" },
        { "nbfc.exe", "NoteBook FanControl service" },
        { "ControlCenter.exe", "Clevo and Tongfang chassis" },
        { "MSI Center.exe", "MSI laptops" },
        { "Dragon Center.exe", "MSI Dragon Center" },
        { "OmenCommandCenterBackground.exe", "HP OMEN" },
        { "PredatorSense.exe", "Acer Predator" },
        { "NitroSense.exe", "Acer Nitro" },
        { "AWCC.exe", "Alienware Command Center" },
        { "AWCCServiceController.exe", "Alienware service" },
        { "LenovoLegionToolkit.exe", "Lenovo Legion" },
        { "LegionZone.exe", "Lenovo Legion Zone" },
        { "AsusOptimization.exe", "ASUS ROG fan curves" },
        { "GHelper.exe", "G-Helper" },
        { "ThrottleStop.exe", "turbo and thermal limits" },
        { "RyzenAdj.exe", "APU power and thermal limits" },
        { "HandheldCompanion.exe", "handheld TDP and fan behaviour" },
    };

    [Theory]
    [MemberData(nameof(LaptopThermalSoftware))]
    public void Laptop_thermal_software_can_never_be_suspended(string process, string why)
    {
        var protection = ProcessSafetyPolicy.Explain(process);

        Assert.True(
            protection is not null,
            $"{process} ({why}) is not protected. Suspending it freezes the fan curve while "
            + "the game keeps producing heat.");
        Assert.Equal(ProcessRisk.Thermal, protection!.Risk);
    }

    [Fact]
    public void Intel_thermal_framework_is_singled_out_because_it_is_nearly_universal()
    {
        // esif_uf.exe is present on the large majority of Intel laptops and is the component
        // that actually enforces thermal limits. If exactly one entry in this list matters,
        // it is this one.
        var dptf = ProcessSafetyPolicy.Explain("esif_uf.exe");

        Assert.NotNull(dptf);
        Assert.Equal(ProcessRisk.Thermal, dptf!.Risk);
        Assert.Contains("thermal", dptf.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_mixed_suspension_list_still_proceeds_with_what_is_safe()
    {
        var (suspendable, blocked) = ProcessSafetyPolicy.Filter(
            ["chrome.exe", "esif_uf.exe", "Spotify.exe", "PredatorSense.exe", "Slack.exe"]);

        Assert.Equal(["chrome.exe", "Spotify.exe", "Slack.exe"], suspendable.ToArray());
        Assert.Equal(2, blocked.Length);
        Assert.All(blocked, b => Assert.Equal(ProcessRisk.Thermal, b.Risk));
    }
}

/// <summary>
/// The more serious of the two findings: a hole in the guarantee the whole product rests on.
/// </summary>
public sealed class SteamMarkerRegressionTests
{
    /// <summary>
    /// The defect. Nearly every Steam game ships <c>steam_api64.dll</c>. That matched the
    /// Valve signature, the Valve signature declared user-mode, and the uncatalogued-anti-cheat
    /// heuristic only ran when <em>nothing</em> matched.
    ///
    /// <para>
    /// So a kernel anti-cheat that this catalogue has never heard of, shipping on a Steam
    /// title, was reported as user-mode only — and became eligible for contact. The most
    /// common file in PC gaming was quietly defeating the fail-safe.
    /// </para>
    /// </summary>
    [Fact]
    public void An_uncatalogued_kernel_anticheat_on_a_steam_game_is_still_ambient_only()
    {
        var environment = new AntiCheatEnvironment
        {
            InstalledServices = ["NewVendorAntiCheatService", "Steam Client Service"],
            GameDirectoryEntries = ["newgame.exe", "steam_api64.dll"],
        };

        var assessment = AntiCheatDetector.Assess(environment);

        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
        Assert.False(assessment.ContactIsConsidered);

        var permit = GameIntegrityPolicy.Evaluate(
            "Uncatalogued Steam Title", assessment, ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly);
    }

    [Fact]
    public void Steam_alone_never_establishes_that_a_game_is_safe_to_touch()
    {
        // Detecting Steam is not the same as establishing that Valve's user-mode anti-cheat
        // is the only protection present. The finding is still reported - it is true, and
        // useful - but it must not raise confidence.
        var assessment = AntiCheatDetector.Assess(new AntiCheatEnvironment
        {
            GameDirectoryEntries = ["somegame.exe", "steam_api64.dll"],
        });

        Assert.Contains(assessment.Findings, f => f.Vendor == "Valve Anti-Cheat");
        Assert.False(assessment.Findings.Single(f => f.Vendor == "Valve Anti-Cheat").EstablishesTier);

        Assert.Equal(AntiCheatTier.Unknown, assessment.Tier);
        Assert.False(assessment.ContactIsConsidered);
    }

    [Fact]
    public void The_heuristic_runs_even_when_a_catalogued_product_already_matched()
    {
        // This is the structural fix. The heuristic used to be skipped once anything matched,
        // so a weak match could mask a strong signal sitting right next to it.
        var assessment = AntiCheatDetector.Assess(new AntiCheatEnvironment
        {
            InstalledServices = ["SomeGameAntiCheatDriver"],
            GameDirectoryEntries = ["steam_api64.dll"],
        });

        Assert.Contains(assessment.Findings, f => f.Vendor == "Unrecognised anti-cheat");
        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
    }

    [Theory]
    [InlineData("MyGameAntiCheat")]
    [InlineData("acme-anti-cheat")]
    [InlineData("SuperGameGuard")]
    [InlineData("NewHackShield")]
    [InlineData("StudioAntiHack")]
    public void Uncatalogued_products_are_caught_by_name_alongside_a_steam_marker(string service)
    {
        var permit = GameIntegrityPolicy.Evaluate(
            "Some Steam Game",
            new AntiCheatEnvironment
            {
                InstalledServices = [service],
                GameDirectoryEntries = ["game.exe", "steam_api64.dll"],
            },
            ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly, $"'{service}' alongside a Steam marker was not caught.");
    }

    [Fact]
    public void A_conclusive_kernel_finding_still_wins_over_a_weak_one()
    {
        // The reference machine: Battlefield 6 ships steam_api64.dll and registers EA's
        // Javelin service. The conclusive kernel finding must decide the answer.
        var assessment = AntiCheatDetector.Assess(new AntiCheatEnvironment
        {
            InstalledServices = ["EAAntiCheatService"],
            GameDirectoryEntries = ["bf6.exe", "steam_api64.dll", "EAAntiCheat.cfg"],
        });

        Assert.Equal(AntiCheatTier.Kernel, assessment.Tier);
        Assert.Equal("EA Javelin", assessment.Findings[0].Vendor);
        Assert.True(assessment.Findings[0].EstablishesTier);
    }
}
