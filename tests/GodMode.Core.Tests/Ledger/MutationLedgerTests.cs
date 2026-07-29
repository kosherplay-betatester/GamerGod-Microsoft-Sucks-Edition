using GodMode.Core.Ledger;
using GodMode.Core.Mutations;
using GodMode.Core.Policy;
using GodMode.Core.Safety;
using Xunit;

namespace GodMode.Core.Tests.Ledger;

/// <summary>
/// The tests behind Charter Article VI. If these pass, "nothing we do survives a reboot
/// unless you said so" is an engineering property. If they do not, it is marketing.
/// </summary>
public sealed class MutationLedgerTests
{
    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate("Battlefield 6", new AntiCheatAssessment
        {
            Tier = AntiCheatTier.Kernel,
            Findings = [new AntiCheatFinding("EA Javelin", AntiCheatTier.Kernel, "service 'EAAntiCheatService'")],
        });

    private static MutationPermit FullAccess() =>
        GameIntegrityPolicy.Evaluate(
            "Cyberpunk 2077",
            new AntiCheatAssessment { Tier = AntiCheatTier.None, Findings = [] },
            ContactPreference.WhenProvablySafe);

    // ------------------------------------------------------------------
    // The headline property: any crash point recovers exactly.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Any_crash_point_recovers_the_machine_exactly()
    {
        const int Trials = 500;
        var random = new Random(20260729); // Seeded: a failure must be reproducible.

        for (var trial = 0; trial < Trials; trial++)
        {
            var machine = new FakeMachine();
            var resolver = new FakeResolver(machine);

            // Seed a machine with pre-existing state, so revert has to restore real values
            // rather than trivially clearing keys.
            var keys = new List<string>();
            var keyCount = random.Next(3, 12);
            for (var k = 0; k < keyCount; k++)
            {
                var key = $"key{k}";
                keys.Add(key);
                machine[key] = $"original-{k}";
            }

            var before = machine.Snapshot();

            var mutations = new List<IMutation>();
            var mutationCount = random.Next(1, 20);
            for (var m = 0; m < mutationCount; m++)
            {
                var mutation = new FakeMutation(
                    machine,
                    keys[random.Next(keys.Count)],
                    (MutationTier)(random.Next(0, 6) * 10),
                    $"changed-{trial}-{m}",
                    bootPersistent: random.Next(4) == 0);

                resolver.Remember(mutation);
                mutations.Add(mutation);
            }

            var durable = new InMemoryJournal();

            // Crash somewhere in the write sequence, including before anything lands and
            // after everything does.
            var crashAfter = random.Next(0, (mutationCount * 3) + 3);
            var crashing = new CrashingJournal(durable, crashAfter);

            try
            {
                await new MutationLedger(crashing, resolver)
                    .ApplyAsync($"s{trial}", mutations, AmbientOnly());
            }
            catch (SimulatedCrash)
            {
                // Expected for most trials.
            }

            // Cold recovery: a brand new ledger over only what reached durable storage.
            // Nothing is shared with the process that died.
            var recovery = new MutationLedger(durable.ReopenCold(), resolver);
            var report = await recovery.RevertAsync();

            Assert.True(
                machine.Matches(before),
                $"Trial {trial} (crashAfter={crashAfter}, mutations={mutationCount}) did not recover.\n"
                + $"  expected: {before.Describe()}\n"
                + $"  actual:   {machine.Describe()}");
            Assert.True(report.IsClean, $"Trial {trial} reported an unclean recovery.");
        }
    }

    [Fact]
    public async Task Recovery_is_idempotent_when_run_repeatedly()
    {
        // The boot-recovery pass may run against a journal that a previous pass already
        // handled, for instance if the machine lost power during recovery itself.
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        machine["service:WSearch"] = "Running";

        var before = machine.Snapshot();
        var journal = new InMemoryJournal();
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(mutation);

        await new MutationLedger(journal, resolver).ApplyAsync("s1", [mutation], AmbientOnly());
        Assert.Equal("Stopped", machine["service:WSearch"]);

        for (var pass = 0; pass < 4; pass++)
        {
            var report = await new MutationLedger(journal, resolver).RevertAsync();
            Assert.True(report.IsClean);
            Assert.True(machine.Matches(before));
        }
    }

    // ------------------------------------------------------------------
    // Ordering.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Revert_walks_descending_tier_so_the_shell_returns_first()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        var order = new List<string>();

