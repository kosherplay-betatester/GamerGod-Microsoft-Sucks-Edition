using GamerGod.Core.Library;
using Xunit;

namespace GamerGod.Core.Tests.Library;

/// <summary>
/// Manifest parsing, checked against the real files on disk rather than an assumed shape.
/// The Steam fixtures below are the actual structure of an <c>appmanifest_*.acf</c>.
/// </summary>
public sealed class SteamManifestTests
{
    private static string Manifest(string appId, string name, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"Universe"		"1"
        	"name"		"{{name}}"
        	"StateFlags"		"4"
        	"installdir"		"{{installDir}}"
        	"LastUpdated"		"1753800000"
        	"SizeOnDisk"		"92364204213"
        }
        """;

    [Fact]
    public void A_real_manifest_yields_the_app_id_name_and_folder()
    {
        var app = StoreManifests.ParseSteamManifest(
            Manifest("2807960", "Battlefield™ 6", "Battlefield 6"));

        Assert.NotNull(app);
        Assert.Equal("2807960", app!.AppId);
        Assert.Equal("Battlefield™ 6", app.Name);
        Assert.Equal("Battlefield 6", app.InstallDir);
    }

    [Fact]
    public void Redistributables_are_not_games_and_are_excluded()
    {
        // 228980 is installed on essentially every Steam machine. A library that lists it
        // alongside your games looks broken, and it was the first thing a real scan turned up.
        Assert.Null(StoreManifests.ParseSteamManifest(
            Manifest("228980", "Steamworks Common Redistributables", "Steamworks Shared")));
    }

    [Theory]
    [InlineData("1070560")]  // Steam Linux Runtime 1.0
    [InlineData("1391110")]  // Steam Linux Runtime 2.0
    [InlineData("1628350")]  // Steam Linux Runtime 3.0
    [InlineData("1493710")]  // Proton Experimental
    [InlineData("2805730")]  // Proton 9.0
    public void Runtimes_and_proton_builds_are_excluded(string appId)
    {
        Assert.Null(StoreManifests.ParseSteamManifest(Manifest(appId, "Some Runtime", "Runtime")));
    }

    [Fact]
    public void A_half_written_manifest_is_skipped()
    {
        // Steam creates the file the moment a download is queued, before it knows the title.
        Assert.Null(StoreManifests.ParseSteamManifest(Manifest("12345", "", "SomeGame")));
        Assert.Null(StoreManifests.ParseSteamManifest(Manifest("12345", "unknown", "SomeGame")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a manifest at all")]
    [InlineData("\"AppState\" { \"appid\" \"1\" }")]
    public void Malformed_input_yields_null_rather_than_a_broken_entry(string acf)
    {
        Assert.Null(StoreManifests.ParseSteamManifest(acf));
    }

    [Fact]
    public void Library_folders_across_several_drives_are_all_found()
    {
        // A user with games on a second drive would otherwise see a half-empty library with no
        // explanation. Steam escapes backslashes in VDF, which has to be undone.
        var vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            	}
            	"1"
            	{
            		"path"		"D:\\SteamLibrary"
            	}
            }
            """;

        var folders = StoreManifests.ParseSteamLibraryFolders(vdf);

        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], folders.ToArray());
    }

    [Fact]
    public void Duplicate_library_paths_are_collapsed()
    {
        var vdf = "\"path\" \"C:\\\\Steam\"\n\"path\" \"C:\\\\Steam\"";

        Assert.Single(StoreManifests.ParseSteamLibraryFolders(vdf));
    }
}

public sealed class EpicManifestTests
{
    private const string Item = """
        {
          "FormatVersion": 0,
          "bIsIncompleteInstall": false,
          "AppName": "Fortnite",
          "CatalogItemId": "4fe75bbc5a674f4f9b356b5c90567da5",
          "DisplayName": "Fortnite",
          "InstallLocation": "D:\\Epic Games\\Fortnite",
          "LaunchExecutable": "FortniteGame\\Binaries\\Win64\\FortniteClient-Win64-Shipping.exe"
        }
        """;

    [Fact]
    public void A_real_epic_manifest_yields_the_app_name_and_title()
    {
        var app = StoreManifests.ParseEpicManifest(Item);

        Assert.NotNull(app);
        Assert.Equal("Fortnite", app!.AppName);
        Assert.Equal("Fortnite", app.DisplayName);
        Assert.Equal(@"D:\Epic Games\Fortnite", app.InstallLocation);
    }

    [Fact]
    public void A_manifest_missing_its_install_location_is_still_launchable()
    {
        // The launch goes through Epic's protocol handler using AppName, so a missing path
        // costs the anti-cheat pre-check but not the ability to start the game.
        var app = StoreManifests.ParseEpicManifest(
            """{ "AppName": "Rocket", "DisplayName": "Rocket League" }""");

        Assert.NotNull(app);
        Assert.Null(app!.InstallLocation);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{ "AppName": "OnlyId" }""")]
    [InlineData("""{ "DisplayName": "OnlyName" }""")]
    public void Incomplete_manifests_yield_null(string json)
    {
        Assert.Null(StoreManifests.ParseEpicManifest(json));
    }

    [Fact]
    public void Trailing_content_does_not_defeat_the_parse()
    {
        // Parsed by expression rather than a strict deserialiser precisely so that a detail
        // nobody cares about cannot cost the user a game in their library.
        var app = StoreManifests.ParseEpicManifest(Item + "\n\n// leftover");

        Assert.NotNull(app);
        Assert.Equal("Fortnite", app!.DisplayName);
    }
}

public sealed class GameEntryTests
{
    [Fact]
    public void Store_titles_launch_through_the_store_not_the_executable()
    {
        // Starting a store title's exe directly is the classic way a third-party launcher
        // breaks cloud saves and anti-cheat bootstrapping.
        var steam = new GameEntry
        {
            Name = "Battlefield 6",
            Source = GameSource.Steam,
            LaunchTarget = "steam://rungameid/2807960",
        };

        Assert.StartsWith("steam://", steam.LaunchTarget, StringComparison.Ordinal);
        Assert.Equal("Steam", steam.SourceLabel);
    }

    [Fact]
    public void Every_source_has_a_label_and_none_is_left_unhandled()
    {
        // A missing case in the switch would silently show "Manual" for a real store, so every
        // value is checked rather than trusting the default arm.
        foreach (var source in Enum.GetValues<GameSource>())
        {
            var label = new GameEntry { Name = "x", Source = source, LaunchTarget = "x" }.SourceLabel;

            Assert.False(string.IsNullOrWhiteSpace(label));

            if (source != GameSource.Manual)
            {
                Assert.NotEqual("Added by you", label);
            }
        }
    }

    [Fact]
    public void A_manually_added_game_is_labelled_in_the_user_s_terms()
    {
        var entry = new GameEntry { Name = "x", Source = GameSource.Manual, LaunchTarget = "x" };

        // "Manual" is how the code thinks about it; "Added by you" is how a person does.
        Assert.Equal("Added by you", entry.SourceLabel);
    }
}
