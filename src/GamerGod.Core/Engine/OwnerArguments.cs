using System.Globalization;

namespace GamerGod.Core.Engine;

/// <summary>
/// Reads and writes <c>--owner &lt;pid&gt;</c>, the process whose death ends a session.
///
/// <para>
/// Here rather than in the command line for the same reason <see cref="LeverArguments"/> is: the
/// desktop app renders this argument and the command line parses it, they are separate processes,
/// and a setting that is spelled one way by the sender and another by the receiver is silently
/// dropped. One type owns the spelling and both sides use it.
/// </para>
///
/// <para>
/// Only the pid crosses the boundary. The start time that turns it into an identity is read from
/// the live process by the receiver, which is the only place it can be read honestly — a caller
/// that supplied both could name a pid and a start time that never belonged together, and the
/// watchdog would then hold a session open against a process that does not exist.
/// </para>
/// </summary>
public static class OwnerArguments
{
    public const string Flag = "--owner";

    /// <summary>
    /// The requested owner pid, or null when the arguments name none.
    ///
    /// <para>
    /// A malformed or non-positive value reads as null rather than throwing. The flag is an
    /// optimisation of when a session ends, never a precondition for arming one, and refusing to
    /// turn Game Mode on because a pid was mistyped would trade a working feature for a
    /// diagnostic.
    /// </para>
    /// </summary>
    public static int? Find(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (!string.Equals(arguments[i], Flag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return int.TryParse(
                arguments[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                && pid > 0
                ? pid
                : null;
        }

        return null;
    }

    /// <summary>Renders the flag, or nothing at all when there is no owner to name.</summary>
    public static string[] Render(int? ownerProcessId) =>
        ownerProcessId is { } pid && pid > 0
            ? [Flag, pid.ToString(CultureInfo.InvariantCulture)]
            : [];

    /// <summary>
    /// <c>--owner-exe &lt;name&gt;</c>: a program to wait for after arming, and then hand the
    /// session to.
    ///
    /// <para>
    /// The flag exists because a pid cannot always be known in advance. A game started through a
    /// <c>steam://</c> URI is launched by Steam, not by GamerGod, so nothing GamerGod calls
    /// returns its id — and the machine must be quiet before the game starts, so arming cannot
    /// wait for it either. A name is the only handle available at the moment of arming.
    /// </para>
    /// </summary>
    public const string ExecutableFlag = "--owner-exe";

    /// <summary>The program to wait for, or null when none was named.</summary>
    public static string? FindExecutable(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (var i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], ExecutableFlag, StringComparison.OrdinalIgnoreCase))
            {
                var value = arguments[i + 1];
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }

        return null;
    }

    /// <summary>Renders the executable flag, or nothing when there is no program to wait for.</summary>
    public static string[] RenderExecutable(string? executable) =>
        string.IsNullOrWhiteSpace(executable) ? [] : [ExecutableFlag, executable];
}