        var journal = new InMemoryJournal();
        var mutations = new IMutation[]
        {
            new FakeMutation(machine, "power", MutationTier.Power, "high"),
            new FakeMutation(machine, "service", MutationTier.Service, "stopped"),
            new FakeMutation(machine, "shell", MutationTier.Shell, "down"),
            new FakeMutation(machine, "registry", MutationTier.Registry, "set"),
        };

        foreach (var m in mutations)
        {
            resolver.Remember(m);
        }

        await new MutationLedger(journal, resolver).ApplyAsync("s1", mutations, AmbientOnly());

        var recording = new RecordingResolver(resolver, order);
        await new MutationLedger(journal, recording).RevertAsync();

        // Shell (50) first, power (0) last: a user watching this sees a desktop return
        // before the slower service restarts have finished.
        Assert.Equal(["shell", "service", "registry", "power"], order);
    }

    [Fact]
    public async Task Apply_walks_ascending_tier()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();

        var mutations = new IMutation[]
        {
            new FakeMutation(machine, "shell", MutationTier.Shell, "down"),
            new FakeMutation(machine, "power", MutationTier.Power, "high"),
            new FakeMutation(machine, "service", MutationTier.Service, "stopped"),
        };

        await new MutationLedger(journal, resolver).ApplyAsync("s1", mutations, AmbientOnly());

        var applied = journal.Entries
            .Where(e => e.Op == JournalOp.Capture)
            .Select(e => e.Key)
            .ToArray();

        Assert.Equal(["power", "service", "shell"], applied);
    }

    // ------------------------------------------------------------------
    // Failure isolation.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_failing_revert_never_strands_the_rest_of_the_chain()
    {
        var machine = new FakeMachine();
        var resolver = new StubbornResolver(machine, stubbornKey: "service");
        machine["shell"] = "up";
        machine["service"] = "Running";
        machine["power"] = "balanced";

        var journal = new InMemoryJournal();
        var mutations = new IMutation[]
        {
            new FakeMutation(machine, "shell", MutationTier.Shell, "down"),
            new FakeMutation(machine, "service", MutationTier.Service, "Stopped"),
            new FakeMutation(machine, "power", MutationTier.Power, "high"),
        };

        await new MutationLedger(journal, resolver).ApplyAsync("s1", mutations, AmbientOnly());

        var report = await new MutationLedger(journal, resolver).RevertAsync();

        // The stubborn one is recorded as failed, and everything else still came back.
        Assert.False(report.IsClean);
        Assert.Single(report.Failed);
        Assert.Equal("service", report.Failed[0].Key);
        Assert.Equal("up", machine["shell"]);
        Assert.Equal("balanced", machine["power"]);
        Assert.Contains("shell", report.Reverted);
        Assert.Contains("power", report.Reverted);
    }

    [Fact]
    public async Task A_change_whose_apply_failed_is_still_reverted()
    {
        // Capture is journalled before apply precisely so a half-applied change is still
        // recoverable. This asserts that ordering actually pays off.
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        machine["registry"] = "original";

        var journal = new InMemoryJournal();
        var mutation = new FakeMutation(machine, "registry", MutationTier.Registry, "changed")
        {
            FailOnApply = true,
        };
        resolver.Remember(mutation);

        var apply = await new MutationLedger(journal, resolver).ApplyAsync("s1", [mutation], AmbientOnly());

        Assert.Single(apply.Failed);
        Assert.Contains(journal.Entries, e => e.Op == JournalOp.Capture && e.Key == "registry");

        var report = await new MutationLedger(journal, resolver).RevertAsync();

        Assert.True(report.IsClean);
        Assert.Equal("original", machine["registry"]);
    }

    [Fact]
    public async Task An_unresolvable_entry_is_reported_and_the_journal_stays_dirty()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        machine["mystery"] = "original";

        var journal = new InMemoryJournal();
        var mutation = new FakeMutation(machine, "mystery", MutationTier.Registry, "changed");
        resolver.Remember(mutation);

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("s1", [mutation], AmbientOnly());

        // Model a downgrade: the implementation that made the change no longer exists.
        resolver.Unresolvable.Add("mystery");

        var report = await ledger.RevertAsync();

        Assert.False(report.IsClean);
        Assert.Contains("mystery", report.Unresolvable);
        Assert.True(await ledger.HasOutstandingChangesAsync());
    }

    // ------------------------------------------------------------------
    // Policy is enforced at the ledger, not merely advised.
    // ------------------------------------------------------------------

    [Fact]
    public async Task The_ledger_refuses_contact_mutations_on_a_protected_title()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        machine["cpuset:bf6"] = "default";

        var journal = new InMemoryJournal();
        var contact = new FakeMutation(
            machine, "cpuset:bf6", MutationTier.CpuRouting, "D0", MutationVisibility.Contact);
        var ambient = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");

        var report = await new MutationLedger(journal, resolver)
            .ApplyAsync("s1", [contact, ambient], AmbientOnly());

        Assert.Contains("cpuset:bf6", report.Refused);
        Assert.Contains("service:WSearch", report.Applied);

        // The game-facing value was never touched, and nothing about it reached the journal.
        Assert.Equal("default", machine["cpuset:bf6"]);
        Assert.DoesNotContain(journal.Entries, e => e.Key == "cpuset:bf6");
    }

    [Fact]
    public async Task Contact_mutations_apply_when_the_policy_grants_them()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);

        var journal = new InMemoryJournal();
        var contact = new FakeMutation(
            machine, "cpuset:cp2077", MutationTier.CpuRouting, "D0", MutationVisibility.Contact);

        var report = await new MutationLedger(journal, resolver)
            .ApplyAsync("s1", [contact], FullAccess());

        Assert.Empty(report.Refused);
        Assert.Contains("cpuset:cp2077", report.Applied);
        Assert.Equal("D0", machine["cpuset:cp2077"]);
    }

    private sealed class RecordingResolver(IMutationResolver inner, List<string> order) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key)
        {
            order.Add(key);
            return inner.Resolve(mutationType, key);
        }
    }

    private sealed class StubbornResolver(FakeMachine machine, string stubbornKey) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key) =>
            new FakeMutation(machine, key, MutationTier.Registry, "x")
            {
                FailOnRevert = string.Equals(key, stubbornKey, StringComparison.Ordinal),
            };
    }
}

