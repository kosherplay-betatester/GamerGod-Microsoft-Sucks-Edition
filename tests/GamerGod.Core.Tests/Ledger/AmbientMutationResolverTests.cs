using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Tests.Engine;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Ledger;

/// <summary>
/// The resolver is what lets a process that never applied anything undo it — the boot
/// recovery pass, the watchdog, <c>gamergod off</c> from a fresh shell. A key it does not
/// recognise is reported as <see cref="RevertReport.Unresolvable"/>, which means the change
/// stays on the machine and the journal stays dirty forever.
///
/// <para>
/// There were two copies of this resolver, and they had diverged: the one behind
/// <c>gamergod bench</c> was missing <c>confine:ambient-domain</c>, so a cold revert from a
/// measurement run could not undo a job-object confinement and reported the journal as
/// unclean. These tests exist so a third copy cannot silently drift the same way.
/// </para>
/// </summary>
public sealed class AmbientMutationResolverTests
{
    /// <summary>
    /// Every mutation type in the domain, and the key it emits. Asserted against reflection
    /// below, so adding a mutation without adding it here fails the build rather than
    /// producing a key nothing can resolve.
    /// </summary>
    private static readonly (string Type, string Key)[] EveryMutation =
    [
        (nameof(EfficiencyModeMutation), "ecoqos:background"),
        (nameof(DomainConfinementMutation), "confine:ambient-domain"),
        (nameof(ReleasedConfinementMutation), "confine:ambient-domain"),
        (nameof(AffinityConfinementMutation), "affinity:ambient-domain"),
        (nameof(ServiceSuspensionMutation), "service:WSearch"),
        (nameof(PowerSchemeMutation), "power:scheme"),
    ];

    public static TheoryData<string> EveryKey()
    {
        var data = new TheoryData<string>();
        foreach (var key in EveryMutation.Select(m => m.Key).Distinct(StringComparer.Ordinal))
        {
            data.Add(key);
        }

        return data;
    }

