using System.Text;

namespace GamerGod.Core.Search;

/// <summary>
/// Matches what somebody typed against what they meant.
///
/// <para>
/// A plain substring filter fails the searches people actually perform. "battlefeild" finds
/// nothing. "wot" finds nothing, though World of Tanks is right there. "over watch" finds
/// nothing because of a space. Each of those is a person looking at a game that is in the list
/// and being told it is not.
/// </para>
///
/// <para>
/// So matching runs in tiers, best first, and the score carries which tier answered. That
/// ordering matters as much as the matching: a typo-corrected hit must never outrank something
/// the user literally typed, or searching "war" would surface "Warframe" below some six-edit
/// approximation of it.
/// </para>
/// </summary>
public static class FuzzySearch
{
    /// <summary>Shorter than this and fuzzy matching would relate almost anything to anything.</summary>
    private const int MinimumFuzzyLength = 4;

    /// <summary>An acronym needs enough letters to be evidence rather than coincidence.</summary>
    private const int MinimumAcronymLength = 2;

    /// <summary>
    /// How well <paramref name="candidate"/> answers <paramref name="query"/>, or null for no
    /// match. Higher is better; the bands are far enough apart that a weaker tier can never
    /// overtake a stronger one.
    /// </summary>
    public static int? Score(string? query, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var q = Normalise(query);
        var c = Normalise(candidate);

        if (q.Length == 0 || c.Length == 0)
        {
            return null;
        }

        if (q == c)
        {
            return 1000;
        }

        if (c.StartsWith(q, StringComparison.Ordinal))
        {
            // Longer candidates are slightly worse answers to the same prefix: typing "war"
            // should put "War Thunder" above "Warhammer 40,000: Darktide".
            return 900 - Math.Min(c.Length - q.Length, 80);
        }

        var words = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // "tanks" should find "World of Tanks" — a word starting with the query is a strong
        // hit even when the full string does not begin with it.
        foreach (var word in words)
        {
            if (word.StartsWith(q, StringComparison.Ordinal))
            {
                return 800 - Math.Min(word.Length - q.Length, 60);
            }
        }

        var position = c.IndexOf(q, StringComparison.Ordinal);
        if (position >= 0)
        {
            // The plain %like% case, and it stays: it is what people expect from a search box.
            return 700 - Math.Min(position, 60);
        }

        // Every word typed appears somewhere, in any order. Handles "exile path" and the
        // reversed word orders people produce when half-remembering a title.
        var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 1 && terms.All(t => c.Contains(t, StringComparison.Ordinal)))
        {
            return 650;
        }

        // "wot" finds World of Tanks; "cs" finds Counter-Strike.
        if (q.Length >= MinimumAcronymLength && Acronym(words).StartsWith(q, StringComparison.Ordinal))
        {
            return 600;
        }

        // Dropped letters: "btlfield" against "battlefield".
        if (q.Length >= MinimumFuzzyLength && IsSubsequence(q, c))
        {
            return 500;
        }

        if (q.Length < MinimumFuzzyLength)
        {
            return null;
        }

        // Finally, real typos. Compared against the whole string and against each word, because
        // "battlefeild" should match the word inside "Battlefield 6" and not be diluted by the
        // rest of the title's length.
        var best = int.MaxValue;

        foreach (var target in words.Append(c))
        {
            if (Math.Abs(target.Length - q.Length) > Tolerance(q.Length))
            {
                continue;
            }

            var distance = EditDistance(q, target, Tolerance(q.Length));

            if (distance >= 0 && distance < best)
            {
                best = distance;
            }
        }

        return best <= Tolerance(q.Length) ? 400 - (best * 40) : null;
    }

    /// <summary>
    /// How wrong a word may be and still count.
    ///
    /// <para>
    /// About one mistake per three characters. Calibrated against misspellings people actually
    /// produce rather than picked for neatness: "fortnight" for Fortnite is three edits over
    /// nine letters, and a tighter curve rejects it while a person is looking straight at the
    /// game.
    /// </para>
    ///
    /// <para>
    /// Loosening this is safe because the score is also the sort key. A weak correction lands in
    /// the lowest band, so it can appear far down a list without ever displacing something the
    /// user literally typed — which is what would actually make the search feel wrong.
    /// </para>
    /// </summary>
    private static int Tolerance(int length) =>
        length < MinimumFuzzyLength ? 0 : Math.Clamp(length / 3, 1, 4);

    /// <summary>
    /// Best score across several fields, so a search can cover a title, its genre and its
    /// publisher at once.
    ///
    /// <para>
    /// Fields are given in order of authority — title first — and each later one carries a small
    /// penalty. Small on purpose: two points cannot move a result between tiers, so it only ever
    /// settles a tie <em>within</em> one. Searching "wot" produced exactly that tie, matching
    /// World of Tanks by its title and Magic Spellslingers by its publisher, Wizards of the
    /// Coast. Both are genuine acronyms; the one in the name is the better answer.
    /// </para>
    /// </summary>
    public static int? Best(string? query, params string?[] fields)
    {
        if (fields is null)
        {
            return null;
        }

        int? best = null;

        for (var i = 0; i < fields.Length; i++)
        {
            if (Score(query, fields[i]) is not { } score)
            {
                continue;
            }

            var weighted = score - (i * 2);

            if (best is null || weighted > best)
            {
                best = weighted;
            }
        }

        return best;
    }

    public static bool Matches(string? query, params string?[] fields) =>
        string.IsNullOrWhiteSpace(query) || Best(query, fields) is not null;

    /// <summary>
    /// Lowercased, with punctuation turned into word breaks and runs of spaces collapsed.
    ///
    /// <para>
    /// Punctuation becomes a space rather than vanishing, so "Counter-Strike" yields two words
    /// and its acronym is "cs". Deleting it outright would give "counterstrike" and one word,
    /// and the acronym search would never fire.
    /// </para>
    /// </summary>
    private static string Normalise(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(char.ToLowerInvariant(c));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private static string Acronym(string[] words)
    {
        var builder = new StringBuilder(words.Length);

        foreach (var word in words)
        {
            if (word.Length > 0)
            {
                builder.Append(word[0]);
            }
        }

        return builder.ToString();
    }

    private static bool IsSubsequence(string query, string candidate)
    {
        var index = 0;

        foreach (var c in candidate)
        {
            if (index < query.Length && query[index] == c)
            {
                index++;
            }
        }

        return index == query.Length;
    }

    /// <summary>
    /// Optimal string alignment distance — Levenshtein plus adjacent transposition.
    ///
    /// <para>
    /// The transposition case is the point. Swapping two letters is the most common typing
    /// mistake there is, and plain Levenshtein charges two edits for it: "battelfield" would
    /// need a tolerance of two where one should do, and that same tolerance would start
    /// matching genuinely different words.
    /// </para>
    ///
    /// <para>
    /// Abandoned early once every cell in a row exceeds <paramref name="limit"/>, which is what
    /// keeps this cheap enough to run over several hundred entries on every keystroke. Returns
    /// -1 when it gives up.
    /// </para>
    /// </summary>
    private static int EditDistance(string a, string b, int limit)
    {
        var previousPrevious = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowBest = current[0];

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                var value = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, previousPrevious[j - 2] + 1);
                }

                current[j] = value;

                if (value < rowBest)
                {
                    rowBest = value;
                }
            }

            if (rowBest > limit)
            {
                return -1;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[b.Length];
    }
}
