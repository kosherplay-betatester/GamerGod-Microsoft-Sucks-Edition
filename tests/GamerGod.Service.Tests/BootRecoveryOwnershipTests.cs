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
    private static async Task<string> JournalForAsync(
        string directory, int ownerProcessId, long ownerStartedAtUtcTicks)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.journal");
        var journal = new FileJournal(path);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "armed",
                MachineBootedAtUtcTicks = MachineBoot.At().UtcTicks,
                OwnerProcessId = ownerProcessId,
                OwnerStartedAtUtcTicks = ownerStartedAtUtcTicks,
            },
            default);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Capture,
                SessionId = "armed",
                Key = "confine:ambient-domain",
                MutationType = "GamerGod.Core.Engine.DomainConfinementMutation",
                Tier = MutationTier.ProcessDemotion,
                State = """{"Group":0,"Mask":4294901760,"ProcessCount":3}""",
            },
            default);

        return path;
    }

    private static LedgerRecoveryPass PassFor(string path) =>
        new(
            [path],
            new WindowsAmbientOperations(),
            new WindowsTopologyProvider().Classify(),
            new WindowsProcessLiveness());

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
