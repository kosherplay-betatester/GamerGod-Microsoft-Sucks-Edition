using System.Collections.Immutable;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Recovery;

/// <summary>
/// The pass the background service runs when it starts.
///
/// <para>
/// This is the third of Charter Article X's four escape paths, and the one that has to work
/// when the other two are gone: the hotkey needs a running agent and the controller combo
/// needs a running agent, but a machine that lost power in the middle of a session has
/// neither. What it has is a journal on disk and a service that starts before anybody signs
/// in.
/// </para>
///
/// <para>
/// Two properties matter more than anything else here. It must put the machine back, and it
/// must never throw — the service is registered with restart-on-failure, so an exception
/// escaping this pass is not a crash, it is a crash loop.
/// </para>
/// </summary>
public sealed class BootRecoveryTests
{
    /// <summary>
    /// The liveness answer for a journal that names no owner: nothing to ask about. Owner
    /// identity is exercised in <see cref="SessionOwnershipTests"/>; these tests are about
    /// what the pass does once it has decided a session is orphaned.
    /// </summary>
    private static readonly IProcessLiveness NobodyIsRunning = new NoProcessIsRunning();

    /// <summary>
    /// An owner that <see cref="NobodyIsRunning"/> reports as gone.
    ///
    /// <para>
    /// Tests about cancellation, failure reporting and idempotence need a session boot recovery
    /// will actually revert, and an ownerless session is no longer one: a tool that applies and
    /// exits leaves no owner by design, so recovery leaves it alone. Recording a dead owner
    /// makes these sessions genuinely orphaned, which is what they always meant to be.
    /// </para>
    /// </summary>
    private static readonly ProcessIdentity DeadOwner = new(4812, StartedAtUtcTicks: 638_000_000_000_000_000L);

    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    private static ImmutableArray<IMutation> Session(FakeMachine machine, FakeResolver resolver)
    {
        var mutations = ImmutableArray.Create<IMutation>(
            new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped"),
            new FakeMutation(machine, "affinity:ambient-domain", MutationTier.CpuRouting, "0xFFFF0000"));

        foreach (var mutation in mutations)
        {
            resolver.Remember(mutation);
        }

        return mutations;
    }

    [Fact]
    public async Task A_session_killed_part_way_through_is_reverted_by_the_next_start()
    {
        // The service is stopped mid-session - task manager, an upgrade, a bugcheck. Nothing
        // it applied gets a chance to be undone by the process that applied it.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["affinity:ambient-domain"] = "unrestricted";
        var before = machine.Snapshot();

        var durable = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        // Dies after the second capture reaches the disk, with both changes on the machine.
        var dying = new MutationLedger(new CrashingJournal(durable, crashAfterWrites: 4), resolver);

        await Assert.ThrowsAsync<SimulatedCrash>(async () =>
            await dying.ApplyAsync("killed", Session(machine, resolver), Permit()));

        Assert.False(machine.Matches(before), "the test needs the machine to actually be changed");

        // A cold process: new ledger, new resolver, and only the lines that reached the disk.
        var next = new MutationLedger(durable.ReopenCold(), new FakeResolver(machine));
        var outcome = await BootRecovery.RunAsync(next, NobodyIsRunning, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_clean_journal_is_a_no_op()
    {
        var journal = new InMemoryJournal();
        var machine = new FakeMachine();

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, new FakeResolver(machine)), NobodyIsRunning, default);

        Assert.False(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean);
        Assert.Null(outcome.Report);

        // Nothing written either. A service that starts every boot must not grow the journal
        // by one line per boot forever.
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task Running_the_pass_twice_finds_nothing_the_second_time()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["affinity:ambient-domain"] = "unrestricted";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        var mutations = Session(machine, resolver);

        await new MutationLedger(journal, resolver).ApplyAsync("s1", mutations, Permit(), DeadOwner);

        var ledger = new MutationLedger(journal, resolver);

        var first = await BootRecovery.RunAsync(ledger, NobodyIsRunning, default);
        var second = await BootRecovery.RunAsync(ledger, NobodyIsRunning, default);

        Assert.True(first.HadOutstandingChanges);
        Assert.False(second.HadOutstandingChanges);
        Assert.True(machine.Matches(before));
    }

