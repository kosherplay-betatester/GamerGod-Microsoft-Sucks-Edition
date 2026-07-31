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
            return $"GamerGod could not check for changes to restore: {Error}. "
                + "Nothing was changed by this pass. Restarting the machine still restores it.";
        }

        if (!HadOutstandingChanges)
        {
            return "Nothing to restore. The journal records no changes still applied to this machine.";
        }

        var report = Report!;

        if (report.IsClean)
        {
            return $"Restored {Plural(report.Reverted.Length, "change")} left behind by a session "
                + "that ended without cleaning up. This machine is as it was.";
        }

        var parts = new List<string>
        {
            $"Restored {Plural(report.Reverted.Length, "change")} left behind by a session that "
            + "ended without cleaning up.",
        };

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

        parts.Add("The journal has been kept so the next start can try again. Restarting the "
            + "machine restores anything that is left.");

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
    public static async ValueTask<RecoveryOutcome> RunAsync(
        MutationLedger ledger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);

        bool outstanding;

        try
        {
            outstanding = await ledger.HasOutstandingChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new RecoveryOutcome { HadOutstandingChanges = false, Error = ex.Message };
        }

        if (!outstanding)
        {
            // Deliberately writes nothing. A service that starts at every boot must not grow
            // the journal by a line each time just to record that it had nothing to do.
            return new RecoveryOutcome { HadOutstandingChanges = false };
        }

        try
        {
            var report = await ledger.RevertAsync(cancellationToken).ConfigureAwait(false);
            return new RecoveryOutcome { HadOutstandingChanges = true, Report = report };
        }
        catch (Exception ex)
        {
            // Includes cancellation. A stop arriving mid-pass leaves the journal dirty on
            // purpose, so the next start picks it up rather than the record being lost.
            return new RecoveryOutcome { HadOutstandingChanges = true, Error = ex.Message };
        }
    }
}
