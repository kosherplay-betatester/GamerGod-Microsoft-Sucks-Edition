using System.Collections.Immutable;
using System.Runtime.Versioning;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Recovery;
using GamerGod.Windows;

namespace GamerGod.Service;

/// <summary>
/// Runs boot recovery over every journal GamerGod writes.
///
/// <para>
/// More than one, because an interrupted measurement run leaves exactly the same kind of
/// dirty journal a session does. Recovering only the session journal would strand a machine
/// for a reason nobody would ever think to look for.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LedgerRecoveryPass : IBootRecoveryPass
{
    private readonly ImmutableArray<string> _journalPaths;
    private readonly IAmbientOperations _operations;
    private readonly CpuTopology _topology;

    public LedgerRecoveryPass(
        ImmutableArray<string> journalPaths, IAmbientOperations operations, CpuTopology topology)
    {
        _journalPaths = journalPaths.IsDefault ? [] : journalPaths;
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    }

    /// <summary>
    /// The pass this machine needs, built from what the machine reports about itself.
    ///
    /// <para>
    /// Never throws. A hosted service that cannot be constructed stops the whole host from
    /// starting, and a service that will not start is one Windows restarts forever.
    /// </para>
    /// </summary>
    public static IBootRecoveryPass ForThisMachine()
    {
        try
        {
            return new LedgerRecoveryPass(
                StateLayout.Journals(),
                new WindowsAmbientOperations(),
                new WindowsTopologyProvider().Classify());
        }
        catch (Exception ex)
        {
            return new UnavailablePass(ex.Message);
        }
    }

    public async ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<RecoveryOutcome>(_journalPaths.Length);

        foreach (var path in _journalPaths)
        {
            var ledger = new MutationLedger(
                new FileJournal(path), new AmbientMutationResolver(_operations, _topology));

            outcomes.Add(await BootRecovery.RunAsync(ledger, cancellationToken).ConfigureAwait(false));
        }

        return Combine(outcomes);
    }

    /// <summary>
    /// Folds one outcome per journal into the single answer the event log records. A failure
    /// on one journal must not hide a success on another, or the other way round.
    /// </summary>
    private static RecoveryOutcome Combine(List<RecoveryOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return new RecoveryOutcome { HadOutstandingChanges = false };
        }

        if (outcomes.Count == 1)
        {
            return outcomes[0];
        }

        var errors = outcomes.Select(o => o.Error).OfType<string>().ToArray();
        var reports = outcomes.Select(o => o.Report).OfType<RevertReport>().ToArray();

        return new RecoveryOutcome
        {
            HadOutstandingChanges = outcomes.Exists(o => o.HadOutstandingChanges),
            Error = errors.Length == 0 ? null : string.Join("; ", errors),
            Report = reports.Length == 0
                ? null
                : new RevertReport
                {
                    Reverted = [.. reports.SelectMany(r => r.Reverted)],
                    Failed = [.. reports.SelectMany(r => r.Failed)],
                    Unresolvable = [.. reports.SelectMany(r => r.Unresolvable)],
                },
        };
    }

    /// <summary>
    /// Stands in when this machine could not be read at all. It reports the reason rather
    /// than pretending there was nothing to do, which would be the one lie that matters here.
    /// </summary>
    private sealed class UnavailablePass(string reason) : IBootRecoveryPass
    {
        public ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RecoveryOutcome { HadOutstandingChanges = false, Error = reason });
    }
}
