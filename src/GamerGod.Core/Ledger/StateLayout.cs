using System.Collections.Immutable;

namespace GamerGod.Core.Ledger;

/// <summary>
/// Where GamerGod keeps the files that outlive a process.
///
/// <para>
/// Three separate programs read and write this directory — the command line, the desktop app
/// and the LocalSystem service — and they are separate processes that never speak to each
/// other except through these files. A disagreement about the path is not a broken build, it
/// is a service that recovers a journal nobody writes while the command line writes one
/// nobody recovers, with both reporting success.
/// </para>
///
/// <para>
/// It lives in <c>ProgramData</c> rather than a user profile because the service runs before
/// anybody signs in. The installer restricts write access to Administrators and SYSTEM: an
/// unprivileged user who could write here could hand a LocalSystem process instructions.
/// </para>
/// </summary>
public static class StateLayout
{
    public static string StateRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GamerGod");

    public static string StateDirectory { get; } = Path.Combine(StateRoot, "state");

    /// <summary>What <c>gamergod on</c> writes, and what the service recovers at boot.</summary>
    public static string SessionJournal { get; } = Path.Combine(StateDirectory, "session.journal");

    /// <summary>
    /// Kept separate from the session journal so an A/B run, which deliberately applies and
    /// reverts many times, cannot interleave its record with a user's live session.
    /// </summary>
    public static string BenchJournal { get; } = Path.Combine(StateDirectory, "bench.journal");

    /// <summary>
    /// Every journal a recovery pass must consider. A measurement run that was interrupted
    /// leaves exactly the same kind of dirty journal a session does, and forgetting it would
    /// strand a machine for a reason nobody would ever guess.
    /// </summary>
    public static ImmutableArray<string> Journals() => [SessionJournal, BenchJournal];
}
