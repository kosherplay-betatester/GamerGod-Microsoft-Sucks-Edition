using GamerGod.Core.Catalogue;
using Xunit;

namespace GamerGod.Core.Tests.Catalogue;

/// <summary>
/// Matching a catalogue entry to a name in Windows' installed-programs list.
///
/// <para>
/// A false negative costs an icon and a launch button. A false positive puts a Launch button
/// for one program onto a completely different one, so the tests lean hard on the names that
/// nearly collide.
/// </para>
/// </summary>
public sealed class InstalledMatchTests
{
    [Theory]
    [InlineData("Steam", "Steam")]
    [InlineData("Battle.net", "Battle.net")]
    [InlineData("EA app", "EA App")]
    [InlineData("GOG GALAXY", "GOG GALAXY")]
    [InlineData("Epic Games Launcher", "Epic Games Launcher")]
    [InlineData("RivaTuner Statistics Server", "RivaTuner Statistics Server 7.3.7")]
    public void The_same_program_matches_however_it_is_punctuated(string wanted, string installed) =>
        Assert.True(InstalledMatch.IsSameProgram(wanted, installed));

    [Theory]
    [InlineData("Dolphin", "Dolphin 2506")]
    [InlineData("PPSSPP", "PPSSPP 1.19.3")]
    [InlineData("GOG GALAXY", "GOG GALAXY 2.0")]
    [InlineData("DOSBox-X", "DOSBox-X 2026.07.02")]
    [InlineData("MSI Afterburner", "MSI Afterburner 4.6.6")]
    public void A_version_suffix_does_not_break_the_match(string wanted, string installed) =>
        Assert.True(InstalledMatch.IsSameProgram(wanted, installed));

    [Theory]
    [InlineData("itch", "Twitch")]
    [InlineData("ares", "VMware Workstation")]
    [InlineData("Steam", "SteamCMD Deluxe")]
    [InlineData("Cemu", "Cemu Graphic Packs Downloader")]
    [InlineData("Xenia", "Xenia Manager")]
    public void A_different_program_does_not_match(string wanted, string installed)
    {
        // The reason the short-name rule exists. "itch" sits inside "Twitch" and "ares" inside
        // "VMware", and either false positive would put a Launch button on the wrong program.
        Assert.False(InstalledMatch.IsSameProgram(wanted, installed));
    }

    [Fact]
    public void A_short_name_must_match_exactly()
    {
        Assert.True(InstalledMatch.IsSameProgram("ares", "ares"));
        Assert.True(InstalledMatch.IsSameProgram("itch", "itch"));

        // Prefix matching is not available to names this short, in either direction.
        Assert.False(InstalledMatch.IsSameProgram("ares", "aresenal"));
        Assert.False(InstalledMatch.IsSameProgram("itch", "itchy"));
    }

    [Theory]
    [InlineData("", "Steam")]
    [InlineData("Steam", "")]
    [InlineData("   ", "Steam")]
    [InlineData("...", "Steam")]
    public void Empty_and_punctuation_only_names_never_match(string wanted, string installed) =>
        Assert.False(InstalledMatch.IsSameProgram(wanted, installed));

    [Fact]
    public void Matching_is_not_direction_agnostic()
    {
        // "Xenia" must not claim "Xenia Manager", but neither should a longer catalogue name
        // match a shorter installed one — the installed name is the one that carries suffixes.
        Assert.False(InstalledMatch.IsSameProgram("Epic Games Launcher", "Epic"));
    }

    [Fact]
    public void No_catalogue_entry_can_match_another_catalogue_entry()
    {
        // If two entries could both claim the same installed program, the Apps page would show
        // the same icon twice under different names and one of them would launch the wrong
        // thing.
        foreach (var a in SoftwareCatalogue.All)
        {
            foreach (var b in SoftwareCatalogue.All.Where(x => x.Id != a.Id))
            {
                Assert.False(
                    InstalledMatch.IsSameProgram(a.Name, b.Name),
                    $"'{a.Name}' would also match '{b.Name}'");
            }
        }
    }
}
