using GamerGod.Core.Updates;
using Xunit;

namespace GamerGod.Core.Tests.Updates;

/// <summary>
/// Deciding whether to tell somebody a new version exists.
///
/// <para>
/// Most of these assert silence. An update banner that appears when nothing is newer is how
/// people learn to dismiss update banners without reading them, so every ordinary failure —
/// rate limits, drafts, unparseable versions, an offline machine — has to end in nothing being
/// said at all.
/// </para>
/// </summary>
public sealed class ReleaseCheckTests
{
    private const string Digest = "sha256:2491e5e8648f88091ce7702faa6fa9973105e51afc2b7948648b662afe5e1f5b";

    private const string DownloadUrl =
        "https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition/releases/download/v1.2.0/GamerGod-1.2.0-Setup.exe";

    /// <summary>The real shape of GitHub's response, trimmed to what is read.</summary>
    private static string Response(
        string tag = "v1.2.0",
        bool draft = false,
        bool prerelease = false,
        string? assetUrl = DownloadUrl,
        string? digest = Digest) =>
        "{"
        + $"\"tag_name\":\"{tag}\","
        + $"\"name\":\"GamerGod {tag.TrimStart('v')}\","
        + $"\"draft\":{(draft ? "true" : "false")},"
        + $"\"prerelease\":{(prerelease ? "true" : "false")},"
        + "\"html_url\":\"https://github.com/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition/releases/tag/v1.2.0\","
        + "\"assets\":["
        + (assetUrl is null
            ? string.Empty
            : "{\"name\":\"GamerGod-1.2.0-Setup.exe\",\"size\":76546048,"
              + $"\"browser_download_url\":\"{assetUrl}\""
              + (digest is null ? string.Empty : $",\"digest\":\"{digest}\"")
              + "}")
        + "]}";

    [Fact]
    public void A_newer_release_is_reported_with_everything_needed_to_get_it()
    {
        var release = ReleaseCheck.Evaluate(Response(), "1.1.0+67603ed");

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release!.Version.ToString());
        Assert.True(release.CanDownload);
        Assert.Equal(
            "2491e5e8648f88091ce7702faa6fa9973105e51afc2b7948648b662afe5e1f5b",
            release.Sha256);
    }

    [Fact]
    public void The_version_already_running_is_not_an_update()
    {
        // The case that would otherwise fire on every single launch: the assembly reports
        // "1.1.0+<commit>" and the tag reads "v1.1.0". Compared as strings those differ.
        Assert.Null(ReleaseCheck.Evaluate(Response("v1.1.0"), "1.1.0+67603ed"));
    }

    [Fact]
    public void An_older_release_is_not_an_update() =>
        Assert.Null(ReleaseCheck.Evaluate(Response("v1.0.0"), "1.1.0"));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Drafts_and_pre_releases_are_never_offered(bool draft, bool prerelease)
    {
        // A draft is unpublished and a pre-release is opt-in by nature. Neither should reach
        // somebody who asked only to be told about new versions.
        Assert.Null(ReleaseCheck.Evaluate(Response(draft: draft, prerelease: prerelease), "1.1.0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("<html><body>rate limited</body></html>")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{\"message\":\"API rate limit exceeded\"}")]
    public void A_response_that_is_not_a_release_says_nothing(string? json) =>
        Assert.Null(ReleaseCheck.Evaluate(json, "1.1.0"));

    [Fact]
    public void An_unreadable_running_version_says_nothing()
    {
        // Better to stay quiet than to offer an "update" to something that might be older.
        Assert.Null(ReleaseCheck.Evaluate(Response(), "not-a-version"));
        Assert.Null(ReleaseCheck.Evaluate(Response(), null));
    }

    [Fact]
    public void An_unreadable_tag_says_nothing() =>
        Assert.Null(ReleaseCheck.Evaluate(Response("nightly"), "1.1.0"));

    // ---- what it is willing to fetch -----------------------------------

    [Theory]
    [InlineData("https://evil.example.com/GamerGod-Setup.exe")]
    [InlineData("http://github.com/x/y/releases/download/v1/z.exe")]
    [InlineData("https://github.com.evil.example.com/z.exe")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    public void An_installer_hosted_anywhere_but_GitHub_is_refused(string url)
    {
        // This URL arrives from a remote server and would become an executable on disk. The
        // release is still announced — the page is legitimate — but with no download offered.
        var release = ReleaseCheck.Evaluate(Response(assetUrl: url), "1.1.0");

        Assert.NotNull(release);
        Assert.False(release!.CanDownload);
        Assert.Null(release.InstallerUrl);
    }

    [Fact]
    public void A_release_with_no_digest_cannot_be_downloaded()
    {
        // Without a published fingerprint there is nothing to verify against, and an
        // unverified installer is not worth offering. The release page still is.
        var release = ReleaseCheck.Evaluate(Response(digest: null), "1.1.0");

        Assert.NotNull(release);
        Assert.False(release!.CanDownload);
        Assert.NotNull(release.PageUrl);
    }

    [Theory]
    [InlineData("sha256:tooshort")]
    [InlineData("md5:2491e5e8648f88091ce7702faa6fa997")]
    [InlineData("sha256:zzzz5e8648f88091ce7702faa6fa9973105e51afc2b7948648b662afe5e1f5b0")]
    public void A_malformed_digest_is_treated_as_no_digest(string digest)
    {
        var release = ReleaseCheck.Evaluate(Response(digest: digest), "1.1.0");

        Assert.NotNull(release);
        Assert.False(release!.CanDownload);
    }

    [Fact]
    public void A_release_with_no_installer_still_offers_its_page()
    {
        var release = ReleaseCheck.Evaluate(Response(assetUrl: null), "1.1.0");

        Assert.NotNull(release);
        Assert.False(release!.CanDownload);
        Assert.StartsWith("https://github.com/", release.PageUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void The_only_address_this_feature_contacts_is_the_GitHub_api()
    {
        var uri = new Uri(ReleaseCheck.LatestReleaseUrl);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal("api.github.com", uri.Host);
        Assert.Equal(string.Empty, uri.Query);
    }
}
