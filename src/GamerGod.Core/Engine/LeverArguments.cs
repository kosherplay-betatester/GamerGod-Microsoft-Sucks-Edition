namespace GamerGod.Core.Engine;

/// <summary>
/// Carries the four levers across the process boundary between the app and the command line.
///
/// <para>
/// The desktop application cannot write the revert journal — that directory is
/// administrators-only, deliberately, because the journal is the restore guarantee. So it
/// brokers arming through <c>gamergod on</c>, and for a while it did that with no arguments at
/// all: the user's settings were read, listed back to them in a confirmation dialog, and then
/// thrown away as the command applied its own defaults. Unticking confinement still confined;
/// ticking service suppression stopped nothing.
/// </para>
///
/// <para>
/// Every flag is explicit in both directions. There is no "unset means the default", because
/// the whole failure was a default quietly standing in for a choice — a caller that means
/// "off" has to say "off".
/// </para>
/// </summary>
public static class LeverArguments
{
    private const string Confine = "--confine";
    private const string Efficiency = "--efficiency";
    private const string Power = "--power";
    private const string Services = "--services";

    /// <summary>
    /// Reads the levers out of a command line, falling back to the shipped defaults for any
    /// flag that was not given — which is what a person typing <c>gamergod on</c> expects.
    /// </summary>
    public static AmbientOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var defaults = new AmbientOptions();

        return new AmbientOptions
        {
            ConfineToAmbientDomain = Read(args, Confine, defaults.ConfineToAmbientDomain),
            DemoteToEfficiencyMode = Read(args, Efficiency, defaults.DemoteToEfficiencyMode),
            ManagePowerScheme = Read(args, Power, defaults.ManagePowerScheme),

            Services = Read(args, Services, !defaults.Services.IsDefaultOrEmpty)
                ? ["WSearch", "SysMain", "DiagTrack", "wuauserv", "BITS"]
                : [],
        };
    }

    /// <summary>
    /// Renders a set of levers as arguments. Used by the app so the two sides cannot disagree
    /// about the spelling — a mismatch here would silently drop a setting again.
    /// </summary>
    public static string[] Render(AmbientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            Flag(Confine, options.ConfineToAmbientDomain),
            Flag(Efficiency, options.DemoteToEfficiencyMode),
            Flag(Power, options.ManagePowerScheme),
            Flag(Services, !options.Services.IsDefaultOrEmpty),
        ];
    }

    /// <summary>
    /// <c>--confine</c> or <c>--no-confine</c>. Both spellings exist so a caller states its
    /// intent either way rather than relying on absence to mean anything.
    /// </summary>
    private static string Flag(string name, bool on) =>
        on ? name : "--no-" + name[2..];

    private static bool Read(string[] args, string name, bool fallback)
    {
        var negative = "--no-" + name[2..];

        foreach (var arg in args)
        {
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (arg.Equals(negative, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return fallback;
    }
}
