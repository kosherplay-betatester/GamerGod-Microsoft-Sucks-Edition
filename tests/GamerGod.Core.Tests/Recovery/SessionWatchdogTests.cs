using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Recovery;

/// <summary>
/// The service-side watchdog: Charter Article X's third escape path.
///
/// <para>
/// It watches the process that armed a session. When that process stops existing — closed,
/// killed, crashed — the machine goes back, without anything in the dead process having to
/// run. The other two escapes both depend on the agent still working; this one exists for
/// when it does not.
/// </para>
///
/// <para>
/// It has to be conservative in one direction and decisive in the other. Reverting when the
/// owner is still alive ends somebody's session mid-match for no reason. Failing to revert
/// when the owner is gone strands the machine until the next reboot. Only the second one is
/// a broken promise, but the first is the one that loses trust.
/// </para>
/// </summary>
public sealed class SessionWatchdogTests
{
    private static readonly TimeSpan Immediately = TimeSpan.FromMilliseconds(1);

    private static readonly ProcessIdentity Owner = new(4812, StartedAtUtcTicks: 638_000_000_000_000_000L);

    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>A journal describing a live session, and the machine it changed.</summary>
    private static async Task<(MutationLedger Ledger, FakeMachine Machine, FakeMachine Before)> LiveSessionAsync()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["affinity:ambient-domain"] = "unrestricted";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        var mutations = ImmutableArray.Create<IMutation>(
            new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped"),
            new FakeMutation(machine, "affinity:ambient-domain", MutationTier.CpuRouting, "0xFFFF0000"));

        foreach (var mutation in mutations)
        {
            resolver.Remember(mutation);
        }

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("live", mutations, Permit());

