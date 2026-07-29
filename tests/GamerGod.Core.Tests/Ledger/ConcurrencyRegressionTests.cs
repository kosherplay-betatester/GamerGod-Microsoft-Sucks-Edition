using System.Text.Json;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using Xunit;

namespace GamerGod.Core.Tests.Ledger;

/// <summary>
/// Regression for the most serious defect an adversarial audit found: a revert overlapping an
/// in-flight apply silently destroyed the record of the change.
///
/// <para>
/// The sequence. <c>ApplyAsync</c> journals its capture, then parks inside
/// <c>mutation.ApplyAsync</c> — the window between "we have recorded what we are about to
/// change" and "we have changed it". A revert fires in that window, sees the capture, undoes
/// nothing (there is nothing to undo yet), writes a <c>Reverted</c> line that permanently
/// cancels the capture, and reports <c>IsClean</c>. The apply then completes and changes the
/// machine, with no surviving record. The journal says clean, the machine is modified, and
/// boot recovery finds nothing to do.
/// </para>
///
/// <para>
/// That is Charter Article VI broken while reporting success, which is worse than breaking it
/// loudly. The window is real for every one of the five recovery triggers — a panic hotkey, a
/// watchdog timeout, the game exiting, a logoff — all of which can fire during enter.
/// </para>
/// </summary>
public sealed class LedgerConcurrencyRegressionTests
{
    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate(
            "Any Title",
            new AntiCheatAssessment { Tier = AntiCheatTier.Kernel, Findings = [] });

    [Fact]
    public async Task A_revert_cannot_overlap_an_in_flight_apply()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        var applyReachedTheChange = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var mutation = new BlockingMutation(
            machine, "service:WSearch", "Stopped", applyReachedTheChange, releaseApply.Task);
        resolver.Remember(mutation);

        // Two ledgers over one journal: the shape that actually occurs, with the service
        // applying while the session agent reverts.
        var applying = new MutationLedger(journal, resolver)
            .ApplyAsync("s1", [mutation], AmbientOnly())
            .AsTask();

        await applyReachedTheChange.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The apply is now parked between its capture and its change. Fire the revert.
        var reverting = new MutationLedger(journal, resolver).RevertAsync().AsTask();

        // The revert must not be able to complete while the apply holds the journal.
        var finishedEarly = await Task.WhenAny(reverting, Task.Delay(300));
        Assert.NotSame(reverting, finishedEarly);

        releaseApply.SetResult();
        await applying;

        var report = await reverting;

        // The change is genuinely undone, and the journal agrees.
        Assert.True(report.IsClean);
        Assert.Contains("service:WSearch", report.Reverted);
        Assert.True(machine.Matches(before), $"machine left as: {machine.Describe()}");
        Assert.False(await new MutationLedger(journal, resolver).HasOutstandingChangesAsync());
    }

    [Fact]
    public async Task Two_concurrent_applies_over_one_journal_do_not_interleave()
    {
        var machine = new FakeMachine();
        machine["a"] = "original-a";
        machine["b"] = "original-b";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        var first = new FakeMutation(machine, "a", MutationTier.Service, "changed-a");
        var second = new FakeMutation(machine, "b", MutationTier.Service, "changed-b");
        resolver.Remember(first);
        resolver.Remember(second);

        await Task.WhenAll(
            new MutationLedger(journal, resolver).ApplyAsync("s1", [first], AmbientOnly()).AsTask(),
            new MutationLedger(journal, resolver).ApplyAsync("s2", [second], AmbientOnly()).AsTask());

        // Both captures survive, so both changes are recoverable.
        var captures = journal.Entries.Where(e => e.Op == JournalOp.Capture).Select(e => e.Key).ToArray();
        Assert.Contains("a", captures);
        Assert.Contains("b", captures);

        var report = await new MutationLedger(journal, resolver).RevertAsync();

        Assert.True(report.IsClean);
        Assert.True(machine.Matches(before), $"machine left as: {machine.Describe()}");
    }

    [Fact]
    public async Task A_file_journal_serialises_across_ledger_instances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gamergod-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var journal = new FileJournal(Path.Combine(directory, "session.journal"));
            var machine = new FakeMachine();
            machine["k"] = "original";
            var before = machine.Snapshot();
            var resolver = new FakeResolver(machine);

            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var mutation = new BlockingMutation(machine, "k", "changed", reached, release.Task);
            resolver.Remember(mutation);

            var applying = new MutationLedger(journal, resolver)
                .ApplyAsync("s1", [mutation], AmbientOnly())
                .AsTask();

            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var reverting = new MutationLedger(journal, resolver).RevertAsync().AsTask();
            var finishedEarly = await Task.WhenAny(reverting, Task.Delay(300));
            Assert.NotSame(reverting, finishedEarly);

            release.SetResult();
            await applying;

            Assert.True((await reverting).IsClean);
            Assert.True(machine.Matches(before));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Parks inside <c>ApplyAsync</c> so a test can occupy the window between the capture
    /// being journalled and the machine actually changing.
    /// </summary>
    private sealed class BlockingMutation(
        FakeMachine machine,
        string key,
        string newValue,
        TaskCompletionSource reached,
        Task release) : IMutation
    {
        public string Key => key;

        public MutationTier Tier => MutationTier.Service;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => false;

        public string Describe() => $"set {key} to {newValue}";

        public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new Capture(machine[key])));

        public async ValueTask ApplyAsync(CancellationToken cancellationToken)
        {
            reached.TrySetResult();
            await release.ConfigureAwait(false);
            machine[key] = newValue;
        }

        public ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
        {
            machine[key] = capture.Deserialize<Capture>()!.Previous;
            return ValueTask.CompletedTask;
        }

        private sealed record Capture(string Previous);
    }
}
