using GamerGod.Core.Library;
using Xunit;

namespace GamerGod.Core.Tests.Library;

/// <summary>
/// Cover art downloading is the only feature in GamerGod that opens an outbound connection, so
/// it is the only place where a bad input can reach a network stack rather than a local API.
/// These cover the three things that go wrong: asking the wrong host, believing the wrong
/// bytes, and writing to the wrong path.
/// </summary>
public sealed class CoverArtSourceTests
{
    // ---- what may be interpolated -------------------------------------

    [Theory]
    [InlineData("1517290")]
    [InlineData("0")]
    public void A_numeric_store_id_is_usable(string appId) =>
        Assert.True(CoverArtSource.IsUsableAppId(appId));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("1517290/../../secrets")]
    [InlineData("evil.example.com")]
    [InlineData("1517290?x=1")]
    [InlineData("1517290 ")]
    [InlineData("151 7290")]
    [InlineData("١٥١٧٢٩٠")]
    public void Anything_that_could_escape_a_path_is_refused(string? appId) =>
        Assert.False(CoverArtSource.IsUsableAppId(appId));

    [Fact]
    public void A_refused_id_produces_no_addresses_at_all()
    {
        // The guard has to stop the request being formed, not merely be available to callers
        // who remember to ask. An id that cannot be trusted yields an empty list, so the
        // download loop has nothing to iterate and never reaches a socket.
        Assert.Empty(CoverArtSource.CandidateUrls("../../../etc/passwd"));
        Assert.Empty(CoverArtSource.CandidateUrls(string.Empty));
    }

    // ---- where it asks -------------------------------------------------

    [Fact]
    public void Every_address_is_https_on_a_public_valve_cdn()
    {
        foreach (var url in CoverArtSource.CandidateUrls("1517290"))
        {
            var uri = new Uri(url);

            Assert.Equal("https", uri.Scheme);
            Assert.EndsWith(".steamstatic.com", uri.Host, StringComparison.Ordinal);

            // No account, key, session or identifier may ride along. A query string is how
            // that would arrive, so there must not be one.
            Assert.Equal(string.Empty, uri.Query);
            Assert.Contains("1517290", uri.AbsolutePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_portrait_cover_is_preferred_over_the_landscape_header()
    {
        var urls = CoverArtSource.CandidateUrls("1517290");

        var firstHeader = urls.ToList().FindIndex(u => u.Contains("header", StringComparison.Ordinal));
        var lastPortrait = urls.ToList().FindLastIndex(u => u.Contains("600x900", StringComparison.Ordinal));

        Assert.True(lastPortrait >= 0 && firstHeader >= 0);
        Assert.True(
            lastPortrait < firstHeader,
            "a cropped landscape header must only ever be a fallback, never a first choice");
    }

    // ---- what it believes ----------------------------------------------

    private static byte[] Jpeg(int size)
    {
        var bytes = new byte[size];
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    [Fact]
    public void A_real_cover_is_accepted()
    {
        // 3DMark Demo's actual published portrait on the reference machine: 40,105 bytes.
        Assert.True(CoverArtSource.IsUsableCover(Jpeg(40_105)));
    }

    [Fact]
    public void A_publishers_blank_placeholder_is_rejected()
    {
        // Battlefield 6, measured on the reference machine. The CDN answers 200 at the
        // expected address with a structurally valid 300x450 JPEG containing four shades of
        // grey, in 1,655 bytes. Every check but size passes. A blank grey rectangle in the
        // grid is worse than the generated tile it would replace, and because a cached file is
        // never re-fetched it would stay that way permanently.
        Assert.False(CoverArtSource.IsUsableCover(Jpeg(1_655)));
    }

    [Fact]
    public void An_html_error_page_served_with_status_200_is_rejected()
    {
        // The other permanent-poisoning failure: a CDN answering 200 with an error document.
        var html = new byte[64 * 1024];
        "<!DOCTYPE html><html><head><title>404"u8.CopyTo(html);

        Assert.False(CoverArtSource.IsUsableCover(html));
    }

    [Fact]
    public void A_truncated_response_is_rejected() =>
        Assert.False(CoverArtSource.IsUsableCover(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));

    [Fact]
    public void An_empty_response_is_rejected_rather_than_throwing()
    {
        Assert.False(CoverArtSource.IsUsableCover([]));
        Assert.False(CoverArtSource.IsUsableCover(new byte[2]));
    }

    [Fact]
    public void A_body_beyond_the_ceiling_is_rejected()
    {
        // Bounded so a redirect to something enormous cannot fill the disk.
        Assert.False(CoverArtSource.IsUsableCover(Jpeg(CoverArtSource.MaxBytes + 1)));
    }

    [Fact]
    public void The_accepted_range_spans_every_real_cover_size()
    {
        // Portrait covers run 60-200 KB. Limits that excluded them would turn this feature off
        // without ever saying so — the floor exists to catch placeholders, not real art.
        Assert.True(CoverArtSource.IsUsableCover(Jpeg(60 * 1024)));
        Assert.True(CoverArtSource.IsUsableCover(Jpeg(200 * 1024)));
        Assert.True(CoverArtSource.MinBytes < 60 * 1024);
        Assert.True(CoverArtSource.MaxBytes > 1024 * 1024);
    }
}
