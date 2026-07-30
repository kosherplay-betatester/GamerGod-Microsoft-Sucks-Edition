using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using GamerGod.Core.Library;

namespace GamerGod.Windows;

/// <summary>
/// Downloads box art for titles whose store has not cached any locally, once, on request.
///
/// <para>
/// <b>This is the only code in GamerGod that opens an outbound connection.</b> Charter Article
/// IV permits exactly one kind: a connection the user explicitly initiated. So nothing here
/// runs on its own — <see cref="TryFetchAsync"/> is only ever reached after somebody presses
/// the button that says what it is about to do, and the preference that button sets can be
/// turned off again in Settings. There is no background refresh, no update ping and no
/// schedule.
/// </para>
///
/// <para>
/// What actually leaves the machine is one anonymous HTTPS GET per title, for a numeric store
/// id, against the same public CDN the store client itself uses. No account, no cookie, no
/// identifier, no list of what is installed — the requests are indistinguishable from opening
/// those store pages in a browser. The result is written to disk so it happens once per game
/// and never again.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class CoverArtCache
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Where fetched art lives. Per user, so no elevation is involved.</summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GamerGod",
        "covers");

    /// <summary>An already-downloaded cover for this app id, or null. Never touches the network.</summary>
    public static string? FindCached(string appId)
    {
        if (!CoverArtSource.IsUsableAppId(appId))
        {
            return null;
        }

        var path = Path.Combine(CacheDirectory, appId + ".jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Fetches portrait art for a Steam app id and returns the cached file path, or null if
    /// the title has no published art or the network is unavailable.
    ///
    /// <para>
    /// Returning null is an ordinary outcome, not an error. Plenty of app ids — demos, tools,
    /// delisted titles — have no portrait art at all, and a machine that is offline is not a
    /// machine with a problem. The caller draws its generated tile and says nothing.
    /// </para>
    /// </summary>
    public static async Task<string?> TryFetchAsync(string appId, CancellationToken token)
    {
        if (FindCached(appId) is { } existing)
        {
            return existing;
        }

        // CandidateUrls returns nothing for an id that is not safe to interpolate, so a
        // malformed manifest entry falls out here rather than reaching a socket.
        foreach (var url in CoverArtSource.CandidateUrls(appId))
        {
            var bytes = await TryDownloadAsync(url, token).ConfigureAwait(false);

            if (bytes is not null)
            {
                return Persist(appId, bytes);
            }
        }

        // No portrait cover exists for this title. Ask the store where its header art lives
        // and take that instead — landscape, so the tile presents it as a banner rather than
        // cropping it, but real key art all the same.
        return await TryFetchHeaderAsync(appId, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The fallback for titles whose publisher never uploaded a portrait capsule.
    ///
    /// <para>
    /// Two requests rather than one, and only for titles that already failed the cheap path.
    /// The store endpoint is rate-limited, so a large library will eventually start being
    /// refused — which costs nothing, because a refusal is indistinguishable from "no art" and
    /// pressing the button again picks up where it left off.
    /// </para>
    /// </summary>
    private static async Task<string?> TryFetchHeaderAsync(string appId, CancellationToken token)
    {
        if (CoverArtSource.StoreDetailsUrl(appId) is not { } detailsUrl)
        {
            return null;
        }

        string json;
        try
        {
            using var response = await Http.GetAsync(detailsUrl, token).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        // Validated against an allow-list before it is fetched: this address arrived from a
        // remote server and is about to become the target of a request.
        if (CoverArtSource.ExtractHeaderImageUrl(json, appId) is not { } headerUrl)
        {
            return null;
        }

        var bytes = await TryDownloadAsync(headerUrl, token).ConfigureAwait(false);

        return bytes is null ? null : Persist(appId, bytes);
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken token)
    {
        try
        {
            using var response = await Http
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > CoverArtSource.MaxBytes)
            {
                return null;
            }

            using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();

            // Copied with a ceiling rather than ReadAsByteArrayAsync, which would happily
            // allocate whatever a server chose to send when it declared no length.
            var chunk = new byte[64 * 1024];
            int read;

            while ((read = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > CoverArtSource.MaxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();

            // An error page served as 200, or a publisher's blank grey placeholder, would
            // otherwise poison the cache permanently and leave the tile broken forever.
            return CoverArtSource.IsUsableCover(bytes) ? bytes : null;
        }
        catch (Exception)
        {
            // Offline, DNS failure, TLS failure, timeout, cancellation. All the same outcome:
            // no art, generated tile, no message. Cover art is not worth an error dialog.
            return null;
        }
    }

    /// <summary>
    /// Written to a temporary name and moved into place, so a process killed mid-download
    /// cannot leave a truncated file that every future launch then treats as cached.
    /// </summary>
    private static string? Persist(string appId, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);

            var final = Path.Combine(CacheDirectory, appId + ".jpg");
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            // Nothing to persist between requests, and a cookie is exactly the sort of thing
            // that turns four anonymous GETs into a correlatable session.
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(12),
        };

        // Deliberately generic. A distinctive agent string would let a CDN count GamerGod
        // installs from its logs, which is the telemetry Article IV forbids collecting —
        // it should not be collectable by someone else either.
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");

        return client;
    }
}
