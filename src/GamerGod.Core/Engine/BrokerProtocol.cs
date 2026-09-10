using System.Globalization;

namespace GamerGod.Core.Engine;

/// <summary>
/// The two sentences the desktop application and its elevated helper are allowed to exchange.
///
/// <para>
/// <b>Why this exists.</b> The window runs with no special rights and the journal it has to
/// write lives where only administrators may, so every arm and every disarm shelled out to
/// <c>gamergod.exe</c> with <c>runas</c> — and each one of those is a Windows consent prompt.
/// Turn Game Mode on: a prompt. Off again: another. Arm on launch: a prompt before the window is
/// even usable. People stop reading prompts that appear that often, which makes the prompt worth
/// less exactly where it matters.
/// </para>
///
/// <para>
/// So consent is asked for once, and what it starts is a helper that stays for as long as the
/// application is open.
/// </para>
///
/// <para>
/// <b>What keeps that honest.</b> The helper is not a shell. It accepts the verbs below and
/// nothing else — no path, no executable name, no arbitrary argument list — so the worst a
/// caller can ask for is the thing the button already does, which is journalled and reversible.
/// Its pipe is restricted to the single account that launched it. And it exits when the window
/// does, so the elevated process does not outlive the reason it was created.
/// </para>
///
/// <para>
/// Deliberately line-based text rather than serialised objects: this crosses a privilege
/// boundary, and a format a person can read in full is one a reviewer can check in full.
/// </para>
/// </summary>
public static class BrokerProtocol
{
    /// <summary>Turn Game Mode on. Followed by the lever arguments, space separated.</summary>
    public const string On = "on";

    /// <summary>Turn Game Mode off.</summary>
    public const string Off = "off";

    /// <summary>Is anything applied? Answered with <see cref="Ok"/> and an exit code.</summary>
    public const string Status = "status";

    /// <summary>Confirms the helper is alive and elevated, without changing anything.</summary>
    public const string Ping = "ping";

    /// <summary>Asks the helper to stop. Sent when the application closes.</summary>
    public const string Quit = "quit";

    /// <summary>Prefix of a reply carrying the verb's exit code.</summary>
    public const string Ok = "ok ";

    /// <summary>Prefix of a reply carrying a reason the verb could not run at all.</summary>
    public const string Error = "error ";

    /// <summary>
    /// The verbs a helper will act on. A request that is not exactly one of these is refused
    /// without being parsed further — the allow-list is the security boundary, so it is a
    /// literal list rather than a pattern.
    /// </summary>
    public static bool IsAllowedVerb(string? verb) =>
        verb is On or Off or Status or Ping or Quit;

    /// <summary>Formats a reply carrying an exit code.</summary>
    public static string Reply(int exitCode) =>
        Ok + exitCode.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a reply. Returns null when it is not an <see cref="Ok"/> line, which the caller
    /// treats as a failure rather than as a zero.
    /// </summary>
    public static int? ReadExitCode(string? reply)
    {
        if (reply is null || !reply.StartsWith(Ok, StringComparison.Ordinal))
        {
            return null;
        }

        return int.TryParse(
            reply.AsSpan(Ok.Length).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var code)
            ? code
            : null;
    }

    /// <summary>
    /// Splits a request into its verb and the arguments after it.
    ///
    /// <para>
    /// Whitespace only. Nothing here unescapes or unquotes, because nothing on the other side
    /// needs it: the only arguments that ever travel are the lever flags, which are fixed
    /// strings from <see cref="LeverArguments"/> and a numeric process id.
    /// </para>
    /// </summary>
    public static (string Verb, string[] Arguments) Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0
            ? (string.Empty, [])
            : (parts[0].ToLowerInvariant(), parts[1..]);
    }
}
