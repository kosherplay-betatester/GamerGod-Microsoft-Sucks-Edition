using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using GamerGod.Core.Updates;

namespace GamerGod.Windows;

/// <summary>Where a downloaded installer ended up, and whether it can be trusted.</summary>
public sealed record DownloadedUpdate
{
    public required bool Verified { get; init; }

    /// <summary>Set only when <see cref="Verified"/> is true.</summary>
    public string? Path { get; init; }

    /// <summary>What went wrong, in words worth showing.</summary>
    public string? Problem { get; init; }

    /// <summary>The SHA-256 actually computed over what arrived.</summary>
    public string? ActualSha256 { get; init; }
}

/// <summary>
/// Asks GitHub whether a newer GamerGod exists, and fetches it when told to.
///
/// <para>
/// Charter Article IV forbids an update ping and permits an outbound connection the user
/// explicitly initiated — naming "checking for a release" as an example of the permitted kind.
/// Those are the same sentence describing two different things, and the difference is entirely
/// consent: this is off by default, turned on in Settings, and never enabled by an upgrade.
/// </para>
///
/// <para>
/// What it will not do is replace itself. A program that silently downloads and executes code
/// from the internet is one compromised account away from running an attacker's installer on
/// every machine that has it. So the verified installer is handed to the user, and the elevation
/// prompt and the installer's own "here is what this does" dialog both still happen.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class UpdateChecker
{
    /// <summary>Nothing plausible is larger; the installer is about 73 MB.</summary>
    private const long MaxInstallerBytes = 400L * 1024 * 1024;

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// The newest published release, or null when there is nothing newer to report.
    ///
    /// <para>
    /// Every failure returns null. A machine that is offline, rate-limited, or behind a proxy
    /// that mangles the response has no update problem, and an error dialog at startup for a
    /// feature nobody was waiting on would be its own bug.
    /// </para>
    /// </summary>
    public static async Task<AvailableRelease?> CheckAsync(string runningVersion, CancellationToken token)
    {
        try
        {
            using var response = await Http
                .GetAsync(ReleaseCheck.LatestReleaseUrl, token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            return ReleaseCheck.Evaluate(json, runningVersion);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads a release's installer and verifies it against the digest GitHub published.
    ///
    /// <para>
    /// The digest arrives from the API over TLS and the file from a CDN, so checking one against
    /// the other detects a substituted, corrupted or truncated download before anything is run.
    /// A mismatch deletes the file: leaving an unverified installer on disk with a warning is
    /// how somebody ends up running it anyway.
    /// </para>
    /// </summary>
    public static async Task<DownloadedUpdate> DownloadAsync(
        AvailableRelease release, IProgress<double>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.InstallerUrl is not { } url || release.Sha256 is not { } expected)
        {
            return new DownloadedUpdate
            {
                Verified = false,
                Problem = "This release did not publish a verifiable installer.",
            };
        }

        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GamerGod", "updates");

        var destination = Path.Combine(folder, release.InstallerName ?? "GamerGod-Setup.exe");
        var partial = destination + ".part";

        try
        {
            Directory.CreateDirectory(folder);

            using (var response = await Http
                       .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                       .ConfigureAwait(false))
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    return new DownloadedUpdate
                    {
                        Verified = false,
                        Problem = $"GitHub answered {(int)response.StatusCode} for the installer.",
                    };
                }

                var total = response.Content.Headers.ContentLength ?? release.InstallerBytes;

                if (total > MaxInstallerBytes)
                {
                    return new DownloadedUpdate
                    {
                        Verified = false,
                        Problem = "The installer is implausibly large; nothing was downloaded.",
                    };
                }

                using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var file = File.Create(partial);

                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    written += read;

                    if (written > MaxInstallerBytes)
                    {
                        throw new InvalidOperationException("installer exceeded the size ceiling");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);

                    if (total > 0)
                    {
                        progress?.Report((double)written / total);
                    }
                }
            }

            var actual = await Sha256Async(partial, token).ConfigureAwait(false);

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);

                return new DownloadedUpdate
                {
                    Verified = false,
                    ActualSha256 = actual,
                    Problem = "The downloaded file does not match the fingerprint GitHub published, "
                            + "so it was deleted without being run.",
                };
            }

            File.Move(partial, destination, overwrite: true);

            return new DownloadedUpdate { Verified = true, Path = destination, ActualSha256 = actual };
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }
            }
            catch (Exception)
            {
                // Nothing further to do about a temporary file that will not delete.
            }

            return new DownloadedUpdate { Verified = false, Problem = ex.Message };
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);

        return Convert.ToHexStringLower(
            await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        // The API rejects a request with no user agent. This one identifies the software and
        // nothing else — no version, no machine, no install id — so the request cannot be tied
        // to a particular copy or counted as one.
        client.DefaultRequestHeaders.Add("User-Agent", "GamerGod");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }
}
