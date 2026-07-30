using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using GamerGod.Core.Catalogue;

namespace GamerGod.Windows;

/// <summary>
/// Downloads a project's own logo from its own website, once, on request.
///
/// <para>
/// The second and last place GamerGod opens an outbound connection, behind the same explicit
/// opt-in as cover art. What leaves the machine is a request for a public page and then for the
/// image that page declares — no account, no cookie, no identifier, and nothing about the
/// machine or what is installed on it.
/// </para>
///
/// <para>
/// Only reached for software that is <em>not</em> installed. Anything already on the machine has
/// its icon inside its own executable, and <see cref="IconExtractor"/> reads it with no network
/// at all.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppIconCache
{
    private static readonly HttpClient Http = CreateClient();

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerGod",
        "icons");

    /// <summary>An already-downloaded icon for this catalogue id, or null. Never touches the network.</summary>
    public static string? FindCached(string entryId)
    {
        if (!IsSafeId(entryId))
        {
            return null;
        }

        var path = Path.Combine(CacheDirectory, entryId + ".img");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Fetches the logo for a catalogue entry and returns the cached file, or null.
    ///
    /// <para>
    /// Null is an ordinary outcome. Several projects sit behind bot protection that refuses an
    /// anonymous request, and some publish only an SVG, which WPF cannot draw. Those keep the
    /// generated mark, which is a designed state rather than a failed one.
    /// </para>
    /// </summary>
    public static async Task<string?> TryFetchAsync(
        string entryId, string homepage, CancellationToken token)
    {
        if (FindCached(entryId) is { } existing)
        {
            return existing;
        }

        if (!IsSafeId(entryId))
        {
            return null;
        }

        var host = Uri.TryCreate(homepage, UriKind.Absolute, out var page) ? page.Host : string.Empty;

        foreach (var url in await CandidatesAsync(homepage, token).ConfigureAwait(false))
        {
            var bytes = await TryDownloadAsync(url, token).ConfigureAwait(false);

            if (bytes is null)
            {
                continue;
            }

            // A GitHub Pages or hosting-provider default is a real image served from the right
            // address, and showing it would put GitHub's logo on Cemu. Rejected here rather
            // than displayed, which is why this returns null and the tile keeps its own mark.
            if (IsGeneric(entryId, host, bytes))
            {
                continue;
            }

            return Persist(entryId, bytes);
        }

        return null;
    }

    /// <summary>
    /// Whether these exact bytes are somebody's default favicon rather than this project's logo.
    ///
    /// <para>
    /// Found on the real catalogue: Heroic, Cemu and Google Play Games all resolved to the same
    /// 4,286-byte file, and DOSBox Staging and Azahar to the same 15,406-byte one — hosting
    /// defaults, served correctly from each project's own domain. Nothing about the request
    /// looks wrong; only the fact that several unrelated projects return an identical image.
    /// </para>
    ///
    /// <para>
    /// So identity across <em>different hosts</em> is the test. Two entries on the same site —
    /// GPU-Z and ThrottleStop are both TechPowerUp — legitimately share a mark and are kept.
    /// The first project to claim a hash keeps it until a second host claims the same bytes, at
    /// which point both are dropped and the hash is remembered as generic.
    /// </para>
    /// </summary>
    private static bool IsGeneric(string entryId, string host, byte[] bytes)
    {
        try
        {
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));

            Directory.CreateDirectory(CacheDirectory);
            var indexPath = Path.Combine(CacheDirectory, "index.tsv");

            var lines = File.Exists(indexPath) ? File.ReadAllLines(indexPath) : [];

            foreach (var line in lines)
            {
                var parts = line.Split('\t');

                if (parts.Length != 3 || parts[0] != hash)
                {
                    continue;
                }

                if (parts[1] == entryId || string.Equals(parts[2], host, StringComparison.OrdinalIgnoreCase))
                {
                    // Same entry re-fetched, or a sibling product on the same site.
                    return false;
                }

                // A second host claiming identical bytes. Neither owns it — retract the first.
                var claimed = Path.Combine(CacheDirectory, parts[1] + ".img");

                if (File.Exists(claimed))
                {
                    File.Delete(claimed);
                }

                return true;
            }

            File.AppendAllText(indexPath, $"{hash}\t{entryId}\t{host}{Environment.NewLine}");
            return false;
        }
        catch (Exception)
        {
            // Without the index this check cannot be made, and showing a possibly-generic icon
            // beats failing the fetch.
            return false;
        }
    }

    /// <summary>
    /// What the page declares first, then the conventional paths.
    ///
    /// <para>
    /// Reading the page costs one extra request and is what finds the good artwork — on the
    /// real sites, fixed paths alone locate about half, and miss every project that publishes
    /// its icon on a CDN or under a hashed asset name.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> CandidatesAsync(
        string homepage, CancellationToken token)
    {
        var candidates = new List<string>();

        try
        {
            using var response = await Http.GetAsync(homepage, token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var html = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);

                // The address the page was actually served from, so relative hrefs resolve
                // against the final location rather than the one that redirected.
                var final = response.RequestMessage?.RequestUri?.ToString() ?? homepage;

                candidates.AddRange(AppIconSource.DeclaredIcons(html, final));
            }
        }
        catch (Exception)
        {
            // Bot protection, a timeout, or no network. The conventional paths may still work.
        }

        candidates.AddRange(AppIconSource.FallbackPaths(homepage));

        return candidates;
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !AppIconSource.IsFetchable(uri))
        {
            return null;
        }

        try
        {
            using var response = await Http
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK
                || response.Content.Headers.ContentLength is > AppIconSource.MaxBytes)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();

            var chunk = new byte[32 * 1024];
            int read;

            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > AppIconSource.MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();

            return AppIconSource.IsUsableIcon(bytes) ? bytes : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Persist(string entryId, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var final = Path.Combine(CacheDirectory, entryId + ".img");
            var temp = final + ".part";

            File.WriteAllBytes(temp, bytes);
            File.Move(temp, final, overwrite: true);

            return final;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Catalogue ids are authored in this repository, not parsed from anything — but they become
    /// a filename, so the guard stays.
    /// </summary>
    private static bool IsSafeId(string entryId)
    {
        if (string.IsNullOrWhiteSpace(entryId))
        {
            return false;
        }

        foreach (var c in entryId)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        // Generic, for the same reason the cover-art client's is: a distinctive agent string
        // would let a site count GamerGod installs from its logs.
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        return client;
    }
}
