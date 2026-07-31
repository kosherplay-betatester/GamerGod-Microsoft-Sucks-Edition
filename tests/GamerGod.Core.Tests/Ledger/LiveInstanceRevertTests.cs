using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Tests.Engine;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Ledger;

/// <summary>
/// Reverting through the object that applied, when this process is still the one that applied.
///
/// <para>
/// The ledger used to rebuild every mutation from the journal on the way out, including in the
/// process that had just applied it. That is right for the cold case and wrong for the warm
/// one: a job object's affinity limit exists only while a handle to it is open, and the handle
/// belongs to the <c>DomainConfinementMutation</c> instance that opened it. A rebuilt copy
/// holds nothing, closes nothing, and returns success — so the confinement stayed in force
/// with the journal reporting it lifted. Latent while only the command line applies, because
/// it uses affinity instead and exits anyway; live the moment the resident service is the
/// applier, which is the only caller that can use a job object at all.
/// </para>
///
/// <para>
/// The journal stays the source of truth for the cold path. Nothing here may make a crash
/// less recoverable, which is why every one of these has a matching assertion for the process
/// that holds no objects.
/// </para>
/// </summary>
public sealed class LiveInstanceRevertTests
{
    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    private static CpuTopology Reference() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    private static ImmutableArray<ProcessInfo> Background() =>
    [
        new ProcessInfo(1000, "chrome", 800L * 1024 * 1024),
        new ProcessInfo(1001, "msedge", 400L * 1024 * 1024),
    ];

    private static (FakeAmbientOperations Os, MutationLedger Ledger, InMemoryJournal Journal, DomainConfinementMutation Mutation)
        Confinement()
    {
        var os = new FakeAmbientOperations().AddProcess(1000, "chrome", 800).AddProcess(1001, "msedge", 400);
        var topology = Reference();
        var journal = new InMemoryJournal();

        return (
            os,
            new MutationLedger(journal, new AmbientMutationResolver(os, topology)),
            journal,
            new DomainConfinementMutation(os, topology.AmbientMask, Background(), topology));
    }

    [Fact]
    public async Task A_warm_revert_closes_the_job_object_the_apply_opened()
    {
        var (os, ledger, _, mutation) = Confinement();

        await ledger.ApplyAsync("s1", [mutation], Permit());

        var job = os.ActiveConfinement;
        Assert.NotNull(job);
        Assert.False(job!.Disposed);

        var report = await ledger.RevertAsync();

        Assert.True(report.IsClean, string.Join(", ", report.Failed.Select(f => $"{f.Key}: {f.Error}")));
        Assert.Contains("confine:ambient-domain", report.Reverted);
        Assert.True(
            job.Disposed,
            "the ledger rebuilt the mutation from the journal, so the job object was never closed "
            + "and the confinement is still in force.");
        Assert.Null(os.ActiveConfinement);
    }

    [Fact]
    public async Task A_cold_revert_from_a_journal_still_works_with_no_live_instance()
    {
        // The crash path, unchanged. A process that never applied anything holds no handle and
        // needs none — Windows lifted the job object's limits when the process that owned it
        // exited. Recovery's whole job here is to agree there is nothing left to do and retire
        // the entry, so the journal stops reporting the machine as changed.
        var (os, ledger, journal, mutation) = Confinement();

        await ledger.ApplyAsync("s1", [mutation], Permit());
        var job = os.ActiveConfinement;

        var cold = new MutationLedger(journal.ReopenCold(), new AmbientMutationResolver(os, Reference()));
        var report = await cold.RevertAsync();

        Assert.True(report.IsClean, string.Join(", ", report.Failed.Select(f => $"{f.Key}: {f.Error}")));
        Assert.Contains("confine:ambient-domain", report.Reverted);

        // And it did not pretend to close a handle it never had.
        Assert.False(job!.Disposed);
    }

    [Fact]
    public async Task Every_job_object_a_key_opened_is_closed_even_if_it_was_applied_twice()
    {
        // Nothing stops a caller applying the same key twice before reverting, and each apply
        // opens its own job object. Remembering only the most recent instance would close one
        // handle and leak the other, which is the same defect with an extra step.
        var (os, ledger, _, first) = Confinement();
        var topology = Reference();
        var second = new DomainConfinementMutation(os, topology.AmbientMask, Background(), topology);

        await ledger.ApplyAsync("s1", [first], Permit());
        var firstJob = os.ActiveConfinement;

        await ledger.ApplyAsync("s2", [second], Permit());
        var secondJob = os.ActiveConfinement;

        Assert.NotSame(firstJob, secondJob);

        await ledger.RevertAsync();

        Assert.True(firstJob!.Disposed, "the first job object was left open.");
        Assert.True(secondJob!.Disposed, "the second job object was left open.");
    }

