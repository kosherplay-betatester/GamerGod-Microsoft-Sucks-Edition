using System.Collections.Immutable;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Recovery;

/// <summary>
/// Telling a session that was orphaned by a crash from one that is still running.
///
/// <para>
/// Boot recovery used to revert any outstanding journal at every service start, because the
/// journal recorded nothing about who armed the session. Restarting GamerGodService — an
/// upgrade, a manual restart, a failed start followed by a retry — therefore turned Game Mode
/// off underneath somebody who was still playing. It erred safe, and it was still wrong: a
/// tool whose whole claim is that it puts your machine back exactly cannot also change it at
/// a moment nobody chose.
/// </para>
///
/// <para>
/// The asymmetry runs the other way from the watchdog's. Skipping a session whose owner is
/// really gone strands the machine until the next reboot, so anything short of a confident
/// "that exact process is still running" means revert.
/// </para>
/// </summary>
public sealed class SessionOwnershipTests
{
    private static readonly ProcessIdentity Owner = new(4812, StartedAtUtcTicks: 638_000_000_000_000_000L);

    /// <summary>
    /// Found on real hardware, and only there: <c>gamergod on</c> armed the machine,
    /// <c>Restart-Service GamerGodService</c> ran, and Game Mode came back off.
    ///
    /// <para>
    /// The command-line tool applies and exits — that is its design, and the desktop app does
    /// the same. Neither records an owner, because neither stays to be one. Recovery read "no
    /// owner" as "nobody is left to end this session" and reverted it, which is the original
    /// bug wearing a justification: every session on the machine died the moment the service
    /// restarted.
    /// </para>
    ///
    /// <para>
    /// Article VI is about surviving a <em>reboot</em>, and a reboot is exactly the case the
    /// boot check already handles. Within one boot the user asked for this and can end it with
    /// <c>gamergod off</c>. Reverting it is not upholding the Charter, it is overruling them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_session_armed_by_a_tool_that_exits_survives_a_service_restart()
    {
        var (ledger, machine, before) = await ArmedByAsync(owner: null);

        var outcome = await BootRecovery.RunAsync(ledger, NoProcessIsAlive, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.Empty(outcome.Report?.Reverted ?? []);
        Assert.False(machine.Matches(before), "the session was reverted despite nobody asking");
    }

    /// <summary>
    /// The other half, and the reason the rule is about the boot rather than about the owner:
    /// across a restart an ownerless session must still be recovered, because a boot-persistent
    /// change really would otherwise outlive the reboot with nobody left to undo it.
    /// </summary>
    [Fact]
    public async Task An_ownerless_session_from_a_previous_boot_is_still_recovered()
    {
        var machine = new FakeMachine();
        machine["registry:irq-policy"] = "changed";

        var journal = new InMemoryJournal();

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "old",

                // Far enough back that the machine has certainly restarted since.
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

                // Boot-persistent: a reboot did NOT undo this one, so it is exactly the case
                // where an ownerless session must still be recovered.
                IsBootPersistent = true,
                State = """{"Previous":"original"}""",
            },
            default);

        var ledger = new MutationLedger(journal, new FakeResolver(machine));

        var outcome = await BootRecovery.RunAsync(ledger, NoProcessIsAlive, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.Equal("original", machine["registry:irq-policy"]);
    }

    /// <summary>Every probe says the process is gone, which is the interesting case.</summary>
    private static IProcessLiveness NoProcessIsAlive { get; } = new DeadProcesses();

