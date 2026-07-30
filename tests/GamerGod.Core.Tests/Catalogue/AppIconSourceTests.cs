using GamerGod.Core.Catalogue;
using Xunit;

namespace GamerGod.Core.Tests.Catalogue;

/// <summary>
/// Finding a project's own logo on a project's own site.
///
/// <para>
/// The reason this parses HTML rather than guessing <c>/favicon.ico</c>: checked against the
/// real sites, fixed paths find artwork for about half of the catalogue. Ubisoft publishes a
/// 228-pixel icon on a separate CDN, Battle.net a 192-pixel one under <c>/static</c>, CapFrameX
/// a 120-pixel Apple touch icon — none at a guessable path.
/// </para>
/// </summary>
public sealed class AppIconSourceTests
{
    [Fact]
    public void The_largest_declared_icon_wins()
    {
        // Sites declare several sizes and the small ones are unusable at tile size.
        const string html = """
            <link rel="icon" sizes="16x16" href="/small.png">
            <link rel="apple-touch-icon" sizes="180x180" href="/big.png">
            <link rel="icon" sizes="32x32" href="/medium.png">
            """;

        var icons = AppIconSource.DeclaredIcons(html, "https://example.com/");

        Assert.Equal("https://example.com/big.png", icons[0]);
    }

    [Fact]
    public void A_relative_href_resolves_against_the_page_it_came_from()
    {
        var icons = AppIconSource.DeclaredIcons(
            """<link rel="icon" href="assets/img/favicon32.png">""",
            "https://www.duckstation.org/");

        Assert.Equal("https://www.duckstation.org/assets/img/favicon32.png", icons[0]);
    }

    [Fact]
    public void An_icon_on_another_host_is_accepted()
    {
        // Ubisoft's real arrangement. Refusing cross-host icons would reject most of the good
        // artwork in this catalogue, and the response is only ever decoded as an image.
        var icons = AppIconSource.DeclaredIcons(
            """<link rel="icon" sizes="228x228" href="https://static-dm.ubisoft.com/favicon-228x228.png">""",
            "https://ubisoftconnect.com/");

        Assert.Single(icons);
        Assert.StartsWith("https://static-dm.ubisoft.com/", icons[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<link rel="icon" href="http://example.com/i.png">""")]
    [InlineData("""<link rel="icon" href="file:///C:/Windows/System32/config/SAM">""")]
    [InlineData("""<link rel="icon" href="javascript:alert(1)">""")]
    [InlineData("""<link rel="icon" href="https://127.0.0.1/i.png">""")]
    public void An_address_that_is_not_a_public_https_image_is_refused(string html)
    {
        // This href arrives inside HTML from a remote server and becomes the target of a
        // request, so the scheme is pinned and loopback is excluded.
        Assert.Empty(AppIconSource.DeclaredIcons(html, "https://example.com/"));
    }

    [Fact]
    public void A_page_declaring_nothing_yields_nothing()
    {
        Assert.Empty(AppIconSource.DeclaredIcons("<html><body>hello</body></html>", "https://example.com/"));
        Assert.Empty(AppIconSource.DeclaredIcons(string.Empty, "https://example.com/"));
    }

    [Fact]
    public void The_conventional_paths_are_the_fallback_and_prefer_the_large_one()
    {
        // apple-touch-icon is 180 by convention; favicon.ico is frequently 16.
        var paths = AppIconSource.FallbackPaths("https://mgba.io/some/page");

        Assert.Equal("https://mgba.io/apple-touch-icon.png", paths[0]);
        Assert.Contains("https://mgba.io/favicon.ico", paths);
        Assert.True(
            paths.IndexOf("https://mgba.io/apple-touch-icon.png")
            < paths.IndexOf("https://mgba.io/favicon.ico"));
    }

    [Fact]
    public void A_homepage_that_is_not_https_produces_no_fallbacks() =>
        Assert.Empty(AppIconSource.FallbackPaths("http://example.com/"));

    // ---- what it believes ----------------------------------------------

    private static byte[] Sized(int size, params byte[] header)
    {
        var bytes = new byte[size];
        header.CopyTo(bytes, 0);
        return bytes;
    }

    [Fact]
    public void Real_image_formats_are_accepted()
    {
        Assert.True(AppIconSource.IsUsableIcon(Sized(2048, 0x89, 0x50, 0x4E, 0x47)));
        Assert.True(AppIconSource.IsUsableIcon(Sized(2048, 0x00, 0x00, 0x01, 0x00)));
        Assert.True(AppIconSource.IsUsableIcon(Sized(2048, 0xFF, 0xD8, 0xFF)));
        Assert.True(AppIconSource.IsUsableIcon(Sized(2048, (byte)'G', (byte)'I', (byte)'F')));
    }

    [Fact]
    public void An_html_error_page_is_rejected()
    {
        // A project that has removed its icon commonly answers 200 with a page. Caching that
        // would leave the tile permanently blank, because a cached file is never re-fetched.
        var html = new byte[2048];
        "<!DOCTYPE html><html>"u8.CopyTo(html);

        Assert.False(AppIconSource.IsUsableIcon(html));
    }

    [Fact]
    public void An_svg_is_rejected_because_WPF_cannot_draw_one()
    {
        var svg = new byte[2048];
        "<svg xmlns=\"http://www.w3.org/2000/svg\">"u8.CopyTo(svg);

        Assert.False(AppIconSource.IsUsableIcon(svg));
    }

    [Fact]
    public void Empty_truncated_and_oversized_responses_are_rejected()
    {
        Assert.False(AppIconSource.IsUsableIcon([]));
        Assert.False(AppIconSource.IsUsableIcon(Sized(8, 0x89, 0x50, 0x4E, 0x47)));
        Assert.False(AppIconSource.IsUsableIcon(Sized(AppIconSource.MaxBytes + 1, 0x89, 0x50, 0x4E, 0x47)));
    }

    [Fact]
    public void A_small_but_real_favicon_is_accepted()
    {
        // Unlike cover art, a clean 16-pixel favicon really can be a few hundred bytes, so the
        // floor here is far lower than the one that rejects blank cover placeholders.
        Assert.True(AppIconSource.IsUsableIcon(Sized(400, 0x00, 0x00, 0x01, 0x00)));
    }

    [Fact]
    public void Every_catalogue_homepage_can_produce_a_lookup()
    {
        // Nothing in the catalogue should be unreachable by this mechanism for a trivial
        // reason like a malformed URL.
        foreach (var entry in SoftwareCatalogue.All)
        {
            Assert.NotEmpty(AppIconSource.FallbackPaths(entry.Homepage));
        }
    }
}
