using System.Collections.Immutable;
using GamerGod.Core.Catalogue;
using Xunit;

namespace GamerGod.Core.Tests.Catalogue;

/// <summary>
/// Choosing which executable a Launch button starts.
///
/// <para>
/// Written after finding that Steam, MSI Afterburner and RivaTuner Statistics Server all record
/// <c>uninstall.exe</c> as their <c>DisplayIcon</c> on a real machine, and all three leave
/// <c>InstallLocation</c> empty. Reading DisplayIcon as a launch target — which is exactly what
/// it looks like — puts a Launch button on the uninstaller for three of the most common
/// programs a gamer has installed.
/// </para>
///
/// <para>
/// The asymmetry drives every test here. Failing to find the right executable costs a button.
/// Finding the wrong one costs somebody's installation.
/// </para>
/// </summary>
public sealed class ExecutablePickerTests
{
    [Theory]
    [InlineData("uninstall.exe")]
    [InlineData("Uninstall.exe")]
    [InlineData("unins000.exe")]
    [InlineData("uninst.exe")]
    [InlineData("Uninstall Steam.exe")]
    [InlineData("setup.exe")]
    [InlineData("install.exe")]
    [InlineData("remove.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\uninstall.exe")]
    public void Anything_that_removes_software_is_recognised(string fileName) =>
        Assert.True(ExecutablePicker.IsUninstaller(fileName), fileName);

    [Theory]
    [InlineData("steam.exe")]
    [InlineData("MSIAfterburner.exe")]
    [InlineData("RTSS.exe")]
    [InlineData("Dolphin.exe")]
    [InlineData("retroarch.exe")]
    public void A_real_program_is_not_mistaken_for_one(string fileName) =>
        Assert.False(ExecutablePicker.IsUninstaller(fileName), fileName);

    [Fact]
    public void Steam_resolves_to_steam_and_never_to_its_uninstaller()
    {
        // The exact folder from the reference machine.
        var chosen = ExecutablePicker.Choose(
            "Steam",
            "Steam",
            ["steam.exe", "uninstall.exe", "GameOverlayUI.exe", "streaming_client.exe"]);

        Assert.Equal("steam.exe", chosen);
    }

    [Fact]
    public void Afterburner_resolves_through_its_spacing()
    {
        var chosen = ExecutablePicker.Choose(
            "MSI Afterburner 4.6.6",
            "MSI Afterburner",
            ["MSIAfterburner.exe", "uninstall.exe"]);

        Assert.Equal("MSIAfterburner.exe", chosen);
    }

    [Fact]
    public void Rivatuner_resolves_through_its_acronym()
    {
        // Nothing in "RivaTuner Statistics Server" textually resembles RTSS.exe, and the folder
        // is named after the long form. The initials are the only link.
        var chosen = ExecutablePicker.Choose(
            "RivaTuner Statistics Server 7.3.7",
            "RivaTuner Statistics Server",
            ["RTSS.exe", "RTSSHooksLoader64.exe", "EncoderServer64.exe", "uninstall.exe"]);

        Assert.Equal("RTSS.exe", chosen);
    }

    [Fact]
    public void A_version_in_the_display_name_does_not_prevent_a_match()
    {
        Assert.Equal(
            "Dolphin.exe",
            ExecutablePicker.Choose("Dolphin 2506", "Dolphin-x64", ["Dolphin.exe", "unins000.exe"]));
    }

    [Fact]
    public void A_folder_holding_only_an_uninstaller_yields_nothing()
    {
        Assert.Null(ExecutablePicker.Choose("Something", "Something", ["uninstall.exe"]));
        Assert.Null(ExecutablePicker.Choose("Something", "Something", []));
    }

    [Fact]
    public void An_ambiguous_folder_yields_nothing_rather_than_a_guess()
    {
        // Four unrelated helpers and no name that matches. Offering no button is correct;
        // starting a crash reporter because it happened to be first is not.
        Assert.Null(ExecutablePicker.Choose(
            "Some Program",
            "SomeProgram",
            ["helper.exe", "crashpad.exe", "updater.exe", "service.exe"]));
    }

    [Fact]
    public void A_single_remaining_executable_is_accepted()
    {
        // Once uninstallers are excluded, one candidate is not a guess.
        Assert.Equal(
            "weirdname.exe",
            ExecutablePicker.Choose("Some Program", "SomeProgram", ["weirdname.exe", "unins000.exe"]));
    }

    [Fact]
    public void No_input_can_ever_produce_an_uninstaller()
    {
        // The property that actually matters, asserted over every shape the other tests use.
        string[][] folders =
        [
            ["uninstall.exe"],
            ["uninstall.exe", "unins000.exe"],
            ["steam.exe", "uninstall.exe"],
            ["Uninstall Steam.exe", "steam.exe"],
            ["setup.exe"],
            ["remove.exe", "modify.exe", "repair.exe"],
        ];

        foreach (var folder in folders)
        {
            foreach (var name in new[] { "Steam", "Uninstall", "Setup", "Remove", "unins000" })
            {
                var chosen = ExecutablePicker.Choose(name, name, [.. folder]);

                if (chosen is not null)
                {
                    Assert.False(
                        ExecutablePicker.IsUninstaller(chosen),
                        $"'{name}' over [{string.Join(", ", folder)}] chose {chosen}");
                }
            }
        }
    }
}