public sealed class SafetyGateTests
{
    private static readonly RestoreStatus Enabled = new()
    {
        Availability = RestoreAvailability.Enabled,
        ExistingPoints = 5,
        CreationFrequencyMinutes = 0,
    };

    private static readonly RestoreStatus Off = new()
    {
        Availability = RestoreAvailability.Disabled,
        Detail = "System Protection is off for C:",
    };

    private static IMutation Transient(string key) =>
        new FakeMutation(new FakeMachine(), key, MutationTier.Service, "stopped");

    private static IMutation Persistent(string key) =>
        new FakeMutation(new FakeMachine(), key, MutationTier.Registry, "set", bootPersistent: true);

    [Fact]
    public void A_session_that_cannot_outlive_a_reboot_needs_no_restore_point()
    {
        var decision = SafetyGate.Evaluate([Transient("a"), Transient("b")], Enabled);

        Assert.Equal(SafetyVerdict.Proceed, decision.Verdict);
        Assert.Empty(decision.BootPersistentKeys);
        Assert.Contains("No restore point is needed", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Persistent_changes_require_a_restore_point_first()
    {
        var decision = SafetyGate.Evaluate([Transient("a"), Persistent("irq:nic")], Enabled);

        Assert.Equal(SafetyVerdict.RequireRestorePoint, decision.Verdict);
        Assert.Equal(["irq:nic"], decision.BootPersistentKeys.ToArray());
        Assert.True(decision.MayProceed);
    }

    [Fact]
    public void Persistent_changes_are_blocked_when_protection_is_off_and_unacknowledged()
    {
        var decision = SafetyGate.Evaluate([Persistent("irq:nic")], Off);

        Assert.Equal(SafetyVerdict.Blocked, decision.Verdict);
        Assert.False(decision.MayProceed);
        Assert.Contains("System Protection is turned off", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_may_accept_the_risk_explicitly()
    {
        var decision = SafetyGate.Evaluate(
            [Persistent("irq:nic")], Off, userAcknowledgedNoRestorePoint: true);

        Assert.Equal(SafetyVerdict.ProceedWithoutRestorePoint, decision.Verdict);
        Assert.True(decision.MayProceed);

        // The remaining guarantee must be stated accurately, not glossed over.
        Assert.Contains("rebooting will still undo these changes", decision.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_changes_are_never_blocked_even_without_protection()
    {
        var decision = SafetyGate.Evaluate([Transient("service:WSearch")], Off);

        Assert.Equal(SafetyVerdict.Proceed, decision.Verdict);
        Assert.True(decision.MayProceed);
    }
}