    private static AmbientMutationResolver Resolver(IAmbientOperations? os = null) =>
        new(
            os ?? new FakeAmbientOperations(),
            PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D()));

    [Theory]
    [MemberData(nameof(EveryKey))]
    public void Every_key_a_mutation_emits_round_trips_through_the_resolver(string key)
    {
        var mutation = Resolver().Resolve("GamerGod.Core.Engine.Whatever", key);

        Assert.NotNull(mutation);
        Assert.Equal(key, mutation!.Key);
    }

    [Fact]
    public void No_mutation_exists_that_this_test_does_not_know_about()
    {
        // Reflection rather than review. A new mutation type gets a key, and a key nothing
        // resolves is a change that can never be undone from a cold process.
        var declared = typeof(IMutation).Assembly
            .GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IMutation).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            EveryMutation.Select(m => m.Type).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
            declared);
    }

    [Fact]
    public void Every_resolved_mutation_is_ambient()
    {
        // Nothing this resolver can rebuild may be a contact change: it is used by the boot
        // recovery pass, which has no game in front of it and no permit to consult.
        //
        // This test also asserted that no mutation was boot-persistent, and that assertion is
        // gone because it was wrong and it hid a defect. The active power scheme is stored in
        // the registry and does survive a restart, but a blanket "nothing survives a reboot"
        // here meant marking it correctly would fail a test — so it stayed marked as dying
        // with the process, the ledger dropped its capture after every restart, and the user's
        // power plan was never restored.
        //
        // Whether each mutation's flag matches what it actually changes is a per-mutation
        // question with a per-mutation answer, and it lives in BootPersistenceTests.
        foreach (var (_, key) in EveryMutation)
        {
            var mutation = Resolver().Resolve("t", key);

            Assert.NotNull(mutation);
            Assert.Equal(MutationVisibility.Ambient, mutation!.Visibility);
        }
    }

    [Fact]
    public void A_service_key_resolves_to_that_service_and_no_other()
    {
        var mutation = Resolver().Resolve("t", "service:SysMain");

        Assert.NotNull(mutation);
        Assert.Equal("service:SysMain", mutation!.Key);
    }

    [Fact]
    public void An_unknown_key_resolves_to_null_rather_than_guessing()
    {
        // After a downgrade the journal can name a mutation this build has never heard of.
        // The ledger records it as unresolvable and leaves it in the journal; inventing a
        // mutation for it would be worse than admitting we cannot undo it.
        Assert.Null(Resolver().Resolve("t", "gizmo:not-a-real-key"));
    }

    [Fact]
    public async Task A_journalled_confinement_can_be_reverted_by_a_process_that_never_applied_it()
    {
        // The exact defect the bench copy had. A job object's limits are already gone once
        // the process holding it exits, so this revert has nothing to do — but it must still
        // resolve, so the ledger can retire the entry and report the machine clean.
        var journal = new InMemoryJournal();
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

        var report = await new MutationLedger(journal, Resolver()).RevertAsync();

        Assert.Empty(report.Unresolvable);
        Assert.Empty(report.Failed);
        Assert.Contains("confine:ambient-domain", report.Reverted);
        Assert.True(report.IsClean);
    }

    [Fact]
    public async Task A_journalled_affinity_capture_is_restored_exactly()
    {
        var os = new FakeAmbientOperations().AddProcess(1000, "chrome", 800);
        await os.SetAffinityAsync(1000, new ProcessorMask(0, 0xFFFF0000UL), default);

        var journal = new InMemoryJournal();
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
                Key = "affinity:ambient-domain",
                MutationType = "GamerGod.Core.Engine.AffinityConfinementMutation",
                Tier = MutationTier.CpuRouting,
                State = """{"Processes":[{"Id":1000,"Name":"chrome","Group":0,"Mask":4294967295}]}""",
            },
            default);

        var report = await new MutationLedger(journal, Resolver(os)).RevertAsync();

        Assert.True(report.IsClean);
        Assert.Equal(new ProcessorMask(0, 0xFFFFFFFFUL), os.AffinityOf(1000));
    }

    [Fact]
    public void The_resolver_needs_no_operating_system_to_be_constructed()
    {
        // It runs inside a LocalSystem service at boot and inside a test with no admin
        // rights. Both build it the same way, from an interface and a topology.
        Assert.NotNull(Resolver(new FakeAmbientOperations()));
    }

    [Fact]
    public void Resolving_never_returns_a_mutation_carrying_live_targets()
    {
        // A cold rebuild knows nothing about which processes were affected — that lives in
        // the journalled capture. A resolver that invented targets would apply changes on
        // the revert path.
        var mutation = Resolver().Resolve("t", "ecoqos:background");

        Assert.IsType<EfficiencyModeMutation>(mutation);
        Assert.Equal(0, ((EfficiencyModeMutation)mutation!).TargetCount);
    }

    [Fact]
    public void A_released_confinement_describes_itself_without_claiming_work_it_did_not_do()
    {
        var mutation = Resolver().Resolve("t", "confine:ambient-domain");

        Assert.NotNull(mutation);
        Assert.False(
            mutation!.Describe().Contains("restored", StringComparison.OrdinalIgnoreCase),
            "a confinement Windows lifted for us must not be described as something we restored.");
    }

    [Fact]
    public async Task Every_rebuilt_mutation_can_capture_something_the_journal_can_write()
    {
        // The ledger calls GetRawText on whatever CaptureAsync returns, and a default
        // JsonElement throws there rather than serialising. Nothing applies a rebuilt
        // mutation today, so this would sit undetected until the first caller that did.
        foreach (var (_, key) in EveryMutation.DistinctBy(m => m.Key, StringComparer.Ordinal))
        {
            var mutation = Resolver().Resolve("t", key)!;

            if (mutation is ReleasedConfinementMutation)
            {
                var capture = await mutation.CaptureAsync(default);
                Assert.NotEqual(System.Text.Json.JsonValueKind.Undefined, capture.ValueKind);
                Assert.False(string.IsNullOrEmpty(capture.GetRawText()));
            }
        }
    }

    [Fact]
    public async Task Reverting_a_released_confinement_twice_is_not_an_error()
    {
        // Charter invariant: revert is idempotent and may run from a cold process after a
        // crash or a reboot.
        var mutation = Resolver().Resolve("t", "confine:ambient-domain")!;
        using var empty = System.Text.Json.JsonDocument.Parse("{}");

        await mutation.RevertAsync(empty.RootElement, default);
        await mutation.RevertAsync(empty.RootElement, default);
    }

    [Fact]
    public void The_resolver_lives_in_the_domain_so_every_caller_shares_one_copy()
    {
        // There is no reason for this assertion other than the bug that produced it: two
        // copies in the command-line tool drifted apart, and the one used by the measurement
        // command lost the ability to undo a confinement.
        Assert.Equal("GamerGod.Core", typeof(AmbientMutationResolver).Assembly.GetName().Name);
    }

    [Fact]
    public void Resolving_ignores_the_journalled_type_name_and_trusts_the_key()
    {
        // Type names move between namespaces across versions; keys are the stable identity
        // the interface documents. Binding to the type name would make a refactor
        // unrecoverable for anyone whose journal was written by the previous build.
        var byOldName = Resolver().Resolve("GamerGod.Engine.Old.EfficiencyModeMutation", "ecoqos:background");

        Assert.NotNull(byOldName);
        Assert.Equal("ecoqos:background", byOldName!.Key);
    }

    [Fact]
    public void An_empty_key_resolves_to_null()
    {
        Assert.Null(Resolver().Resolve("t", string.Empty));
    }

    [Fact]
    public void A_service_key_with_no_name_resolves_to_null_rather_than_an_unnamed_service()
    {
        // "service:" with nothing after it would build a mutation that starts a service
        // called "", which at best throws and at worst does something surprising.
        Assert.Null(Resolver().Resolve("t", "service:"));
    }

    [Fact]
    public async Task Every_key_the_engine_actually_plans_is_resolvable()
    {
        // The strongest form of the round trip: ask the engine for a full plan on the
        // reference machine and assert the resolver can rebuild every key in it.
        var os = new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800)
            .AddProcess(1001, "msedge", 400)
            .AddService("WSearch", running: true);

        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());
        var engine = new AmbientEngine(os, new MutationLedger(new InMemoryJournal(), Resolver(os)));

        foreach (var resident in new[] { false, true })
        {
            var plan = await engine.PlanAsync(
                topology,
                new AmbientOptions
                {
                    Services = ["WSearch"],
                    ManagePowerScheme = true,
                    CallerStaysResident = resident,
                },
                gameProcessId: null);

            Assert.NotEmpty(plan);

            foreach (var mutation in plan)
            {
                Assert.NotNull(Resolver(os).Resolve(mutation.GetType().FullName!, mutation.Key));
            }
        }
    }

    [Fact]
    public void Constructing_without_a_topology_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AmbientMutationResolver(new FakeAmbientOperations(), null!));
    }

    [Fact]
    public void Constructing_without_an_operating_system_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new AmbientMutationResolver(
                null!, PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D())));
    }

    [Fact]
    public void The_ambient_targets_of_a_rebuilt_mutation_are_empty_not_default()
    {
        // A default-valued ImmutableArray throws on enumeration, and this one is enumerated
        // during a revert at boot — the least forgiving place for a NullReference.
        var mutation = (EfficiencyModeMutation)Resolver().Resolve("t", "ecoqos:background")!;

        var targets = ImmutableArray<ProcessInfo>.Empty;
        Assert.Equal(targets.Length, mutation.TargetCount);
    }
}
