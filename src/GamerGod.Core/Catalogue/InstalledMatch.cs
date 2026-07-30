namespace GamerGod.Core.Catalogue;

/// <summary>
/// Decides whether a name in Windows' installed-programs list is the catalogue entry it looks
/// like.
///
/// <para>
/// Publishers do not agree on how to name themselves. The registry holds "Steam", but also
/// "Epic Games Launcher", "GOG GALAXY 2.0", "Battle.net", "Dolphin 2506", and "PPSSPP 1.19.3".
/// Exact equality misses most of them; a loose contains match is worse, because "ares" appears
/// inside "VMware Workstation" and "itch" inside "Twitch" — and a false match puts a Launch
/// button on the wrong program.
/// </para>
/// </summary>
public static class InstalledMatch
{
    /// <summary>
    /// Whether an installed program is the catalogue entry, allowing only a version to differ.
    ///
    /// <para>
    /// Compared word by word rather than as one flattened string. Flattening first and prefix
    /// matching looks equivalent and is not: it destroys the word boundary, so "Steam" matches
    /// "SteamCMD Deluxe" and "Xenia" matches "Xenia Manager" — two cases where the Launch
    /// button would start a different program than the one it names.
    /// </para>
    ///
    /// <para>
    /// The only difference tolerated is a version, because that is the only one publishers
    /// actually introduce: "Dolphin 2506", "GOG GALAXY 2.0", "MSI Afterburner 4.6.6". An extra
    /// <em>word</em> means a different product.
    /// </para>
    /// </summary>
    public static bool IsSameProgram(string catalogueName, string installedName)
    {
        var wanted = Tokenise(catalogueName);
        var found = Tokenise(installedName);

        if (wanted.Count == 0 || found.Count < wanted.Count)
        {
            return false;
        }

        // Everything before the last word has to match outright.
        for (var i = 0; i < wanted.Count - 1; i++)
        {
            if (wanted[i] != found[i])
            {
                return false;
            }
        }

        var lastWanted = wanted[^1];
        var lastFound = found[wanted.Count - 1];

        // The last word may carry a version glued to it — "HWiNFO" against "HWiNFO64".
        if (lastFound != lastWanted
            && !(lastFound.StartsWith(lastWanted, StringComparison.Ordinal)
                 && IsVersion(lastFound[lastWanted.Length..])))
        {
            return false;
        }

        // Anything left over has to be a version too, never another word.
        for (var i = wanted.Count; i < found.Count; i++)
        {
            if (!IsVersion(found[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Split on anything that is not a letter or digit, lowercased.
    ///
    /// <para>
    /// Collapses "Battle.net" against "Battle.Net" and "EA app" against "EA App" without a rule
    /// per publisher, while keeping "Xenia Manager" as two words rather than one.
    /// </para>
    /// </summary>
    private static List<string> Tokenise(string value)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>Digits only, once the dots have been stripped by tokenising.</summary>
    private static bool IsVersion(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        foreach (var c in token)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
