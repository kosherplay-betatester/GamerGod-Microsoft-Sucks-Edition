using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GamerGod.Windows;

/// <summary>
/// Finds the cover art Steam has already cached on disk.
///
/// <para>
/// Nothing is downloaded and no network call is made. Steam keeps library art under
/// <c>appcache\librarycache</c> for every title you have looked at, and reading a file the
/// user's own client already put there is the only way to show box art without breaking
/// Charter Article IV.
/// </para>
///
/// <para>
/// Art is often missing, because Steam fetches it lazily — a game you own but have never
/// opened in the library grid has only a 32-pixel icon cached. That is expected, and the
/// interface draws a designed tile rather than a broken image.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class SteamArtwork
{
    /// <summary>
    /// Portrait cover for a Steam app id, or null when Steam has not cached one.
    ///
    /// <para>
    /// Preference order matters. <c>library_600x900</c> is the portrait cover and the only one
    /// shaped for a grid; the header is landscape and looks wrong stretched into a tile; the
    /// hash-named files are 32-pixel icons that would be unrecognisable scaled up.
    /// </para>
    /// </summary>
    public static string? FindCover(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var cache = LibraryCache();
        if (cache is null)
        {
            return null;
        }

        // Newer clients keep a folder per app; older ones prefix the file with the id.
        foreach (var candidate in new[]
                 {
                     Path.Combine(cache, appId, "library_600x900.jpg"),
                     Path.Combine(cache, appId, "library_600x900_2x.jpg"),
                     Path.Combine(cache, $"{appId}_library_600x900.jpg"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Landscape header art, used when no portrait cover has been cached. Wrong shape for a
    /// grid tile but far better than nothing, and it is what Steam itself falls back to.
    /// </summary>
    public static string? FindHeader(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return null;
        }

        var cache = LibraryCache();
        if (cache is null)
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(cache, appId, "header.jpg"),
                     Path.Combine(cache, $"{appId}_header.jpg"),
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? LibraryCache()
    {
        try
        {
            var steam = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")
                ?.GetValue("SteamPath") as string;

            if (string.IsNullOrWhiteSpace(steam))
            {
                return null;
            }

            // Steam writes this value with forward slashes.
            var cache = Path.Combine(steam.Replace('/', '\\'), "appcache", "librarycache");
            return Directory.Exists(cache) ? cache : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