        return (ledger, machine, before);
    }

    [Fact]
    public async Task The_machine_goes_back_when_the_arming_process_exits()
    {
        var (ledger, machine, before) = await LiveSessionAsync();
        var liveness = new ScriptedLiveness(Owner, Owner, null);

        var report = await new SessionWatchdog(ledger, liveness, Immediately)
            .WatchAsync(Owner, default);

        Assert.Equal(WatchdogOutcome.OwnerExited, report.Outcome);
        Assert.NotNull(report.Revert);
        Assert.True(report.Revert!.IsClean, report.Explain());
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_live_session_is_left_alone()
    {
        var (ledger, machine, before) = await LiveSessionAsync();
        var liveness = new ScriptedLiveness(Owner);

        using var stop = new CancellationTokenSource();
        var watching = new SessionWatchdog(ledger, liveness, Immediately)
            .WatchAsync(Owner, stop.Token);

        // Several polls' worth of chances to get this wrong.
        await WaitUntilAsync(() => liveness.Calls >= 5, "the watchdog stopped polling");

        await stop.CancelAsync();
        var report = await watching;

        Assert.Equal(WatchdogOutcome.Cancelled, report.Outcome);
        Assert.Null(report.Revert);
        Assert.False(machine.Matches(before), "a live session's changes were undone.");
    }

    [Fact]
    public async Task A_recycled_process_id_counts_as_an_exit()
    {
        // Windows reuses process ids aggressively. Watching the number alone means the
        // watchdog can end up guarding an unrelated program that happens to have inherited
        // it, and never firing for the session it was armed for.
        var (ledger, machine, before) = await LiveSessionAsync();

        var impostor = Owner with { StartedAtUtcTicks = Owner.StartedAtUtcTicks + 10_000_000L };
        var liveness = new ScriptedLiveness(Owner, impostor);

        var report = await new SessionWatchdog(ledger, liveness, Immediately)
            .WatchAsync(Owner, default);

        Assert.Equal(WatchdogOutcome.OwnerExited, report.Outcome);
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task An_owner_that_is_already_gone_fires_on_the_first_poll()
    {
        var (ledger, machine, before) = await LiveSessionAsync();

        var report = await new SessionWatchdog(ledger, new ScriptedLiveness((ProcessIdentity?)null), Immediately)
            .WatchAsync(Owner, default);

        Assert.Equal(WatchdogOutcome.OwnerExited, report.Outcome);
        Assert.True(machine.Matches(before));
    }

    [Fact]
    public async Task A_liveness_probe_that_fails_does_not_fire_the_watchdog()
    {
        // "I cannot tell" is not "it exited". Treating a transient failure as an exit would
        // end a live session on nothing more than a bad answer from the operating system.
        var (ledger, machine, _) = await LiveSessionAsync();
        var applied = machine.Snapshot();

        var liveness = new ScriptedLiveness(Owner) { ThrowFor = 3 };

        using var stop = new CancellationTokenSource();
        var watching = new SessionWatchdog(ledger, liveness, Immediately).WatchAsync(Owner, stop.Token);

        await WaitUntilAsync(() => liveness.Calls >= 5, "the watchdog stopped polling");

        await stop.CancelAsync();
        var report = await watching;

        Assert.Equal(WatchdogOutcome.Cancelled, report.Outcome);
        Assert.True(machine.Matches(applied), "the session was reverted on a failed probe.");
    }

    [Fact]
    public async Task Stopping_the_service_does_not_revert_a_live_session()
    {
        // A service stop is an upgrade or a shutdown, not a crash. The session's owner is
        // still there, and taking its partition away is a change nobody asked for.
        var (ledger, machine, _) = await LiveSessionAsync();
        var applied = machine.Snapshot();

        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();

        var report = await new SessionWatchdog(ledger, new ScriptedLiveness(Owner), Immediately)
            .WatchAsync(Owner, stop.Token);

        Assert.Equal(WatchdogOutcome.Cancelled, report.Outcome);
        Assert.True(machine.Matches(applied));
    }

    [Fact]
    public async Task A_revert_already_under_way_is_not_abandoned_when_the_service_stops()
    {
        // Once the decision to restore has been taken, a half-finished revert is the worst
        // possible outcome: the journal says reverted for some keys and the machine still
        // holds the rest.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var gate = new TaskCompletionSource();

        // The session's own object, not a rebuild: this is the process that applied, so this
        // is the object the ledger reverts through.
        var mutation = new GatedMutation(machine, "service:WSearch", "Stopped", gate.Task);
        var ledger = new MutationLedger(journal, new FakeResolver(machine));
        await ledger.ApplyAsync("live", [mutation], Permit());

        using var stop = new CancellationTokenSource();
        var watching = new SessionWatchdog(ledger, new ScriptedLiveness((ProcessIdentity?)null), Immediately)
            .WatchAsync(Owner, stop.Token);

        await WaitUntilAsync(() => mutation.RevertStarted, "the watchdog never started reverting");

        await stop.CancelAsync();
        gate.SetResult();

        var report = await watching;

        Assert.Equal(WatchdogOutcome.OwnerExited, report.Outcome);
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_revert_that_throws_is_reported_rather_than_taking_the_service_down()
    {
        var report = await new SessionWatchdog(
                new MutationLedger(new HostileJournal(), new FakeResolver(new FakeMachine())),
                new ScriptedLiveness((ProcessIdentity?)null),
                Immediately)
            .WatchAsync(Owner, default);

        Assert.Equal(WatchdogOutcome.OwnerExited, report.Outcome);
        Assert.NotNull(report.RevertError);
        Assert.False(report.IsClean);
    }

    [Fact]
    public async Task The_watchdog_stops_watching_once_it_has_fired()
    {
        // It returns rather than looping, so a session can only be reverted once and the
        // caller decides whether to arm it again.
        var (ledger, _, _) = await LiveSessionAsync();
        var liveness = new ScriptedLiveness((ProcessIdentity?)null);

        await new SessionWatchdog(ledger, liveness, Immediately).WatchAsync(Owner, default);
        var callsWhenItFired = liveness.Calls;

        await Task.Delay(20, CancellationToken.None);

        Assert.Equal(callsWhenItFired, liveness.Calls);
    }

    [Fact]
    public async Task Watching_reports_plainly_and_claims_nothing_it_did_not_do()
    {
        var (ledger, _, _) = await LiveSessionAsync();

        var report = await new SessionWatchdog(ledger, new ScriptedLiveness((ProcessIdentity?)null), Immediately)
            .WatchAsync(Owner, default);

        var text = report.Explain();
        Assert.False(string.IsNullOrWhiteSpace(text));

        foreach (var claim in new[] { "faster", "fps", "boost", "improve" })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_watchdog_without_a_ledger_or_a_liveness_source_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SessionWatchdog(null!, new ScriptedLiveness((ProcessIdentity?)null), Immediately));

        Assert.Throws<ArgumentNullException>(() =>
            new SessionWatchdog(
                new MutationLedger(new InMemoryJournal(), new FakeResolver(new FakeMachine())),
                null!,
                Immediately));
    }

    [Fact]
    public void A_poll_interval_of_zero_or_less_is_refused()
    {
        // A zero interval turns the watchdog into a spin loop on a core the user wanted for
        // their game, which would make the safety net the contention.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SessionWatchdog(
                new MutationLedger(new InMemoryJournal(), new FakeResolver(new FakeMachine())),
                new ScriptedLiveness((ProcessIdentity?)null),
                TimeSpan.Zero));
    }

    /// <summary>
    /// Waits for a condition, and fails rather than hanging when it never arrives. A test
    /// that deadlocks tells nobody anything, and does it for the full length of a CI run.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string whatWentWrong)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, whatWentWrong);
            await Task.Delay(1, CancellationToken.None);
        }
    }

    /// <summary>
    /// Answers a scripted sequence of liveness probes; the last answer repeats forever.
    /// </summary>
    private sealed class ScriptedLiveness(params ProcessIdentity?[] answers) : IProcessLiveness
    {
        private int _index;

        public int Calls { get; private set; }

        /// <summary>How many of the first probes should fail outright.</summary>
        public int ThrowFor { get; init; }

        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken)
        {
            Calls++;

            if (Calls <= ThrowFor)
            {
                throw new InvalidOperationException("the operating system refused the query");
            }

            var answer = answers[Math.Min(_index, answers.Length - 1)];
            _index++;
            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>A mutation whose revert parks until a gate opens.</summary>
    private sealed class GatedMutation(
        FakeMachine machine, string key, string newValue, Task gate) : IMutation
    {
        public string Key => key;

        public MutationTier Tier => MutationTier.Service;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => false;

        public bool RevertStarted { get; private set; }

        public string Describe() => $"gated {key}";

        public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(JsonSerializer.SerializeToElement(new Capture(machine[key])));

        public ValueTask ApplyAsync(CancellationToken cancellationToken)
        {
            machine[key] = newValue;
            return ValueTask.CompletedTask;
        }

        public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
        {
            RevertStarted = true;
            await gate;
            machine[key] = capture.Deserialize<Capture>()!.Previous;
        }

        internal sealed record Capture(string Previous);
    }

    /// <summary>A journal that will not let anything be reverted.</summary>
    private sealed class HostileJournal : IJournal
    {
        public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<ImmutableArray<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken) =>
            throw new IOException("the journal is unreadable");

        public ValueTask<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IAsyncDisposable>(new Nothing());

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
