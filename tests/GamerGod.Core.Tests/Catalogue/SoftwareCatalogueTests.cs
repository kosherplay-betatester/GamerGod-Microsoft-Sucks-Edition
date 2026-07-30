using GamerGod.Core.Catalogue;
using Xunit;

namespace GamerGod.Core.Tests.Catalogue;

/// <summary>
/// The catalogue is data, and the failure modes are all data failures: an identifier that does
/// not resolve produces an install button that fails, a missing homepage produces a dead end,
/// and a duplicated entry means somebody edited one copy.
/// </summary>
public sealed class SoftwareCatalogueTests
{
    [Fact]
    public void Every_entry_has_somewhere_to_send_you()
    {
        // The homepage is the fallback for anything winget does not carry and the citation for
        // everything it does. An entry without one is a dead end.
        foreach (var entry in SoftwareCatalogue.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Homepage), entry.Name);

            var uri = new Uri(entry.Homepage);

            Assert.Equal("https", uri.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(entry.Systems), entry.Name);
            Assert.False(string.IsNullOrWhiteSpace(entry.Name), entry.Id);
        }
    }

    [Fact]
    public void Identifiers_are_unique()
    {
        var ids = SoftwareCatalogue.All.Select(e => e.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void No_package_is_listed_twice()
    {
        // Two entries sharing a winget id would show two rows that install the same thing and
        // disagree with each other about whether it is installed.
        var packages = SoftwareCatalogue.All
            .Select(e => e.WingetId)
            .Where(id => id is not null)
            .ToList();

        Assert.Equal(packages.Count, packages.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Winget_identifiers_are_shaped_like_winget_identifiers()
    {
        // Publisher.Package, no spaces. Every id in this catalogue was checked against the live
        // source; this catches a typo introduced later, which would otherwise surface as an
        // install button that fails for one entry only.
        foreach (var entry in SoftwareCatalogue.All.Where(e => e.WingetId is not null))
        {
            var id = entry.WingetId!;

            Assert.DoesNotContain(' ', id);
            Assert.Contains('.', id);
            Assert.False(id.StartsWith('.') || id.EndsWith('.'), entry.Name);
        }
    }

    [Fact]
    public void An_entry_winget_does_not_carry_is_a_valid_entry()
    {
        // Several of the best emulators publish only from their own site. Those must survive in
        // the catalogue with a null id rather than being dropped or given an invented one —
        // CanInstall is what the interface reads to offer the page instead of a button.
        var manual = SoftwareCatalogue.All.Where(e => !e.CanInstall).ToList();

        Assert.NotEmpty(manual);
        Assert.All(manual, e => Assert.Null(e.WingetId));
        Assert.All(manual, e => Assert.False(string.IsNullOrWhiteSpace(e.Homepage)));
    }

    [Fact]
    public void The_catalogue_spans_arcade_hardware_to_current_consoles()
    {
        var groups = SoftwareCatalogue.Emulators.Select(g => g.Title).ToList();

        Assert.Contains(groups, t => t.Contains("Arcade", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(groups, t => t.Contains("Android", StringComparison.OrdinalIgnoreCase));

        // Old-school through modern, including a current-generation target.
        Assert.Contains(SoftwareCatalogue.All, e => e.Id == "mame");
        Assert.Contains(SoftwareCatalogue.All, e => e.Id == "shadps4");
        Assert.Contains(SoftwareCatalogue.All, e => e.Id == "google-play-games");
    }

    [Fact]
    public void The_major_storefronts_are_all_present()
    {
        foreach (var id in new[] { "steam", "epic", "gog", "ea", "ubisoft", "battlenet", "rockstar" })
        {
            Assert.Contains(SoftwareCatalogue.All, e => e.Id == id);
        }
    }

    [Fact]
    public void Systems_that_need_a_bios_say_so()
    {
        // An emulator that boots to a black screen looks broken to somebody who was never told
        // it needs a BIOS. These three cannot run anything without one.
        foreach (var id in new[] { "pcsx2", "xemu", "flycast" })
        {
            var entry = SoftwareCatalogue.All.Single(e => e.Id == id);

            Assert.NotNull(entry.Note);
            Assert.True(
                entry.Note!.Contains("BIOS", StringComparison.OrdinalIgnoreCase),
                $"{entry.Name} needs a BIOS and its note does not say so");
        }
    }

    [Fact]
    public void The_catalogue_points_at_no_game_content_whatsoever()
    {
        // Emulators are legal; what runs on them is the user's business. GamerGod links to
        // emulator projects and storefronts only — never to a ROM, a BIOS, or a site that
        // traffics in them. This is a licence and a liability boundary, not a style rule.
        //
        // Matched by domain rather than by substring. Searching hosts for "iso" or "rom"
        // sounds thorough and is not: it fails ubisoftconnect.com, which contains "iso", and
        // would keep failing correct entries until somebody deleted the test. Named domains
        // are checkable and do not produce false accusations.
        string[] distributionSites =
        [
            "emuparadise.me", "coolrom.com", "vimm.net", "myrient.erista.me",
            "romsgames.net", "romsmania.cc", "edgeemu.net", "wowroms.com",
            "thepiratebay.org", "1337x.to", "nopaystation.com",
        ];

        foreach (var entry in SoftwareCatalogue.All)
        {
            var host = new Uri(entry.Homepage).Host.ToLowerInvariant();

            foreach (var site in distributionSites)
            {
                Assert.False(
                    host == site || host.EndsWith("." + site, StringComparison.Ordinal),
                    $"{entry.Name} links to {host}, which distributes game content rather than software");
            }
        }
    }

    [Fact]
    public void No_entry_advertises_supplying_the_games_themselves()
    {
        // The other half of the boundary: an entry could link somewhere legitimate while its
        // own copy offers to find ROMs. The catalogue describes software and says when a BIOS
        // is needed; it never offers to supply one.
        foreach (var entry in SoftwareCatalogue.All)
        {
            var copy = $"{entry.Systems} {entry.Note}";

            Assert.DoesNotContain("download roms", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("free games included", copy, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("we provide", copy, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_absence_of_a_switch_emulator_is_explained_rather_than_silent()
    {
        // It is the most obvious hole in this list, and leaving it unexplained reads as an
        // oversight rather than a decision.
        Assert.Contains("Switch", SoftwareCatalogue.SwitchNote, StringComparison.Ordinal);
        Assert.DoesNotContain(SoftwareCatalogue.All, e => e.Id is "yuzu" or "ryujinx");
    }
}