    [Fact]
    public async Task The_live_instance_is_used_in_preference_to_a_rebuilt_one()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var resolver = new CountingResolver(machine);
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("s1", [mutation], Permit());
        await ledger.RevertAsync();

        Assert.Equal(1, mutation.RevertCount);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal("Running", machine["service:WSearch"]);
    }

    [Fact]
    public async Task A_mutation_whose_apply_failed_is_reverted_through_its_own_instance()
    {
        // Capture is journalled before apply precisely so a half-applied change is still
        // recoverable, and the object that got half way through is the one that knows what it
        // managed to do. Registering only successful applies would lose exactly that.
        var machine = new FakeMachine();
        machine["registry:irq-policy"] = "original";

        var journal = new InMemoryJournal();
        var resolver = new CountingResolver(machine);
        var mutation = new FakeMutation(machine, "registry:irq-policy", MutationTier.Registry, "changed")
        {
            FailOnApply = true,
        };

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("s1", [mutation], Permit());
        await ledger.RevertAsync();

        Assert.Equal(1, mutation.RevertCount);
        Assert.Equal(0, resolver.Calls);
    }

    [Fact]
    public async Task A_live_instance_is_forgotten_once_it_has_been_reverted()
    {
        // Revert is idempotent and gets called again — by the watchdog, by 'gamergod off', by
        // the boot pass. Holding the object after its entry has been retired would mean
        // disposing a handle a second time on every one of those.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        var ledger = new MutationLedger(journal, new CountingResolver(machine));

        await ledger.ApplyAsync("s1", [mutation], Permit());

        await ledger.RevertAsync();
        await ledger.RevertAsync();

        Assert.Equal(1, mutation.RevertCount);
    }

    [Fact]
    public async Task A_live_instance_that_could_not_revert_is_kept_for_the_next_attempt()
    {
        // A failed revert leaves the entry in the journal so the next pass tries again. If the
        // object were dropped at that point, the retry would reach for a rebuilt copy holding
        // no handle and the resource would be stranded for the life of the process.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var resolver = new CountingResolver(machine);
        var mutation = new FlakyMutation(machine, "service:WSearch", failuresBeforeSuccess: 1);
        var ledger = new MutationLedger(journal, resolver);

        await ledger.ApplyAsync("s1", [mutation], Permit());

        var first = await ledger.RevertAsync();
        Assert.False(first.IsClean);
        Assert.Contains("service:WSearch", first.Failed.Select(f => f.Key));

        var second = await ledger.RevertAsync();

        Assert.True(second.IsClean, string.Join(", ", second.Failed.Select(f => $"{f.Key}: {f.Error}")));
        Assert.Equal(2, mutation.Attempts);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal("Running", machine["service:WSearch"]);
    }

    [Fact]
    public async Task A_second_ledger_over_the_same_journal_still_reverts_from_the_journal()
    {
        // Two ledgers over one journal is the shape that actually occurs: the service applying
        // while a session agent reverts. The one that did not apply has no objects, and must
        // still be able to put the machine back.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(mutation);

        await new MutationLedger(journal, resolver).ApplyAsync("s1", [mutation], Permit());
        var report = await new MutationLedger(journal, resolver).RevertAsync();

        Assert.True(report.IsClean);
        Assert.Equal(0, mutation.RevertCount);
        Assert.True(machine.Matches(before), machine.Describe());
    }

    /// <summary>Counts how often the cold rebuild path was taken.</summary>
    private sealed class CountingResolver(FakeMachine machine) : IMutationResolver
    {
        public int Calls { get; private set; }

        public IMutation? Resolve(string mutationType, string key)
        {
            Calls++;
            return new FakeMutation(machine, key, MutationTier.Registry, "<irrelevant>");
        }
    }

    /// <summary>Refuses to revert a fixed number of times, then succeeds.</summary>
    private sealed class FlakyMutation(FakeMachine machine, string key, int failuresBeforeSuccess) : IMutation
    {
        public string Key => key;

        public MutationTier Tier => MutationTier.Service;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => false;

        public int Attempts { get; private set; }

        public string Describe() => $"flaky {key}";

        public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new Capture(machine[key])));

        public ValueTask ApplyAsync(CancellationToken cancellationToken)
        {
            machine[key] = "Stopped";
            return ValueTask.CompletedTask;
        }

        public ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
        {
            Attempts++;

            if (Attempts <= failuresBeforeSuccess)
            {
                throw new InvalidOperationException($"{key} could not be put back yet");
            }

            machine[key] = capture.Deserialize<Capture>()!.Previous;
            return ValueTask.CompletedTask;
        }

        private sealed record Capture(string Previous);
    }
}