    private sealed class DeadProcesses : IProcessLiveness
    {
        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken token) =>
            ValueTask.FromResult<ProcessIdentity?>(null);
    }

    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>
    /// Arms a session and hands back the ledger a <em>different process</em> would see. The
    /// service that runs boot recovery never applied any of this and holds none of its
    /// objects, so anything that works here works after a bugcheck too.
    /// </summary>
    private static async Task<(MutationLedger Ledger, FakeMachine Machine, FakeMachine Before)> ArmedByAsync(
        ProcessIdentity? owner, string sessionId = "live")
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

        await new MutationLedger(journal, resolver).ApplyAsync(sessionId, mutations, Permit(), owner);

        return (new MutationLedger(journal.ReopenCold(), new FakeResolver(machine)), machine, before);
    }

    [Fact]
    public async Task A_live_session_survives_a_service_restart()
    {
        // The defect, stated as a test. The service comes back up while somebody is mid-match
        // and the process that armed the session is still there.
        var (ledger, machine, before) = await ArmedByAsync(Owner);
        var applied = machine.Snapshot();

        var outcome = await BootRecovery.RunAsync(ledger, new StubLiveness(Owner), default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Null(outcome.Report);
        Assert.Contains("live", outcome.LeftToTheirOwner);

        Assert.False(machine.Matches(before), "the test needs the session to have changed something");
        Assert.True(machine.Matches(applied), machine.Describe());
    }

    [Fact]
    public async Task An_orphaned_session_is_still_recovered()
    {
        // The half boot recovery exists for, and the half that must never regress in the
        // course of fixing the other one.
        var (ledger, machine, before) = await ArmedByAsync(Owner);

        var outcome = await BootRecovery.RunAsync(ledger, new StubLiveness(null), default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Empty(outcome.LeftToTheirOwner);
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_recycled_owner_process_id_does_not_protect_a_session_for_ever()
    {
        // Windows reuses process ids aggressively. Matching on the number alone would mean an
        // unrelated program inheriting it kept a stranded session alive at every boot from
        // then on, which is the failure mode that turns "leave live sessions alone" into
        // "never recover anything".
        var (ledger, machine, before) = await ArmedByAsync(Owner);
        var impostor = Owner with { StartedAtUtcTicks = Owner.StartedAtUtcTicks + 10_000_000L };

        var outcome = await BootRecovery.RunAsync(ledger, new StubLiveness(impostor), default);

        Assert.Empty(outcome.LeftToTheirOwner);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_journal_with_no_owner_recorded_is_left_alone_and_never_probed()
    {
        // This test used to assert the opposite, and its reasoning was: "an unclaimed session
        // is one nobody can end, and leaving it would strand the machine until a reboot."
        //
        // Both halves are false. `gamergod off` ends it, the desktop app ends it, and a reboot
        // ends it — Article X's escape paths are all intact. And a session is unclaimed for the
        // ordinary reason that the command line applies and exits, which is its design, not an
        // accident. Acting on that assumption meant `gamergod on` followed by a service restart
        // turned Game Mode off, which no test caught because every test's caller exited too.
        //
        // Recovery still leaves it alone without asking the operating system anything: with no
        // owner there is no identity to probe, and a completed apply in the current boot is a
        // state the user chose.
        var (ledger, machine, before) = await ArmedByAsync(owner: null);
        var liveness = new StubLiveness(Owner);

        var outcome = await BootRecovery.RunAsync(ledger, liveness, default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.False(machine.Matches(before), "the session was reverted despite nobody asking");
        Assert.Equal(0, liveness.Calls);
    }

    [Fact]
    public async Task An_owner_recorded_without_a_start_time_is_not_trusted()
    {
        // Half an identity is no identity. A bare process id cannot be told apart from the
        // same id belonging to something else, and guessing in the permissive direction here
        // would leave a machine changed with nothing able to change it back.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "half",
                MachineBootedAtUtcTicks = MachineBoot.At().UtcTicks,
                OwnerProcessId = Owner.ProcessId,
            },
            default);

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Capture,
                SessionId = "half",
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
            new MutationLedger(journal, new FakeResolver(machine)), new StubLiveness(Owner), default);

        Assert.Empty(outcome.LeftToTheirOwner);
        Assert.Equal("original", machine["registry:irq-policy"]);
    }

    [Fact]
    public async Task An_owner_from_before_the_last_reboot_is_never_asked_about()
    {
        // A restart ends every process on the machine, so the recorded owner is gone whatever
        // a probe says now — whoever holds that id today is somebody else. Asking would be
        // worse than pointless: a chance match would leave a boot-persistent change applied
        // with no owner alive to ever undo it.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "old",
                MachineBootedAtUtcTicks = DateTimeOffset.UtcNow.AddDays(-3).UtcTicks,
                OwnerProcessId = Owner.ProcessId,
                OwnerStartedAtUtcTicks = Owner.StartedAtUtcTicks,
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
        var liveness = new StubLiveness(Owner);

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, new FakeResolver(machine)), liveness, default);

        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Equal("original", machine["registry:irq-policy"]);
        Assert.Equal(0, liveness.Calls);
    }

    [Fact]
    public async Task A_liveness_probe_that_fails_is_treated_as_an_orphan()
    {
        // The opposite of the watchdog's rule, on purpose. The watchdog acts by reverting a
        // live session, so it must be sure; this pass acts by leaving one applied, so an
        // uncertain answer has to fall back to the guarantee rather than the convenience.
        var (ledger, machine, before) = await ArmedByAsync(Owner);

        var outcome = await BootRecovery.RunAsync(
            ledger,
            new StubLiveness(Owner) { Throws = new InvalidOperationException("the OS refused the query") },
            default);

        Assert.Empty(outcome.LeftToTheirOwner);
        Assert.True(machine.Matches(before), machine.Describe());
    }

    [Fact]
    public async Task A_cancelled_probe_changes_nothing_at_all()
    {
        // A service stop arriving mid-pass. Swallowing the cancellation and calling it an
        // orphan would revert a live session because Windows was shutting down.
        var (ledger, machine, _) = await ArmedByAsync(Owner);
        var applied = machine.Snapshot();

        var outcome = await BootRecovery.RunAsync(
            ledger,
            new StubLiveness(Owner) { Throws = new OperationCanceledException() },
            default);

        Assert.NotNull(outcome.Error);
        Assert.False(outcome.IsClean);
        Assert.True(machine.Matches(applied), machine.Describe());
    }

    [Fact]
    public async Task The_owner_is_recorded_on_the_session_begin_line_and_nowhere_else()
    {
        var machine = new FakeMachine();
        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");

        await new MutationLedger(journal, resolver).ApplyAsync("s1", [mutation], Permit(), Owner);

        var begin = Assert.Single(journal.Entries, e => e.Op == JournalOp.SessionBegin);
        Assert.Equal(Owner.ProcessId, begin.OwnerProcessId);
        Assert.Equal(Owner.StartedAtUtcTicks, begin.OwnerStartedAtUtcTicks);

        // Everywhere else it is zero. The owner belongs to a session, not to a change.
        Assert.All(
            journal.Entries.Where(e => e.Op != JournalOp.SessionBegin),
            e => Assert.Equal(0, e.OwnerProcessId));
    }

    [Fact]
    public async Task Only_the_orphaned_half_of_two_sessions_is_recovered()
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        machine["power:scheme"] = "Balanced";

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        var alive = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        var dead = new FakeMutation(machine, "power:scheme", MutationTier.Power, "GamerGod");
        resolver.Remember(alive);
        resolver.Remember(dead);

        var ghost = new ProcessIdentity(9001, Owner.StartedAtUtcTicks);

        await new MutationLedger(journal, resolver).ApplyAsync("alive", [alive], Permit(), Owner);
        await new MutationLedger(journal, resolver).ApplyAsync("dead", [dead], Permit(), ghost);

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal.ReopenCold(), new FakeResolver(machine)),
            new SelectiveLiveness(Owner),
            default);

        Assert.Contains("alive", outcome.LeftToTheirOwner);
        Assert.DoesNotContain("dead", outcome.LeftToTheirOwner);
        Assert.Equal("Balanced", machine["power:scheme"]);
        Assert.Equal("Stopped", machine["service:WSearch"]);
    }

    [Fact]
    public async Task A_key_a_live_session_still_holds_is_never_restored_to_our_own_value()
    {
        // The dangerous overlap. Two sessions touched one key: the live one captured the
        // user's original, the orphaned one captured what GamerGod had already put there.
        // Neither answer is available. Restoring the orphan's capture writes GamerGod's own
        // intermediate value onto the machine and calls it a revert; restoring the first
        // capture ends the live session, which is the surprise this whole change exists to
        // remove. So the key is left exactly as it is until somebody ends the live session.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);

        var live = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        var orphan = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Disabled");
        resolver.Remember(live);

        var ghost = new ProcessIdentity(9001, Owner.StartedAtUtcTicks);

        await new MutationLedger(journal, resolver).ApplyAsync("alive", [live], Permit(), Owner);
        await new MutationLedger(journal, resolver).ApplyAsync("dead", [orphan], Permit(), ghost);

        await BootRecovery.RunAsync(
            new MutationLedger(journal.ReopenCold(), new FakeResolver(machine)),
            new SelectiveLiveness(Owner),
            default);

        Assert.Equal("Disabled", machine["service:WSearch"]);
    }

    [Fact]
    public async Task The_explanation_says_what_was_left_alone_and_claims_nothing_else()
    {
        // Charter Article VII. This is the only account anybody gets of what a LocalSystem
        // service decided at three in the morning.
        var (ledger, _, _) = await ArmedByAsync(Owner);

        var outcome = await BootRecovery.RunAsync(ledger, new StubLiveness(Owner), default);
        var text = outcome.Explain();

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("still running", text, StringComparison.OrdinalIgnoreCase);

        foreach (var claim in new[] { "faster", "fps", "boost", "improve", "optimis", "optimiz" })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Recovery_refuses_a_missing_liveness_source_rather_than_reverting_blindly()
    {
        var (ledger, _, _) = await ArmedByAsync(Owner);

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await BootRecovery.RunAsync(ledger, null!, default));
    }

    /// <summary>Answers one identity for every process id, or throws.</summary>
    private sealed class StubLiveness(ProcessIdentity? answer) : IProcessLiveness
    {
        public int Calls { get; private set; }

        /// <summary>Set to model the operating system refusing to answer.</summary>
        public Exception? Throws { get; init; }

        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken)
        {
            Calls++;

            if (Throws is not null)
            {
                throw Throws;
            }

            return ValueTask.FromResult(answer);
        }
    }

    /// <summary>Reports exactly one process as running, and every other id as gone.</summary>
    private sealed class SelectiveLiveness(ProcessIdentity running) : IProcessLiveness
    {
        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(processId == running.ProcessId ? running : null);
    }
}
