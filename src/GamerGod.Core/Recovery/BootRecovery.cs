using System.Collections.Immutable;
using GamerGod.Core.Ledger;

namespace GamerGod.Core.Recovery;

/// <summary>What a boot-recovery pass found, and what it did about it.</summary>
public sealed record RecoveryOutcome
{
    public required bool HadOutstandingChanges { get; init; }

    /// <summary>Null when there was nothing to revert, or when the pass could not run.</summary>
    public RevertReport? Report { get; init; }

    /// <summary>Set when the pass itself failed — an unreadable journal, a cancelled stop.</summary>
    public string? Error { get; init; }

    /// <summary>
    /// Sessions deliberately not touched, because the process that armed them is still
    /// running. Empty by construction rather than default: a default
    /// <see cref="ImmutableArray{T}"/> throws when a log line enumerates it, which is the
    /// least forgiving place for one.
    /// </summary>
    public ImmutableArray<string> LeftToTheirOwner { get; init; } = [];

    /// <summary>True when the machine is provably back to how the user left it.</summary>
    public bool IsClean => Error is null && (Report?.IsClean ?? true);

    /// <summary>
    /// Plain-language account, written for the Windows event log — which is the only record
    /// anybody will ever have of what a LocalSystem service did at three in the morning.
    /// Charter Article VII: it says what happened, never what it was worth.
    /// </summary>
    public string Explain()
    {
        if (Error is not null)
        {
            // Not "nothing was changed by this pass", which is what this said. A pass can fail
            // before it reads anything, and it can also fail after putting several changes back
            // — a journal write that fails mid-revert reaches here too. One sentence has to be
            // true of both, and claiming the machine is untouched when it is half-restored is
            // the wrong one to guess at in the only record of what happened.
            return $"GamerGod could not finish restoring this machine: {Error}. "
                + "Anything it had already put back stayed put back, and anything left is still "
                + "recorded in the journal. Restarting the machine restores it.";
        }

        if (!HadOutstandingChanges)
        {
            return "Nothing to restore. The journal records no changes still applied to this machine.";
        }

        if (Report is null)
        {
            // Everything outstanding belongs to a session whose program is still there. Said
            // plainly, because the alternative reading — "GamerGod started and did nothing" —
            // is the one somebody would reach for when their machine is still partitioned.
            return (LeftToTheirOwner.Length == 1
                    ? "A GamerGod session is still running, "
                    : $"{LeftToTheirOwner.Length} GamerGod sessions are still running, ")
                + "so nothing was restored. This pass only cleans up after a session whose "
                + "program is gone. Use 'gamergod off' or restart the machine to end one that "
                + "is still there.";
        }

        var report = Report;

        if (report.IsClean && LeftToTheirOwner.IsEmpty)
        {
            return $"Restored {Plural(report.Reverted.Length, "change")} left behind by a session "
                + "that ended without cleaning up. This machine is as it was.";
        }

        var parts = new List<string>
        {
            $"Restored {Plural(report.Reverted.Length, "change")} left behind by a session that "
            + "ended without cleaning up.",
        };

        if (!LeftToTheirOwner.IsEmpty)
        {
            parts.Add(LeftToTheirOwner.Length == 1
                ? "One session was left alone because the program holding it is still running."
                : $"{LeftToTheirOwner.Length} sessions were left alone because the programs "
                    + "holding them are still running.");
        }

        if (!report.Failed.IsEmpty)
        {
            parts.Add(
                $"{Plural(report.Failed.Length, "change")} could not be put back: "
                + string.Join("; ", report.Failed.Select(f => $"{f.Key}: {f.Error}")) + ".");
        }

        if (!report.Unresolvable.IsEmpty)
        {
            parts.Add(
                $"{Plural(report.Unresolvable.Length, "change")} was recorded by a version of "
                + "GamerGod this one does not recognise, and has been left in the journal: "
                + string.Join(", ", report.Unresolvable) + ".");
        }

        if (!report.IsClean)
        {
            parts.Add("The journal has been kept so the next start can try again. Restarting the "
                + "machine restores anything that is left.");
        }

        return string.Join(" ", parts);
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// The pass the background service runs when it starts.
///
/// <para>
/// This is Charter Article X's third escape path, and the only one that survives the machine
/// losing power: the hotkey and the controller combo both need a running agent, and a machine
/// that bugchecked mid-session has neither. What it has is a journal on disk and a service
/// set to start before anybody signs in.
/// </para>
///
/// <para>
/// Two rules, and the second is the one that is easy to get wrong. It must put the machine
/// back — and it must never throw. The service is registered with restart-on-failure, so an
/// exception escaping here is not a crash, it is a crash loop, on a LocalSystem service,
/// with no console for anyone to read.
/// </para>
///
/// <para>
/// What counts as outstanding is entirely the ledger's decision and is not second-guessed
/// here. A change tied to a process died with that process, so after a restart the ledger
/// already reports it as gone; a registry value did not, so the ledger still reports it. That
/// distinction is what stops recovery writing a captured affinity mask onto whichever process
/// happens to have inherited the id.
/// </para>
/// </summary>
public static class BootRecovery
{
    /// <summary>
    /// Recovers every orphaned session in a journal and leaves the live ones alone.
    ///
    /// <para>
    /// The liveness source is required rather than optional. Defaulting it would silently
    /// restore the behaviour this parameter exists to remove — reverting any outstanding
    /// journal at every start, including one somebody is playing on — and no test would fail.
    /// </para>
    /// </summary>
    public static async ValueTask<RecoveryOutcome> RunAsync(
        MutationLedger ledger, IProcessLiveness liveness, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(liveness);

        try
        {
            // Reading the journal, probing each recorded owner and reverting the orphans all
            // happen under one hold of the journal.
            //
            // These were three separate steps, and only the last one locked. A session that
            // began while the owners were being probed — the slow part, one process handle per
            // session — was missing from the retained list and present in the journal by the
            // time the revert read it, so the service ended a session whose user was watching.
            // That is the failure retained sessions exist to prevent, arriving through the gap
            // between deciding and acting.
            var pass = await ledger
                .RecoverOrphansAsync(
                    (session, token) => IsStillOwnedAsync(session, liveness, token),
                    cancellationToken)
                .ConfigureAwait(false);

            if (pass.Sessions.IsEmpty)
            {
                // Deliberately writes nothing. A service that starts at every boot must not grow
                // the journal by a line each time just to record that it had nothing to do.
                return new RecoveryOutcome { HadOutstandingChanges = false };
            }

            return new RecoveryOutcome
            {
                HadOutstandingChanges = true,

                // Null when every outstanding session still has its program, and left null: a
                // service restart during a long session must leave no trace at all.
                Report = pass.Report,
                LeftToTheirOwner = pass.Retained,
            };
        }
        catch (Exception ex)
        {
            // Includes cancellation, of a probe or of the revert. A stop arriving mid-pass
            // leaves the journal dirty on purpose, so the next start picks it up rather than the
            // record being lost.
            //
            // HadOutstandingChanges is false because this pass cannot claim to know: the failure
            // may have come before anything was read. Explain() words that case as "could not
            // check", which is the honest reading of both.
            return new RecoveryOutcome { HadOutstandingChanges = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Whether the exact process that armed a session is still running.
    ///
    /// <para>
    /// Every uncertain answer is "no". This pass acts by <em>leaving a session applied</em>,
    /// so a wrong "yes" strands the machine until the next reboot with nothing able to change
    /// it back — while a wrong "no" only reverts a session early, which is what this code did
    /// unconditionally before. The watchdog's rule is the mirror image for the mirror reason:
    /// it acts by reverting, so it must be sure.
    /// </para>
    /// </summary>
    private static async ValueTask<bool> IsStillOwnedAsync(
        OutstandingSession session, IProcessLiveness liveness, CancellationToken cancellationToken)
    {
        // No owner recorded. This is the ordinary case, not the broken one: `gamergod on`
        // applies and exits, and so does the desktop app. Neither stays to be an owner, and
        // the changes are meant to outlive them — that is the whole reason they are journalled
        // rather than held in a process.
        //
        // Reading "no owner" as "orphaned" was wrong, and only a real machine showed it: arm
        // with `gamergod on`, restart the service, and Game Mode came back off. Every session
        // on the machine died the moment the service restarted — the defect this file exists
        // to remove, wearing a justification.
        //
        // What separates the two ownerless cases is whether the apply finished. A session that
        // completed left the machine exactly where the user asked for it, and within the same
        // boot it is theirs to end with `gamergod off`. A session interrupted part way through
        // left some changes applied and others not, which is a state nobody chose, and that is
        // precisely what boot recovery is for.
        //
        // Across a restart neither survives on its own terms: the boot check has already
        // dropped whatever died with its process, and anything boot-persistent that is left
        // has nobody to undo it, so it is recovered.
        if (session.Owner is not { } owner)
        {
            return session.ApplyCompleted && !session.MachineRestartedSince;
        }

        // A restart ended every process on this machine, so the recorded owner is gone
        // whatever a probe would say now — whoever holds that id today is somebody else.
        // Checked before the probe rather than after, because a chance match on a recycled id
        // would leave a boot-persistent change applied with no owner alive to ever undo it.
        if (session.MachineRestartedSince)
        {
            return false;
        }

        try
        {
            var current = await liveness
                .IdentifyAsync(owner.ProcessId, cancellationToken)
                .ConfigureAwait(false);

            // The start time is what makes this an identity rather than a number. A different
            // one on the same id means the original process is gone and something else
            // inherited it — otherwise a recycled id would protect a stranded session for ever.
            return current == owner;
        }
        catch (OperationCanceledException)
        {
            // A service stop mid-pass. Reverting a live session because Windows was shutting
            // down would be the same surprise by another route, so this aborts the pass.
            throw;
        }
        catch (Exception)
        {
            // "I cannot tell" is not "still running".
            return false;
        }
    }
}
