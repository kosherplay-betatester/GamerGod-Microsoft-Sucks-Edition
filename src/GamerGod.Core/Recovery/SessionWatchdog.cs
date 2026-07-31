using GamerGod.Core.Ledger;

namespace GamerGod.Core.Recovery;

/// <summary>
/// A running process, identified in a way a recycled process id cannot forge.
///
/// <para>
/// Windows reuses process ids aggressively, so a watchdog that trusts the number alone can
/// end up guarding an unrelated program that inherited it — and never fire for the session it
/// was armed for. The start time is what makes the identity stick.
/// </para>
/// </summary>
public readonly record struct ProcessIdentity(int ProcessId, long StartedAtUtcTicks);

/// <summary>
/// Whether the process that armed a session is still the process that armed it.
///
/// <para>
/// The seam that keeps this file free of P/Invoke. The implementation opens a handle with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and nothing else, and lives in
/// <c>GamerGod.Windows</c> where a reviewer can find it.
/// </para>
/// </summary>
public interface IProcessLiveness
{
    /// <summary>
    /// The process's current identity, or null when no process with that id is running.
    ///
    /// <para>
    /// Throws when the answer cannot be determined. That distinction matters: the watchdog
    /// treats null as an exit and acts on it, and treats a thrown answer as "ask again",
    /// because ending a live session on a bad answer from the operating system is worse than
    /// waiting for a better one.
    /// </para>
    /// </summary>
    ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken);
}

/// <summary>How a watch ended.</summary>
public enum WatchdogOutcome
{
    /// <summary>The process that armed the session is gone, and the session was reverted.</summary>
    OwnerExited,

    /// <summary>The service is stopping. Nothing was reverted.</summary>
    Cancelled,
}

/// <summary>What the watchdog observed, and what it did about it.</summary>
public sealed record WatchdogReport
{
    public required WatchdogOutcome Outcome { get; init; }

    public RevertReport? Revert { get; init; }

    public string? RevertError { get; init; }

    public bool IsClean => RevertError is null && (Revert?.IsClean ?? true);

    public string Explain() => Outcome switch
    {
        WatchdogOutcome.Cancelled =>
            "Stopped watching. The session is still applied, and 'gamergod off' or a restart "
            + "will undo it.",

        _ when RevertError is not null =>
            "The program holding this session is gone, and GamerGod could not put everything "
            + $"back: {RevertError}. Restarting the machine restores what is left.",

        _ when Revert is { IsClean: true } clean =>
            $"The program holding this session is gone. {Plural(clean.Reverted.Length, "change")} "
            + "put back; this machine is as it was.",

        _ => "The program holding this session is gone, and some changes could not be put back. "
            + "Restarting the machine restores them.",
    };

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// Reverts a session when the process that armed it stops existing.
///
/// <para>
/// Charter Article X promises four independent ways back to Windows. Two of them — the
/// hotkey and the controller combo — need the agent to still be working. This one does not:
/// it lives in a separate process, at a separate privilege level, and does its job precisely
/// when the arming process can no longer do anything at all.
/// </para>
///
/// <para>
/// It is asymmetric on purpose. Firing while the owner is alive ends somebody's session
/// mid-match for no reason; failing to fire strands the machine until the next reboot. Only
/// the second is a broken promise, but the first is the one that loses trust, so anything
/// short of a definite answer means keep waiting.
/// </para>
/// </summary>
public sealed class SessionWatchdog
{
    private readonly MutationLedger _ledger;
    private readonly IProcessLiveness _liveness;
    private readonly TimeSpan _pollInterval;

    public SessionWatchdog(MutationLedger ledger, IProcessLiveness liveness, TimeSpan pollInterval)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _liveness = liveness ?? throw new ArgumentNullException(nameof(liveness));

        // A zero interval turns the safety net into a spin loop on a core the user wanted for
        // their game, which would make the watchdog the contention it exists to remove.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// Watches until the owner is gone or the caller stops. Returns rather than looping after
    /// it fires, so a session can be reverted once and the caller decides what happens next.
    /// </summary>
    public async ValueTask<WatchdogReport> WatchAsync(
        ProcessIdentity owner, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WatchdogReport { Outcome = WatchdogOutcome.Cancelled };
            }

            ProcessIdentity? current;

            try
            {
                current = await _liveness
                    .IdentifyAsync(owner.ProcessId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new WatchdogReport { Outcome = WatchdogOutcome.Cancelled };
            }
            catch (Exception)
            {
                // "I cannot tell" is not "it exited". A transient refusal from the operating
                // system must never be the reason somebody's session ends.
                continue;
            }

            // A different start time on the same id means the original process is gone and
            // something else inherited its number.
            if (current == owner)
            {
                continue;
            }

            return await RevertNowAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<WatchdogReport> RevertNowAsync()
    {
        try
        {
            // Deliberately not the caller's token. Once the decision to restore has been
            // taken, a service stop arriving halfway through would leave the journal saying
            // reverted for some keys while the machine still holds the rest - the one state
            // this design exists to make impossible.
            var report = await _ledger.RevertAsync(CancellationToken.None).ConfigureAwait(false);

            return new WatchdogReport { Outcome = WatchdogOutcome.OwnerExited, Revert = report };
        }
        catch (Exception ex)
        {
            // The journal is left dirty on purpose, so the next start tries again.
            return new WatchdogReport { Outcome = WatchdogOutcome.OwnerExited, RevertError = ex.Message };
        }
    }
}
