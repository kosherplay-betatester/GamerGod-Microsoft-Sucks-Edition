using GodMode.Core.Workloads;
using Xunit;

namespace GodMode.Core.Tests.Workloads;

public sealed class EmulatorCatalogTests
{
    [Theory]
    [InlineData("pcsx2-qt.exe", "PCSX2")]
    [InlineData("PCSX2-QT.EXE", "PCSX2")]
    [InlineData("rpcs3.exe", "RPCS3")]
    [InlineData("Dolphin.exe", "Dolphin")]
    [InlineData("duckstation-qt-x64-ReleaseLTCG.exe", "DuckStation")]
    [InlineData("Ryujinx", "Ryujinx")]
    [InlineData("mgba.exe", "mGBA")]
    [InlineData("retroarch.exe", "RetroArch")]
    public void Known_emulators_are_identified_from_their_process_name(string process, string expected)
    {
        var profile = EmulatorCatalog.Identify(process);

        Assert.NotNull(profile);
        Assert.Equal(expected, profile!.Name);
        Assert.Equal(WorkloadClass.Emulator, EmulatorCatalog.ClassifyProcess(process));
    }

    [Theory]
    [InlineData("HD-Player.exe", "BlueStacks")]
    [InlineData("crosvm.exe", "Google Play Games on PC")]
    [InlineData("dnplayer.exe", "LDPlayer")]
    public void Android_runtimes_are_classified_as_virtualised_guests(string process, string expected)
    {
        var profile = EmulatorCatalog.Identify(process);

        Assert.NotNull(profile);
        Assert.Equal(expected, profile!.Name);
        Assert.Equal(WorkloadClass.VirtualizedGuest, EmulatorCatalog.ClassifyProcess(process));
    }

    [Theory]
    [InlineData("bf6.exe")]
    [InlineData("notepad.exe")]
    [InlineData("")]
    public void Unknown_processes_are_not_misidentified(string process)
    {
        Assert.Null(EmulatorCatalog.Identify(process));
        Assert.Equal(WorkloadClass.Unknown, EmulatorCatalog.ClassifyProcess(process));
    }

    [Fact]
    public void No_profile_ships_marked_as_verified()
    {
        // Charter Article VII. Every tuned value is a hypothesis until someone measures it
        // on real hardware, so nothing may ship pre-blessed.
        Assert.All(EmulatorCatalog.All, p => Assert.False(p.Verified));
        Assert.All(EmulatorCatalog.VirtualizedGuests, p => Assert.False(p.Verified));
    }

    [Fact]
    public void Every_profile_declares_a_usable_core_count_and_process_name()
    {
        foreach (var profile in EmulatorCatalog.All.Concat(EmulatorCatalog.VirtualizedGuests))
        {
            Assert.False(profile.ProcessNames.IsDefaultOrEmpty, $"{profile.Name} has no process names.");
            Assert.False(profile.Systems.IsDefaultOrEmpty, $"{profile.Name} has no systems.");
            Assert.InRange(profile.UsefulPhysicalCores, 1, 32);
            Assert.All(profile.ProcessNames, p => Assert.DoesNotContain('.', p));
        }
    }

    [Fact]
    public void Native_refresh_rates_are_the_real_console_values_not_rounded_to_sixty()
    {
        // The whole point of tracking this is that consoles do not run at exactly 60 Hz.
        // Rounding these to 60 would silently reintroduce the judder GodMode exists to
        // detect, so the awkward values are load-bearing.
        var pcsx2 = EmulatorCatalog.Identify("pcsx2")!;
        var melonds = EmulatorCatalog.Identify("melonds")!;
        var snes = EmulatorCatalog.Identify("bsnes")!;
        var gba = EmulatorCatalog.Identify("mgba")!;

        Assert.Equal(59.94, pcsx2.NativeRefreshHz, 2);
        Assert.Equal(59.8261, melonds.NativeRefreshHz, 4);
        Assert.Equal(60.0988, snes.NativeRefreshHz, 4);
        Assert.Equal(59.7275, gba.NativeRefreshHz, 4);
    }

    [Fact]
    public void RetroArch_declares_no_refresh_because_its_core_decides()
    {
        Assert.Equal(0, EmulatorCatalog.Identify("retroarch")!.NativeRefreshHz);
    }

    [Fact]
    public void Catalogue_spans_old_school_through_current_generation()
    {
        var systems = EmulatorCatalog.SupportedSystems;

        foreach (var expected in new[]
        {
            "NES", "Super Nintendo", "Game Boy Advance", "Nintendo DS",
            "PlayStation", "PlayStation 2", "PlayStation 3", "PlayStation 4",
            "GameCube", "Wii", "Wii U", "Nintendo Switch",
            "Xbox", "Xbox 360", "Dreamcast", "Android",
        })
        {
            Assert.Contains(expected, systems);
        }
    }

    [Fact]
    public void Single_thread_bound_emulators_ask_for_exactly_one_core()
    {
        foreach (var profile in EmulatorCatalog.All.Where(p => p.ThreadModel == ThreadModel.SingleThreadBound))
        {
            Assert.Equal(1, profile.UsefulPhysicalCores);
        }
    }
}

public sealed class CompanionCatalogTests
{
    [Fact]
    public void Companions_already_running_are_not_offered()
    {
        var installed = new[] { "RTSS.exe", "Playnite.FullscreenApp.exe" };

        var missing = CompanionCatalog.Missing(installed).Select(c => c.Name).ToArray();

        Assert.DoesNotContain("RivaTuner Statistics Server", missing);
        Assert.DoesNotContain("Playnite", missing);
        Assert.Contains("Intel PresentMon", missing);
        Assert.Contains("Google Play Games on PC", missing);
    }

    [Fact]
    public void Google_play_games_is_the_offered_android_path()
    {
        var android = Assert.Single(
            CompanionCatalog.All,
            c => c.Role == CompanionRole.Platform && c.Name.Contains("Android", StringComparison.OrdinalIgnoreCase)
                 || c.Name.Contains("Play Games", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("Google.PlayGames", android.WingetId);
        Assert.Contains("Windows Subsystem for Android was retired", android.Caveat!, StringComparison.Ordinal);
    }

    [Fact]
    public void Install_command_is_shown_verbatim_so_nothing_is_hidden()
    {
        var presentMon = CompanionCatalog.All.Single(c => c.Name == "Intel PresentMon");

        Assert.Equal(
            "winget install --id Intel.PresentMon --accept-package-agreements",
            CompanionCatalog.InstallCommand(presentMon));
    }

    [Fact]
    public void Every_companion_explains_its_benefit_honestly()
    {
        Assert.All(CompanionCatalog.All, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Benefit));
            Assert.False(string.IsNullOrWhiteSpace(c.WingetId));
            Assert.False(c.DetectBy.IsDefaultOrEmpty);
        });
    }

    [Fact]
    public void The_injecting_companion_declares_that_it_injects()
    {
        // GodMode never injects, but it can hand off to a tool that does. That has to be
        // stated plainly in the offer rather than buried.
        var rtss = CompanionCatalog.All.Single(c => c.Name.Contains("RivaTuner", StringComparison.Ordinal));

        Assert.NotNull(rtss.Caveat);
        Assert.Contains("inject", rtss.Caveat!, StringComparison.OrdinalIgnoreCase);
    }
}
