using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace GamerGod.Core.Library;

/// <summary>
/// Parses the manifests stores leave on disk.
///
/// <para>
/// All pure string handling, and in Core for the same reason the PresentMon parser is: it can
/// then be tested against real manifest text with nothing installed. The part that needs an
/// operating system is only finding the files.
/// </para>
/// </summary>
public static partial class StoreManifests
{
    /// <summary>
    /// Steam entries that are not games. Redistributables, Proton builds and the Linux runtimes
    /// all appear as installed apps, and a library that lists "Steamworks Common
    /// Redistributables" alongside your games looks broken.
    /// </summary>
    private static readonly ImmutableHashSet<string> NotGames =
        ImmutableHashSet.Create(StringComparer.Ordinal,
            "228980",   // Steamworks Common Redistributables
            "1070560",  // Steam Linux Runtime 1.0
            "1391110",  // Steam Linux Runtime 2.0 (soldier)
            "1628350",  // Steam Linux Runtime 3.0 (sniper)
            "1493710",  // Proton Experimental
            "2180100",  // Proton Hotfix
            "1887720",  // Proton 7.0
            "2230260",  // Proton 8.0
            "2805730"); // Proton 9.0

    [GeneratedRegex("\"appid\"\\s+\"(\\d+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamAppId();

    [GeneratedRegex("\"name\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamName();

    [GeneratedRegex("\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamInstallDir();

    [GeneratedRegex("\"path\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPath();

    /// <summary>
    /// Reads one Steam <c>appmanifest_*.acf</c>. Returns null when the file is not a game.
    /// </summary>
    public static SteamApp? ParseSteamManifest(string acf)
    {
        if (string.IsNullOrWhiteSpace(acf))
        {
            return null;
        }

        var id = SteamAppId().Match(acf);
        var name = SteamName().Match(acf);
        var directory = SteamInstallDir().Match(acf);

        if (!id.Success || !name.Success || !directory.Success)
        {
            return null;
        }

        var appId = id.Groups[1].Value;

        if (NotGames.Contains(appId))
        {
            return null;
        }

        var title = name.Groups[1].Value.Trim();

        // A blank or placeholder name means a partially written manifest - Steam creates one
        // the instant a download is queued, before it knows the title.
        if (title.Length == 0 || title.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new SteamApp(appId, title, directory.Groups[1].Value.Trim());
    }

    /// <summary>Library roots from <c>libraryfolders.vdf</c>. Steam allows several drives.</summary>
    public static ImmutableArray<string> ParseSteamLibraryFolders(string vdf)
    {
        if (string.IsNullOrWhiteSpace(vdf))
        {
            return [];
        }

        var paths = ImmutableArray.CreateBuilder<string>();

        foreach (var match in SteamLibraryPath().Matches(vdf).Cast<Match>())
        {
            // Steam escapes backslashes in VDF.
            var path = match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal).Trim();

            if (path.Length > 0 && !paths.Contains(path))
            {
                paths.Add(path);
            }
        }

        return paths.ToImmutable();
    }

    [GeneratedRegex("\"DisplayName\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex EpicDisplayName();

    [GeneratedRegex("\"AppName\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex EpicAppName();

    [GeneratedRegex("\"InstallLocation\"\\s*:\\s*\"([^\"]*)\"")]
    private static partial Regex EpicInstallLocation();

    /// <summary>
    /// Reads one Epic <c>.item</c> manifest.
    ///
    /// <para>
    /// Parsed with expressions rather than a JSON deserialiser deliberately: these files
    /// occasionally contain trailing content or fields that vary by launcher version, and a
    /// strict parse would drop an entire game over a detail nobody cares about. Three named
    /// fields are all that is needed.
    /// </para>
    /// </summary>
    public static EpicApp? ParseEpicManifest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var name = EpicDisplayName().Match(json);
        var appName = EpicAppName().Match(json);

        if (!name.Success || !appName.Success)
        {
            return null;
        }

        var title = name.Groups[1].Value.Trim();
        if (title.Length == 0)
        {
            return null;
        }

        var location = EpicInstallLocation().Match(json);

        return new EpicApp(
            appName.Groups[1].Value.Trim(),
            title,
            location.Success
                ? location.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal).Trim()
                : null);
    }
}

/// <param name="InstallDir">Folder name under a library's <c>steamapps\common</c>.</param>
public sealed record SteamApp(string AppId, string Name, string InstallDir);

public sealed record EpicApp(string AppName, string DisplayName, string? InstallLocation);