    [Fact]
    public async Task Process_changes_from_a_previous_boot_are_not_replayed_onto_recycled_ids()
    {
        // Delegated entirely to the ledger, which already knows that a change tied to a
        // process died with that process. Restated here because boot recovery is where
        // getting it wrong would write a stranger's affinity mask onto a stranger's process.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "old",
                MachineBootedAtUtcTicks = DateTimeOffset.UtcNow.AddDays(-3).UtcTicks,
            },
            default);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Capture,
                SessionId = "old",
                Key = "affinity:ambient-domain",
                MutationType = "Stub",
                Tier = MutationTier.CpuRouting,
                State = """{"Previous":"original"}""",
            },
            default);

        var machine = new FakeMachine();
        machine["affinity:ambient-domain"] = "belongs to something else now";

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, new FakeResolver(machine)), NobodyIsRunning, default);

        Assert.False(outcome.HadOutstandingChanges);
        Assert.Equal("belongs to something else now", machine["affinity:ambient-domain"]);
    }

    [Fact]
    public async Task A_change_that_outlives_a_reboot_is_still_reverted()
    {
        // The half boot recovery exists for. Nothing GamerGod ships today is boot-persistent,
        // and the first thing that is must be undone by this pass or Article VI is a claim
        // rather than a guarantee.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "old",
                MachineBootedAtUtcTicks = DateTimeOffset.UtcNow.AddDays(-3).UtcTicks,
            },
            default);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Capture,
                SessionId = "old",
                Key = "registry:irq-policy",
                MutationType = "Stub",
                Tier = MutationTier.Registry,
                IsBootPersistent = true,
                State = """{"Previous":"original"}""",
            },
            default);

        var machine = new FakeMachine();
        machine["registry:irq-policy"] = "changed";

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, new FakeResolver(machine)), NobodyIsRunning, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Equal("original", machine["registry:irq-policy"]);
    }

    [Fact]
    public async Task A_revert_that_fails_is_reported_and_the_rest_still_runs()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["power:scheme"] = "Balanced";

        var journal = new InMemoryJournal();
        var resolver = new StubbornResolver(machine, refuses: "service:WSearch");

        var mutations = ImmutableArray.Create<IMutation>(
            new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped"),
            new FakeMutation(machine, "power:scheme", MutationTier.Power, "GamerGod"));

        await new MutationLedger(journal, resolver).ApplyAsync("s1", mutations, Permit(), DeadOwner);

        var outcome = await BootRecovery.RunAsync(new MutationLedger(journal, resolver), NobodyIsRunning, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.False(outcome.IsClean);
        Assert.NotNull(outcome.Report);
        Assert.Contains("service:WSearch", outcome.Report!.Failed.Select(f => f.Key));

        // One stubborn change must never strand the ones after it.
        Assert.Equal("Balanced", machine["power:scheme"]);
    }

    [Fact]
    public async Task A_journal_that_cannot_be_read_is_reported_rather_than_thrown()
    {
        // The service restarts on failure. An exception escaping this pass is not a crash,
        // it is a crash loop, and a crash loop on a LocalSystem service is a support ticket
        // nobody can diagnose.
        var machine = new FakeMachine();

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(new UnreadableJournal(), new FakeResolver(machine)), NobodyIsRunning, default);

        Assert.False(outcome.IsClean);
        Assert.NotNull(outcome.Error);
        Assert.Contains("disk", outcome.Explain(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_cancelled_pass_is_reported_rather_than_thrown()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        await new MutationLedger(journal, resolver)
            .ApplyAsync("s1", Session(machine, resolver), Permit(), DeadOwner);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, resolver), NobodyIsRunning, cancelled.Token);

        Assert.False(outcome.IsClean);
        Assert.NotNull(outcome.Error);
    }

    [Fact]
    public async Task The_explanation_states_what_happened_and_claims_nothing_else()
    {
        // Charter Article VII. This text ends up in the Windows event log, where it is the
        // only account anybody will ever have of what the service did at 3am.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        await new MutationLedger(journal, resolver)
            .ApplyAsync("s1", Session(machine, resolver), Permit(), DeadOwner);

        var outcome = await BootRecovery.RunAsync(new MutationLedger(journal, resolver), NobodyIsRunning, default);
        var text = outcome.Explain();

        Assert.False(string.IsNullOrWhiteSpace(text));

        foreach (var claim in new[] { "faster", "fps", "boost", "improve", "optimis", "optimiz" })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Recovery_refuses_a_null_ledger_rather_than_reporting_success()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await BootRecovery.RunAsync(null!, NobodyIsRunning, default));
    }

    /// <summary>Every process id is gone, so every session is an orphan.</summary>
    private sealed class NoProcessIsRunning : IProcessLiveness
    {
        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(null);
    }

    /// <summary>A resolver whose rebuilt mutation refuses to revert one named key.</summary>
    private sealed class StubbornResolver(FakeMachine machine, string refuses) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key) =>
            new FakeMutation(machine, key, MutationTier.Registry, "<irrelevant>")
            {
                FailOnRevert = string.Equals(key, refuses, StringComparison.Ordinal),
            };
    }

    /// <summary>A journal whose storage has gone away underneath it.</summary>
    private sealed class UnreadableJournal : IJournal
    {
        public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken) =>
            throw new IOException("the disk holding the journal is not readable");

        public ValueTask<ImmutableArray<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken) =>
            throw new IOException("the disk holding the journal is not readable");

        public ValueTask<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken) =>
            throw new IOException("the disk holding the journal is not readable");
    }
}
