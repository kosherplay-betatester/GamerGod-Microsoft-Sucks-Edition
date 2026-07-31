using System.Diagnostics;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Windows;
using Xunit;

namespace GamerGod.Service.Tests;

/// <summary>
/// The native half of processor-group affinity, and the on-disk half of boot recovery.
///
/// <para>
/// This is the only test project that can reach <c>GamerGod.Windows</c>, because
/// <c>GamerGod.Core.Tests</c> deliberately cannot — that separation is what lets the ledger's
/// chaos tests run against a simulated machine. Everything here that touches the real
/// operating system is tagged <c>Host=VM</c> and excluded from the default run.
/// </para>
/// </summary>
public sealed class GroupAffinityTests
{
    [Fact]
    [Trait("Host", "VM")]
    public async Task Reading_an_affinity_reports_the_group_the_process_actually_runs_in()
    {
        // The defect this covers hard-coded group zero. On a machine with more than 64
        // logical processors that records a restore point for the wrong processors, and the
        // reference machine has 32 — so nothing short of this assertion would ever notice.
        var os = new WindowsAmbientOperations();

        var mask = await os.GetAffinityAsync(Environment.ProcessId, default);

        using var self = Process.GetCurrentProcess();

        Assert.False(mask.IsEmpty);
        Assert.Equal((ulong)(nint)self.ProcessorAffinity, mask.Mask);
    }

    [Fact]
    [Trait("Host", "VM")]
    public async Task A_mask_addressed_at_another_processor_group_is_refused_not_reinterpreted()
    {
        // Windows would apply this mask relative to whichever group the process is already
        // in, which is exactly the silent wrong answer. Refusing is the only honest option:
        // the alternative moves a process to cores nobody chose.
        var os = new WindowsAmbientOperations();
        var current = await os.GetAffinityAsync(Environment.ProcessId, default);
        var elsewhere = current with { Group = (ushort)(current.Group + 1) };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await os.SetAffinityAsync(Environment.ProcessId, elsewhere, default));

        Assert.Equal(current, await os.GetAffinityAsync(Environment.ProcessId, default));
    }

    [Fact]
    [Trait("Host", "VM")]
    public async Task An_affinity_write_round_trips_through_its_own_group()
    {
        // Deliberately writes back the value that is already there. Revert-exactly is the
        // property under test, not the ability to move this process somewhere new.
        var os = new WindowsAmbientOperations();
        var before = await os.GetAffinityAsync(Environment.ProcessId, default);

        await os.SetAffinityAsync(Environment.ProcessId, before, default);

        Assert.Equal(before, await os.GetAffinityAsync(Environment.ProcessId, default));
    }

    [Fact]
    public async Task A_journal_left_on_disk_is_recovered_by_the_real_wiring()
    {
        // The production path end to end: a file journal, the canonical resolver, the ledger
        // and the boot recovery pass. The one entry is a job-object confinement, whose revert
        // is a no-op by construction because the process that held the handle is gone — so
        // this exercises every layer without changing anything about this machine.
        var directory = Path.Combine(Path.GetTempPath(), "GamerGod.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var path = Path.Combine(directory, "session.journal");
            var journal = new FileJournal(path);

            await journal.AppendAsync(
                new JournalEntry
                {
                    Op = JournalOp.SessionBegin,
                    SessionId = "crashed",
                    MachineBootedAtUtcTicks = MachineBoot.At().UtcTicks,
                },
                default);

            await journal.AppendAsync(
                new JournalEntry
                {
                    Op = JournalOp.Capture,
                    SessionId = "crashed",
                    Key = "confine:ambient-domain",
                    MutationType = "GamerGod.Core.Engine.DomainConfinementMutation",
                    Tier = MutationTier.ProcessDemotion,
                    State = """{"Group":0,"Mask":4294901760,"ProcessCount":3}""",
                },
                default);

            var pass = new LedgerRecoveryPass(
                [path],
                new WindowsAmbientOperations(),
                new WindowsTopologyProvider().Classify());

            var outcome = await pass.RunAsync(default);

            Assert.True(outcome.HadOutstandingChanges);
            Assert.True(outcome.IsClean, outcome.Explain());

            // And the journal now says so, so the next boot does nothing.
            var again = await pass.RunAsync(default);
            Assert.False(again.HadOutstandingChanges);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_state_directory_that_does_not_exist_is_not_an_error()
    {
        // First boot after an install, or a machine where GamerGod has never been turned on.
        // The service starts before anybody signs in and must not log a failure for it.
        var pass = new LedgerRecoveryPass(
            [Path.Combine(Path.GetTempPath(), "GamerGod.Tests", Guid.NewGuid().ToString("N"), "none.journal")],
            new WindowsAmbientOperations(),
            new WindowsTopologyProvider().Classify());

        var outcome = await pass.RunAsync(default);

        Assert.False(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
    }

    [Fact]
    public void The_service_and_the_command_line_agree_on_where_the_journal_lives()
    {
        // They are separate processes reading one file. A disagreement here means the
        // service recovers a journal nobody writes and the command line writes one nobody
        // recovers, and both report success.
        Assert.Contains("GamerGod", StateLayout.StateRoot, StringComparison.Ordinal);
        Assert.Contains(StateLayout.SessionJournal, StateLayout.Journals());
        Assert.All(StateLayout.Journals(), p => Assert.EndsWith(".journal", p, StringComparison.Ordinal));
    }
}
