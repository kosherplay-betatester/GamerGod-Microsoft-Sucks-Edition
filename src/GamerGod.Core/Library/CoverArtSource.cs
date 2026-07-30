using System.Collections.Immutable;

namespace GamerGod.Core.Library;

/// <summary>
/// Every decision involved in fetching cover art, with none of the fetching.
///
/// <para>
/// The download itself belongs in <c>GamerGod.Windows</c> because it touches sockets and the
/// filesystem. The judgements do not: which addresses are legitimate to ask, which app ids are
/// safe to interpolate into a path, and whether the bytes that came back are an image or an
/// error page. Those are the parts worth being sure about, so they live here where the test
/// suite can reach them without a network.
/// </para>
/// </summary>
public static class CoverArtSource
{
    /// <summary>
    /// A redirect to something enormous must not be able to fill the disk. Real portrait covers
    /// run 60-200 KB; this is generous by an order of magnitude and still bounded.
    /// </summary>
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Below this a JPEG is not art, and accepting it makes the grid look broken.
    ///
    /// <para>
    /// Found on the reference machine rather than reasoned about. Battlefield 6 has a
    /// <c>library_600x900.jpg</c> published at the expected address that returns HTTP 200 with
    /// a valid 300x450 JPEG — of flat grey. Four distinct shades across the whole image, 1,655
    /// bytes. It is a placeholder for a title whose publisher never uploaded Steam library art,
    /// and a blank grey rectangle in the grid is strictly worse than the generated tile it
    /// would replace.
    /// </para>
    ///
    /// <para>
    /// The size is the measurement. JPEG spends bytes on detail, so bytes-per-pixel is a
    /// direct read on how much image is actually there: that placeholder is 0.012 B/px while
    /// the real cover sitting next to it in the same library is 0.297 — a factor of twenty-five,
    /// not a close call. Covers only ever arrive at 300x450 or 600x900, so an absolute floor
    /// separates them without needing to decode anything.
    /// </para>
    ///
    /// <para>
    /// Set to reject placeholders rather than to catch every last one. If a genuinely sparse
    /// cover falls below it the tile falls back to the generated design, which is the harmless
    /// direction to be wrong in.
    /// </para>
    /// </summary>
    public const int MinBytes = 8 * 1024;

    /// <summary>
    /// Whether an app id is safe to place into a URL path and a filename.
    ///
    /// <para>
    /// Ids reach this from parsed store manifests — files GamerGod did not write. Digits only
    /// is not merely a format check: it is what stops <c>../</c> or a host separator from
    /// walking a request out of the intended path or a write out of the cache directory.
    /// </para>
    /// </summary>
    public static bool IsUsableAppId(string? appId)
    {
        if (string.IsNullOrEmpty(appId))
        {
            return false;
        }

        foreach (var c in appId)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Where to ask, in order, for one Steam title's art.
    ///
    /// <para>
    /// Portrait first — it is the only shape that fills a 2:3 tile. The landscape header is a
    /// last resort that gets cropped, but a cropped real cover still beats a letter on a
    /// gradient. Both hosts are the public asset CDNs the Steam client itself reads, and
    /// neither needs an account, a key, or a session.
    /// </para>
    /// </summary>
    public static ImmutableArray<string> CandidateUrls(string appId)
    {
        if (!IsUsableAppId(appId))
        {
            return [];
        }

        return
        [
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/library_600x900.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/library_600x900.jpg",
            $"https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/{appId}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
        ];
    }

    /// <summary>
    /// Whether a downloaded body is a real cover worth showing.
    ///
    /// <para>
    /// Two failures, both of which end with a permanently broken tile if they get through,
    /// because a cached file is never fetched again. A CDN that answers 200 with an HTML error
    /// page fails the start-of-image marker. A publisher's blank placeholder passes every
    /// structural check there is and fails only on <see cref="MinBytes"/>.
    /// </para>
    /// </summary>
    public static bool IsUsableCover(ReadOnlySpan<byte> bytes) =>
        bytes.Length is >= MinBytes and <= MaxBytes
        && bytes[0] == 0xFF
        && bytes[1] == 0xD8
        && bytes[2] == 0xFF;
}
