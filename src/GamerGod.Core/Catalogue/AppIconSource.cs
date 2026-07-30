using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace GamerGod.Core.Catalogue;

/// <summary>
/// Works out where a project publishes its own logo.
///
/// <para>
/// An installed program has its icon inside its executable, which is why the Apps page can show
/// real artwork with no network at all. A program you have <em>not</em> installed has no such
/// file, so the only honest source of its mark is the project's own website — the same place a
/// browser gets the tab icon.
/// </para>
///
/// <para>
/// Guessing <c>/favicon.ico</c> is not enough. Checked against the real sites, fixed paths find
/// artwork for about half: Ubisoft publishes a 228-pixel icon on a separate CDN, Battle.net a
/// 192-pixel one under <c>/static</c>, and CapFrameX a 120-pixel Apple touch icon — none of
/// them at a path anyone would guess. So the page is read for what it declares, exactly as a
/// browser does, and the guesses are the fallback rather than the strategy.
/// </para>
/// </summary>
public static partial class AppIconSource
{
    /// <summary>Icons are small. Anything larger than this is not one.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Below this a file is a truncated download or an error page, not an icon. Far lower than
    /// the cover-art floor because a clean 16-pixel favicon really can be a few hundred bytes.
    /// </summary>
    public const int MinBytes = 64;

    [GeneratedRegex(
        """<link[^>]+rel\s*=\s*["'][^"']*icon[^"']*["'][^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkTag { get; }

    [GeneratedRegex(
        """href\s*=\s*["']([^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Href { get; }

    [GeneratedRegex(
        """sizes\s*=\s*["'](\d+)x\d+["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sizes { get; }

    /// <summary>
    /// Fixed paths to try when a page declares nothing, largest first.
    ///
    /// <para>
    /// The Apple touch icon comes first because it is by convention 180 pixels square, while
    /// <c>favicon.ico</c> is frequently 16 — unusable at tile size.
    /// </para>
    /// </summary>
    public static ImmutableArray<string> FallbackPaths(string homepage)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var uri) || !IsFetchable(uri))
        {
            return [];
        }

        var root = $"{uri.Scheme}://{uri.Authority}";

        return
        [
            $"{root}/apple-touch-icon.png",
            $"{root}/apple-touch-icon-precomposed.png",
            $"{root}/favicon.ico",
        ];
    }

    /// <summary>
    /// Every icon a page declares, best first.
    ///
    /// <para>
    /// Ranked by the declared <c>sizes</c> attribute, since the largest is the only one worth
    /// showing at 40 pixels. A PNG with no declared size outranks an unsized <c>.ico</c>,
    /// because an undeclared PNG is usually 32 or larger while an undeclared ICO is usually 16.
    /// </para>
    /// </summary>
    public static ImmutableArray<string> DeclaredIcons(string html, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(html)
            || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)
            || !IsFetchable(page))
        {
            return [];
        }

        var found = new List<(int Rank, string Url)>();

        foreach (var tag in LinkTag.Matches(html).Cast<Match>())
        {
            var href = Href.Match(tag.Value).Groups[1].Value;

            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (!Uri.TryCreate(page, href.Trim(), out var absolute) || !IsFetchable(absolute))
            {
                continue;
            }

            var declared = Sizes.Match(tag.Value);

            var rank = declared.Success && int.TryParse(declared.Groups[1].Value, out var size)
                ? size
                : absolute.AbsolutePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? 32 : 16;

            found.Add((rank, absolute.ToString()));
        }

        return
        [
            .. found
                .OrderByDescending(f => f.Rank)
                .Select(f => f.Url)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Whether an address may be fetched.
    ///
    /// <para>
    /// The href comes out of HTML served by a remote host and becomes the target of a request,
    /// so the scheme is pinned. The <em>host</em> deliberately is not — Ubisoft's icon lives on
    /// static-dm.ubisoft.com and Battle.net's on a Blizzard CDN, and refusing cross-host icons
    /// would reject most of the real ones. What bounds the risk instead is that the response is
    /// only ever decoded as an image, and only after passing a size check.
    /// </para>
    /// </summary>
    public static bool IsFetchable(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && !uri.IsLoopback;

    /// <summary>
    /// Whether downloaded bytes are an image this interface can draw.
    ///
    /// <para>
    /// Sniffed rather than trusted, because a site that has removed its icon commonly answers
    /// 200 with an HTML error page, and caching that would leave the tile permanently blank.
    /// </para>
    /// </summary>
    public static bool IsUsableIcon(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < MinBytes or > MaxBytes)
        {
            return false;
        }

        // PNG
        if (bytes is [0x89, 0x50, 0x4E, 0x47, ..])
        {
            return true;
        }

        // ICO and CUR share a header; the third byte is the type.
        if (bytes is [0x00, 0x00, 0x01 or 0x02, 0x00, ..])
        {
            return true;
        }

        // JPEG
        if (bytes is [0xFF, 0xD8, 0xFF, ..])
        {
            return true;
        }

        // GIF
        if (bytes is [(byte)'G', (byte)'I', (byte)'F', ..])
        {
            return true;
        }

        // SVG is text and can begin with a declaration, a comment or the tag itself. Accepted
        // because several projects publish only an SVG, and WPF cannot draw one — the caller
        // rejects it, but sniffing it here keeps the reason clear rather than silent.
        return false;
    }
}
