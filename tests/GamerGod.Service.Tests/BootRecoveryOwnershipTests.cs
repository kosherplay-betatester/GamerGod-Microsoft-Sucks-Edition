using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Recovery;
using GamerGod.Windows;
using Xunit;

namespace GamerGod.Service.Tests;

/// <summary>
/// The production wiring of the fix for "restarting the service turns Game Mode off".
///
/// <para>
/// These run against the real <see cref="WindowsProcessLiveness"/> and a real file journal,
/// with this test process standing in as the session's owner — which is the only way to be
/// sure the pass, the liveness probe and the journal agree about what a live process looks
/// like. The single journalled change is a job-object confinement, whose revert is a no-op by
/// construction because the process that held the handle is gone, so nothing about this
/// machine is altered either way.
/// </para>
/// </summary>
public sealed class BootRecoveryOwnershipTests
{
    private static Task<string> JournalForAsync(
        string directory, int ownerProcessId, long ownerStartedAtUtcTicks) =>
        JournalForAsync(directory, "session.journal", "armed", ownerProcessId, ownerStartedAtUtcTicks);

    private static async Task<string> JournalForAsync(
        string directory,
        string fileName,
        string sessionId,
        int ownerProcessId,
        long ownerStartedAtUtcTicks)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var journal = new FileJournal(path);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = sessionId,
                MachineBootedAtUtcTicks = MachineBoot.At().UtcTicks,
                MachineUptimeMs = MachineBoot.UptimeMs(),
                OwnerProcessId = ownerProcessId,
                OwnerStartedAtUtcTicks = ownerStartedAtUtcTicks,
            },
            default);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Capture,
                SessionId = sessionId,
                Key = "confine:ambient-domain",
                MutationType = "GamerGod.Core.Engine.DomainConfinementMutation",
                Tier = MutationTier.ProcessDemotion,
                State = """{"Group":0,"Mask":4294901760,"ProcessCount":3}""",
            },
            default);

        return path;
    }

    private static LedgerRecoveryPass PassFor(params string[] paths) =>
        new(
            [.. paths],
            new WindowsAmbientOperations(),
            new WindowsTopologyProvider().Classify(),
            new WindowsProcessLiveness());

    [Fact]
    public async Task A_live_session_in_one_journal_is_reported_when_the_other_is_recovered()
    {
        // Production ALWAYS folds two outcomes — StateLayout.Journals() returns the session
        // journal and the bench journal — and every test here passed exactly one, so the
        // single-outcome shortcut in Combine was the only path any of them took. The
        // multi-outcome path dropped LeftToTheirOwner, and RecoveryOutcome.Explain() reads it in
        // both of its branches: with a live session retained on one journal and a clean revert on
        // the other, the event log said "Restored 1 change left behind by a session that ended
        // without cleaning up. This machine is as it was." while a session was still fully
        // applied. Explain()'s own doc calls that text the only record anybody will ever have of
        // what a LocalSystem service did at three in the morning.
        var directory = Path.Combine(Path.GetTempPath(), "GamerGod.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var self = await new WindowsProcessLiveness().IdentifyAsync(Environment.ProcessId, default);
            Assert.NotNull(self);

            // One this process still owns, and one whose owner is a start time that never was.
            var live = await JournalForAsync(
                directory, "session.journal", "live", self!.Value.ProcessId, self.Value.StartedAtUtcTicks);

            var orphaned = await JournalForAsync(
                directory, "bench.journal", "orphan",
                self.Value.ProcessId, self.Value.StartedAtUtcTicks - 10_000_000L);

            var outcome = await PassFor(live, orphaned).RunAsync(default);

            Assert.True(outcome.HadOutstandingChanges);
            Assert.True(outcome.IsClean, outcome.Explain());

            // The orphan was put back...
            Assert.Contains("confine:ambient-domain", outcome.Report!.Reverted);

            // ...and the live one is still named, rather than silently folded away.
            Assert.Contains("live", outcome.LeftToTheirOwner);

            // The sentence the event log actually gets must not claim the machine is as it was.
            Assert.DoesNotContain("This machine is as it was", outcome.Explain(), StringComparison.Ordinal);
            Assert.Contains("still running", outcome.Explain(), StringComparison.Ordinal);

            // And the live session's record survives, so nothing else concludes it is finished.
            Assert.True(await new MutationLedger(new FileJournal(live), new NoResolver())
                .HasOutstandingChangesAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_session_this_process_still_owns_survives_a_service_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GamerGod.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var self = await new WindowsProcessLiveness().IdentifyAsync(Environment.ProcessId, default);
            Assert.NotNull(self);

            var path = await JournalForAsync(directory, self!.Value.ProcessId, self.Value.StartedAtUtcTicks);

            var outcome = await PassFor(path).RunAsync(default);

            Assert.True(outcome.HadOutstandingChanges);
            Assert.True(outcome.IsClean, outcome.Explain());
            Assert.Null(outcome.Report);
            Assert.Contains("armed", outcome.LeftToTheirOwner);

            // And the journal still says so, so the session is not quietly forgotten either.
            Assert.True(await new MutationLedger(new FileJournal(path), new NoResolver())
                .HasOutstandingChangesAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_session_whose_owner_died_and_whose_id_was_reused_is_still_recovered()
    {
        // This process is running under that id, but it is not the process that armed the
        // session — a different start time is the whole of the difference. Without that check
        // a recycled id would keep a stranded machine stranded at every boot from now on.
        var directory = Path.Combine(Path.GetTempPath(), "GamerGod.Tests", Guid.NewGuid().ToString("N"));

        try
        {
            var self = await new WindowsProcessLiveness().IdentifyAsync(Environment.ProcessId, default);
            Assert.NotNull(self);

            var path = await JournalForAsync(
                directory, self!.Value.ProcessId, self.Value.StartedAtUtcTicks - 10_000_000L);

            var outcome = await PassFor(path).RunAsync(default);

            Assert.True(outcome.HadOutstandingChanges);
            Assert.True(outcome.IsClean, outcome.Explain());
            Assert.Empty(outcome.LeftToTheirOwner);
            Assert.Contains("confine:ambient-domain", outcome.Report!.Reverted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void The_pass_cannot_be_built_without_a_way_to_tell_whether_an_owner_is_alive()
    {
        // Defaulting to "no liveness source" here would silently restore the old behaviour of
        // reverting every outstanding journal at every start, and nothing would fail.
        Assert.Throws<ArgumentNullException>(() =>
            new LedgerRecoveryPass(
                [],
                new WindowsAmbientOperations(),
                new WindowsTopologyProvider().Classify(),
                null!));
    }

    /// <summary>Reads the journal without being able to rebuild anything from it.</summary>
    private sealed class NoResolver : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key) => null;
    }
}
