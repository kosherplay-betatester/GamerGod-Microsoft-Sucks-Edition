using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Recovery;

/// <summary>
/// What happens when somebody arms Game Mode while the boot-recovery pass is deciding.
///
/// <para>
/// The pass used to do three things in sequence and lock for only the last of them: read the
/// outstanding sessions, probe the operating system for each recorded owner, then revert
/// everything not in the list it had built. Probing is the slow part — one process handle per
/// session — and the user is free to press the switch throughout. A session that began in that
/// window was absent from the retained list and present in the journal by the time the revert
/// read it, so it was reverted while its process was alive and watching.
/// </para>
///
/// <para>
/// This is the same defect as the service ending live sessions, which was found on real hardware
/// and fixed once already. It came back through the gap between deciding and acting rather than
/// through the decision itself, which is why the fix is structural: the journal is held across
/// all three steps, so there is no window for a session to appear in.
/// </para>
/// </summary>
public sealed class RecoveryRaceTests
{
    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>An owner every probe here reports as gone, so the orphan is genuinely orphaned.</summary>
    private static readonly ProcessIdentity DeadOwner = new(4812, StartedAtUtcTicks: 638_000_000_000_000_000L);

    [Fact]
    public async Task The_journal_is_held_for_the_whole_pass_not_just_the_revert()
    {
        // The structural property, asserted directly. Everything else in this file depends on
        // it, and it is the thing that had been missing rather than any particular decision.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();
        var orphaned = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(orphaned);

        await new MutationLedger(journal, resolver)
            .ApplyAsync("orphan", [orphaned], Permit(), DeadOwner);

        var heldDuringProbe = false;

        var probe = new ProbeThatObserves(async () =>
        {
            // Anything else wanting the journal — an apply from the app, a `gamergod off` —
            // waits here rather than slipping between the decision and the revert.
            var contender = journal.AcquireExclusiveAsync(default).AsTask();
            var finished = await Task.WhenAny(contender, Task.Delay(TimeSpan.FromMilliseconds(250)));

            heldDuringProbe = finished != contender;

            if (finished == contender)
            {
                await (await contender).DisposeAsync();
            }
        });

        await BootRecovery.RunAsync(new MutationLedger(journal, resolver), probe, default);

        Assert.True(probe.Fired, "the probe never ran, so this proves nothing");
        Assert.True(heldDuringProbe, "the journal was free while boot recovery was deciding");
    }

    [Fact]
    public async Task A_session_armed_while_the_owners_are_being_probed_is_not_reverted()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["power:scheme"] = "Balanced";

        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();

        // An orphan for the pass to find, so it has a reason to probe at all.
        var orphaned = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(orphaned);

        await new MutationLedger(journal, resolver)
            .ApplyAsync("orphan", [orphaned], Permit(), DeadOwner);

        // The user presses the switch during the probe. A real apply, through the real ledger,
        // against the same journal — on another thread, because that is where it comes from.
        var newcomer = new FakeMutation(machine, "power:scheme", MutationTier.Power, "GamerGod");
        resolver.Remember(newcomer);

        Task? arming = null;
        var started = new TaskCompletionSource();

        var probe = new ProbeThatObserves(async () =>
        {
            arming = Task.Run(async () =>
            {
                started.SetResult();
                await new MutationLedger(journal, resolver)
                    .ApplyAsync("arrived-late", [newcomer], Permit());
            });

            // It has begun. Under the old code it would now sail through, land in the journal
            // mid-pass, and be reverted below. Under this one it is waiting for the journal.
            await started.Task;
            await Task.Yield();
        });

        var outcome = await BootRecovery.RunAsync(new MutationLedger(journal, resolver), probe, default);

        Assert.True(probe.Fired, "the probe never ran, so this proves nothing");
        await arming!;

        // The orphan was cleaned up...
        Assert.Equal("Running", machine["service:WSearch"]);
        Assert.True(outcome.IsClean, outcome.Explain());

        // ...and the session that arrived mid-pass is applied and still recorded as applied.
        Assert.Equal("GamerGod", machine["power:scheme"]);

        var left = await new MutationLedger(journal, resolver).OutstandingSessionsAsync();
        Assert.Contains(left, s => s.SessionId == "arrived-late");
    }

    [Fact]
    public async Task The_pass_still_reverts_an_orphan_when_nothing_races_it()
    {
        // The guard must not have turned recovery into a no-op. Same shape, nothing racing.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();
        var orphaned = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(orphaned);

        await new MutationLedger(journal, resolver)
            .ApplyAsync("orphan", [orphaned], Permit(), DeadOwner);

        Assert.Equal("Stopped", machine["service:WSearch"]);

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, resolver),
            new ProbeThatObserves(() => Task.CompletedTask),
            default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Equal("Running", machine["service:WSearch"]);
    }

    /// <summary>
    /// A liveness probe that reports every owner gone, and runs a caller-supplied action the
    /// first time it is asked — the window between deciding and acting, made observable.
    /// </summary>
    private sealed class ProbeThatObserves(Func<Task> duringFirstProbe) : IProcessLiveness
    {
        public bool Fired { get; private set; }

        public async ValueTask<ProcessIdentity?> IdentifyAsync(
            int processId, CancellationToken cancellationToken)
        {
            if (!Fired)
            {
                Fired = true;
                await duringFirstProbe();
            }

            return null;
        }
    }
}
