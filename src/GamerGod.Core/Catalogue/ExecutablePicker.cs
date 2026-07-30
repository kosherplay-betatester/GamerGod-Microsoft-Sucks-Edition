using System.Collections.Immutable;

namespace GamerGod.Core.Catalogue;

/// <summary>
/// Chooses which executable in a program's folder is the one to start.
///
/// <para>
/// This exists because the obvious source is a trap. Windows records a <c>DisplayIcon</c> for
/// every installed program and it looks exactly like a launch target — but on a real machine
/// Steam, MSI Afterburner and RivaTuner Statistics Server all point it at <c>uninstall.exe</c>,
/// and all three leave <c>InstallLocation</c> empty. A launch button wired to <c>DisplayIcon</c>
/// starts the uninstaller for three of the most popular programs a gamer has installed.
/// </para>
///
/// <para>
/// So the folder is searched instead, and the rule that matters most is the negative one:
/// <see cref="IsUninstaller"/> is checked before anything else, and returning nothing is always
/// preferable to returning a guess. A missing Launch button is a small disappointment; the
/// wrong one is somebody's Steam library.
/// </para>
/// </summary>
public static class ExecutablePicker
{
    /// <summary>
    /// Names that remove software rather than run it.
    ///
    /// <para>
    /// Matched as a prefix on the bare filename, which covers Inno Setup's <c>unins000</c>,
    /// NSIS's <c>uninstall</c>, and the <c>Uninstall &lt;name&gt;</c> form. Deliberately broad:
    /// wrongly refusing to launch something costs a button, and wrongly launching an
    /// uninstaller costs an installation.
    /// </para>
    /// </summary>
    private static readonly string[] Destructive =
    [
        "uninstall", "uninst", "unins", "remove", "setup", "install",
        "cleanup", "repair", "modify",
    ];

    public static bool IsUninstaller(string fileName)
    {
        var bare = Bare(fileName);

        foreach (var prefix in Destructive)
        {
            if (bare.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The best launch candidate among a folder's executables, or null when none is convincing.
    /// </summary>
    /// <param name="displayName">The program's name as Windows records it, version and all.</param>
    /// <param name="folderName">The containing directory's name.</param>
    /// <param name="executables">Bare filenames, with or without extension.</param>
    public static string? Choose(
        string displayName, string folderName, ImmutableArray<string> executables)
    {
        if (executables.IsDefaultOrEmpty)
        {
            return null;
        }

        var safe = executables.Where(e => !IsUninstaller(e)).ToImmutableArray();

        if (safe.IsEmpty)
        {
            return null;
        }

        // The version has to come off first. "RivaTuner Statistics Server 7.3.7" yields the
        // acronym "rtss737", which matches nothing — the digits are part of the name only in
        // the registry's opinion.
        var named = StripVersion(displayName);

        var wanted = Squash(named);
        var folder = Squash(folderName);
        var acronym = Acronym(named);

        // Ordered by how much each is actually evidence rather than coincidence.
        var rules = new Func<string, bool>[]
        {
            // "MSI Afterburner" -> MSIAfterburner.exe
            e => Bare(e) == wanted,

            // "RivaTuner Statistics Server" -> RTSS.exe. Publishers abbreviate constantly and
            // the initials are a strong signal, not a guess.
            e => acronym.Length >= 3 && Bare(e) == acronym,

            // The folder is often named after the binary even when the display name is not.
            e => Bare(e) == folder,

            // "Dolphin 2506" installs Dolphin.exe: the display name carries a version the
            // filename does not.
            e => wanted.StartsWith(Bare(e), StringComparison.Ordinal) && Bare(e).Length >= 4,
            e => folder.StartsWith(Bare(e), StringComparison.Ordinal) && Bare(e).Length >= 4,
        };

        foreach (var rule in rules)
        {
            var match = safe.FirstOrDefault(rule);

            if (match is not null)
            {
                return match;
            }
        }

        // One candidate left after the uninstallers are gone is not a guess. Several is, and
        // this returns nothing rather than picking a favourite.
        return safe.Length == 1 ? safe[0] : null;
    }

    /// <summary>
    /// Drops trailing words that are only digits and dots — "MSI Afterburner 4.6.6" becomes
    /// "MSI Afterburner".
    ///
    /// <para>
    /// Only from the end, so a name where the number is the product keeps it: "FurMark 2" loses
    /// its 2 and matches nothing worse for it, but "Dolphin 2506" has to lose its build number
    /// or it matches nothing at all. Trailing-only also protects "3DMark", where the digit
    /// leads.
    /// </para>
    /// </summary>
    private static string StripVersion(string displayName)
    {
        var words = displayName.Split(
            [' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var end = words.Length;

        while (end > 1 && IsVersionWord(words[end - 1]))
        {
            end--;
        }

        return string.Join(' ', words[..end]);
    }

    private static bool IsVersionWord(string word)
    {
        var sawDigit = false;

        foreach (var c in word)
        {
            if (char.IsAsciiDigit(c))
            {
                sawDigit = true;
            }
            else if (c is not ('.' or ',' or 'v' or 'V'))
            {
                return false;
            }
        }

        return sawDigit;
    }

    /// <summary>Filename without a directory or an extension, lowercased.</summary>
    private static string Bare(string fileName)
    {
        var name = fileName;

        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            name = name[..dot];
        }

        return Squash(name);
    }

    /// <summary>Lowercased with every separator removed, so spacing and punctuation stop mattering.</summary>
    private static string Squash(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    /// <summary>Initials of the capitalised words: "RivaTuner Statistics Server" becomes "rss"… </summary>
    private static string Acronym(string displayName)
    {
        var builder = new System.Text.StringBuilder();
        var atStart = true;

        foreach (var c in displayName)
        {
            if (!char.IsLetterOrDigit(c))
            {
                atStart = true;
                continue;
            }

            // Capitals inside a word count too, so "RivaTuner" contributes R and T and the
            // whole name yields RTSS rather than RSS.
            if (atStart || char.IsUpper(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }

            atStart = false;
        }

        return builder.ToString();
    }
}
