using System.Globalization;

namespace GamerGod.Core.Updates;

/// <summary>
/// A version, compared the way releases are actually ordered.
///
/// <para>
/// Needed because the two strings being compared never arrive in the same shape. The running
/// build reports <c>1.1.0+67603ed…</c> — the informational version, with the commit glued on —
/// while a GitHub tag reads <c>v1.1.0</c>. String comparison says those differ, and would offer
/// an update to the version already installed every time the app started.
/// </para>
///
/// <para>
/// A pre-release suffix sorts <em>below</em> the same numbers without one, so <c>1.2.0-beta.1</c>
/// never presents itself as newer than <c>1.2.0</c>.
/// </para>
/// </summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<ReleaseVersion>
{
    public bool IsPreRelease => PreRelease.Length > 0;

    public static bool TryParse(string? value, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();

        // Tags are conventionally prefixed; the assembly's informational version is not.
        if (text.StartsWith('v') || text.StartsWith('V'))
        {
            text = text[1..];
        }

        // Build metadata never affects ordering, so it is discarded rather than parsed.
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            text = text[..plus];
        }

        var preRelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            preRelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');

        if (parts.Length is 0 or > 4)
        {
            return false;
        }

        // A fourth component is the .NET revision, which semantic versioning does not have and
        // which releases here never use. Parsed so it does not fail, ignored so it cannot make
        // 1.1.0.0 look newer than 1.1.0.
        if (!TryComponent(parts, 0, out var major)
            || !TryComponent(parts, 1, out var minor)
            || !TryComponent(parts, 2, out var patch))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, preRelease);
        return true;
    }

    private static bool TryComponent(string[] parts, int index, out int value)
    {
        if (index >= parts.Length)
        {
            value = 0;
            return true;
        }

        return int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    public int CompareTo(ReleaseVersion other)
    {
        var numeric = Major.CompareTo(other.Major);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Minor.CompareTo(other.Minor);
        if (numeric != 0)
        {
            return numeric;
        }

        numeric = Patch.CompareTo(other.Patch);
        if (numeric != 0)
        {
            return numeric;
        }

        // Equal numbers: a release outranks any pre-release of itself.
        return (IsPreRelease, other.IsPreRelease) switch
        {
            (false, true) => 1,
            (true, false) => -1,
            _ => string.CompareOrdinal(PreRelease, other.PreRelease),
        };
    }

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;

    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;

    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;

    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (IsPreRelease ? $"-{PreRelease}" : string.Empty);
}
