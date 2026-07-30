using System.Text.Json;

namespace GamerGod.Core.Updates;

/// <summary>A release newer than the one running, and how to get it.</summary>
public sealed record AvailableRelease
{
    public required ReleaseVersion Version { get; init; }

    /// <summary>Human title from the release, for the banner.</summary>
    public required string Title { get; init; }

    /// <summary>The release page, which is where a user goes to read what changed.</summary>
    public required string PageUrl { get; init; }

    /// <summary>Direct link to the installer, or null when a release published none.</summary>
    public string? InstallerUrl { get; init; }

    public string? InstallerName { get; init; }

    public long InstallerBytes { get; init; }

    /// <summary>
    /// The SHA-256 GitHub computed for the asset, lowercase hex, or null when absent.
    ///
    /// <para>
    /// This is what makes downloading an installer defensible rather than reckless. The digest
    /// comes from the API over TLS and the file from a CDN; verifying one against the other
    /// means a substituted or truncated download is detected before anything is run.
    /// </para>
    /// </summary>
    public string? Sha256 { get; init; }

    public bool CanDownload => InstallerUrl is not null && Sha256 is not null;
}

/// <summary>
/// Decides whether a GitHub releases response describes something worth telling the user about.
///
/// <para>
/// Pure, so the whole decision — including every reason to stay quiet — is testable without a
/// network. The reasons to stay quiet matter more than the reason to speak: an update prompt
/// that appears when nothing is newer trains people to dismiss it.
/// </para>
/// </summary>
public static class ReleaseCheck
{
    /// <summary>The one address this feature contacts.</summary>
    public const string LatestReleaseUrl =
        "https://api.github.com/repos/kosherplay-betatester/GamerGod-Microsoft-Sucks-Edition/releases/latest";

    /// <summary>
    /// The newer release described by <paramref name="json"/>, or null.
    ///
    /// <para>
    /// Null for every ordinary failure — a rate-limit body, an HTML error page, a draft, a
    /// pre-release, a version that is not newer, or a version neither side can parse. None of
    /// those deserve a dialog.
    /// </para>
    /// </summary>
    public static AvailableRelease? Evaluate(string? json, string? runningVersion)
    {
        if (string.IsNullOrWhiteSpace(json) || !ReleaseVersion.TryParse(runningVersion, out var running))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // A draft is not published and a pre-release is opt-in by nature. Neither should
            // reach somebody who only asked to be told about new versions.
            if (IsTrue(root, "draft") || IsTrue(root, "prerelease"))
            {
                return null;
            }

            if (!root.TryGetProperty("tag_name", out var tag)
                || !ReleaseVersion.TryParse(tag.GetString(), out var latest))
            {
                return null;
            }

            if (latest <= running)
            {
                return null;
            }

            var page = Text(root, "html_url");

            // Without a page there is nowhere honest to send anyone, so the whole result is
            // discarded rather than presented with a dead button.
            if (page is null || !IsGitHubUrl(page))
            {
                return null;
            }

            var (url, name, size, digest) = FindInstaller(root);

            return new AvailableRelease
            {
                Version = latest,
                Title = Text(root, "name") ?? $"GamerGod {latest}",
                PageUrl = page,
                InstallerUrl = url,
                InstallerName = name,
                InstallerBytes = size,
                Sha256 = digest,
            };
        }
        catch (JsonException)
        {
            // GitHub answers rate limits and outages with something that is not this shape.
            return null;
        }
    }

    private static (string? Url, string? Name, long Size, string? Digest) FindInstaller(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null, 0, null);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");

            if (name is null
                || url is null
                || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !IsGitHubUrl(url))
            {
                continue;
            }

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;

            // Reported as "sha256:<hex>". Anything else is treated as absent, which disables
            // downloading rather than downgrading to an unverified fetch.
            var digest = Text(asset, "digest");
            string? sha = null;

            if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                var hex = digest["sha256:".Length..].Trim().ToLowerInvariant();

                if (hex.Length == 64 && hex.All(Uri.IsHexDigit))
                {
                    sha = hex;
                }
            }

            return (url, name, size, sha);
        }

        return (null, null, 0, null);
    }

    /// <summary>
    /// Both addresses in the response come from a remote server and become a navigation or a
    /// download, so neither is taken on trust.
    /// </summary>
    public static bool IsGitHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsTrue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
