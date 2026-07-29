using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Core.Tests.Hardware;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// The first code in the project that changes a real machine, driven end to end against a
/// simulated one.
/// </summary>
public sealed class AmbientEngineTests
{
    private static CpuTopology Reference() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    private static MutationPermit ProtectedTitle() =>
        GameIntegrityPolicy.Evaluate(
            "Battlefield 6",
            new AntiCheatAssessment
            {
                Tier = AntiCheatTier.Kernel,
                Findings = [new AntiCheatFinding("EA Javelin", AntiCheatTier.Kernel, "service")],
            });

    private static readonly RestoreStatus ProtectionOn = new()
    {
        Availability = RestoreAvailability.Enabled,
        ExistingPoints = 5,
    };

    private static FakeAmbientOperations BusyDesktop() =>
        new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800)
            .AddProcess(1001, "msedge", 400)
            .AddProcess(1002, "Discord", 300)
            .AddProcess(1003, "Spotify", 150)
            .AddProcess(2000, "bf6", 6000)          // the game
            .AddProcess(3000, "iCUE", 120)          // thermal - must be untouched
            .AddProcess(3001, "csrss", 90)          // system critical - must be untouched
            .AddProcess(3002, "vrserver", 200)      // VR runtime - must be untouched
            .AddProcess(4000, "tinyhelper", 4);     // too small to bother with

    private static (AmbientEngine Engine, FakeAmbientOperations Os, InMemoryJournal Journal, FakeMachine Machine)
        Build(FakeAmbientOperations? os = null)
    {
        var operations = os ?? BusyDesktop();
        var journal = new InMemoryJournal();
        var machine = new FakeMachine();
        var ledger = new MutationLedger(journal, new NullResolver());
        return (new AmbientEngine(operations, ledger), operations, journal, machine);
    }

    // ------------------------------------------------------------------
    // Planning.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Protected_software_is_never_targeted()
    {
        var (engine, os, _, _) = Build();

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        // Thermal, system-critical and VR software must be untouched, whatever else happened.
        Assert.False(os.IsEfficient(3000), "iCUE was demoted - that is the thermal risk.");
        Assert.False(os.IsEfficient(3001), "csrss was demoted.");
        Assert.False(os.IsEfficient(3002), "the VR runtime was demoted.");

        // And none of them were moved off the game's cores either.
        foreach (var id in new[] { 3000, 3001, 3002 })
        {
            Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(id).Mask);
        }
    }

    [Fact]
    public async Task The_game_itself_is_never_touched()
    {
        var (engine, os, journal, _) = Build();

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        Assert.False(os.IsEfficient(2000));
        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(2000).Mask);

        // And nothing in the journal names it, so no record exists of us having considered it.
        Assert.DoesNotContain(journal.Entries, e => e.Key.Contains("2000", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trivial_processes_are_left_alone()
    {
        // Demoting a 4 MB helper costs a system call and gains nothing measurable.
        var (engine, os, _, _) = Build();

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        Assert.False(os.IsEfficient(4000));
    }

    [Fact]
    public async Task Every_planned_mutation_is_ambient()
    {
        // The engine must never emit a mutation that a protected title would refuse, which
        // is why it needs no special handling for anti-cheat.
        var (engine, _, _, _) = Build();

        var plan = await engine.PlanAsync(
            Reference(),
            new AmbientOptions
            {
                Services = ["WSearch", "SysMain"],
                ManagePowerScheme = true,
            },
            gameProcessId: 2000);

        Assert.NotEmpty(plan);
        Assert.All(plan, m => Assert.Equal(MutationVisibility.Ambient, m.Visibility));
        Assert.All(plan, m => Assert.False(m.IsBootPersistent));
    }

    [Fact]
    public async Task A_protected_service_in_a_profile_is_dropped_rather_than_applied()
    {
        var (engine, _, _, _) = Build();

        var plan = await engine.PlanAsync(
            Reference(),
            new AmbientOptions { Services = ["WSearch", "PlugPlay", "Audiosrv", "SysMain"] },
            gameProcessId: null);

        var services = plan.Select(m => m.Key).Where(k => k.StartsWith("service:", StringComparison.Ordinal));

        Assert.Equal(["service:WSearch", "service:SysMain"], services.ToArray());
    }

    // ------------------------------------------------------------------
    // Applying and reverting.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Entering_demotes_background_work_and_clears_the_game_domain()
    {
        var (engine, os, _, _) = Build();

        var receipt = await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        Assert.True(os.IsEfficient(1000));
        Assert.True(os.IsEfficient(1001));
        Assert.True(receipt.Partitioned);

        // Confined to the 32 MB domain: logical processors 16-31.
        Assert.Equal(0x00000000FFFF0000UL, os.AffinityOf(1000).Mask);

        // Three, not four. Discord is on the communication denylist - suspending it drops an
        // active voice call, and being dropped out of comms mid-match is not a trade anybody
        // would accept for a few frames.
        Assert.Equal(3, receipt.ProcessesDemoted);
        Assert.Equal(3, receipt.ProcessesConfined);
        Assert.False(os.IsEfficient(1002), "Discord was demoted despite being protected.");
    }

    [Fact]
    public async Task Exiting_restores_everything()
    {
        var (engine, os, journal, _) = Build();
        var ledger = new MutationLedger(journal, new AmbientResolver(os));

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        Assert.True(os.IsEfficient(1000));

        var report = await new AmbientEngine(os, ledger).ExitAsync();

        Assert.True(report.IsClean);
        Assert.False(os.IsEfficient(1000));
        Assert.False(os.IsEfficient(1001));
        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(1000).Mask);
    }

    [Fact]
    public async Task A_process_already_in_efficiency_mode_is_left_that_way()
    {
        // Restoring it to "normal" would be a change of its own, dressed up as a revert.
        var os = BusyDesktop();
        os.Processes.Add(new ProcessInfo(1500, "AlreadyQuiet", 200 * 1024 * 1024));
        await os.SetEfficiencyModeAsync(1500, true, default);

        var journal = new InMemoryJournal();
        var engine = new AmbientEngine(os, new MutationLedger(journal, new NullResolver()));

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        await new AmbientEngine(os, new MutationLedger(journal, new AmbientResolver(os))).ExitAsync();

        Assert.True(os.IsEfficient(1500));
        Assert.False(os.IsEfficient(1000));
    }

    [Fact]
    public async Task Services_are_stopped_and_restarted_only_if_they_were_running()
    {
        var os = BusyDesktop()
            .AddService("WSearch", running: true)
            .AddService("SysMain", running: false);

        var journal = new InMemoryJournal();
        var engine = new AmbientEngine(os, new MutationLedger(journal, new NullResolver()));

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(),
            new AmbientOptions { Services = ["WSearch", "SysMain"] },
            ProtectionOn, gameProcessId: 2000);

        Assert.False(os.IsServiceRunning("WSearch"));

        await new AmbientEngine(os, new MutationLedger(journal, new AmbientResolver(os))).ExitAsync();

        Assert.True(os.IsServiceRunning("WSearch"));

        // SysMain was already stopped, so starting it would be a change rather than a revert.
        Assert.False(os.IsServiceRunning("SysMain"));
        Assert.DoesNotContain("start:SysMain", os.Log);
    }

    [Fact]
    public async Task The_power_scheme_is_duplicated_never_edited_and_the_original_comes_back()
    {
        var os = BusyDesktop();
        var original = os.ActiveScheme;

        var journal = new InMemoryJournal();
        var engine = new AmbientEngine(os, new MutationLedger(journal, new NullResolver()));

        await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(),
            new AmbientOptions { ManagePowerScheme = true }, ProtectionOn, gameProcessId: 2000);

        Assert.NotEqual(original, os.ActiveScheme);
        Assert.Equal(1, os.SchemeCount);

        await new AmbientEngine(os, new MutationLedger(journal, new AmbientResolver(os))).ExitAsync();

        Assert.Equal(original, os.ActiveScheme);
    }

    // ------------------------------------------------------------------
    // Failure paths.
    // ------------------------------------------------------------------

    [Fact]
    public async Task A_process_that_exits_mid_session_does_not_fail_the_session()
    {
        // Ordinary desktop churn. Abandoning a session because Notepad closed would be absurd.
        var os = BusyDesktop();
        os.FailEfficiencyFor.Add(1002);

        var journal = new InMemoryJournal();
        var engine = new AmbientEngine(os, new MutationLedger(journal, new NullResolver()));

        var receipt = await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(), new AmbientOptions(), ProtectionOn, gameProcessId: 2000);

        Assert.Empty(receipt.Failed);
        Assert.True(os.IsEfficient(1000));
        Assert.True(receipt.Partitioned);
    }

    [Fact]
    public async Task A_service_that_refuses_to_stop_is_recorded_and_the_rest_proceeds()
    {
        var os = BusyDesktop().AddService("WSearch", true).AddService("SysMain", true);
        os.FailStopFor.Add("WSearch");

        var journal = new InMemoryJournal();
        var engine = new AmbientEngine(os, new MutationLedger(journal, new NullResolver()));

        var receipt = await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(),
            new AmbientOptions { Services = ["WSearch", "SysMain"] },
            ProtectionOn, gameProcessId: 2000);

        Assert.Single(receipt.Failed);
        Assert.Equal("service:WSearch", receipt.Failed[0].Key);
        Assert.False(os.IsServiceRunning("SysMain"));
        Assert.True(receipt.Partitioned);
    }

    [Fact]
    public async Task Confinement_is_refused_outright_when_it_would_strand_windows()
    {
        // A topology whose ambient side is too small to hold Windows is reported as
        // unpartitionable, so no confinement mutation is planned at all.
        var single = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7800X3D());
        var (engine, _, _, _) = Build();

        var plan = await engine.PlanAsync(single, new AmbientOptions(), gameProcessId: 2000);

        Assert.False(single.CanPartition);
        Assert.DoesNotContain(plan, m => m.Key == "confine:ambient-domain");

        // Everything else still applies - a machine that cannot be partitioned is not a
        // machine GamerGod has nothing to offer.
        Assert.Contains(plan, m => m.Key == "ecoqos:background");
    }

    [Fact]
    public async Task A_dry_run_changes_nothing()
    {
        var (engine, os, journal, _) = Build();

        var receipt = await engine.EnterAsync(
            "s1", Reference(), ProtectedTitle(),
            new AmbientOptions { DryRun = true }, ProtectionOn, gameProcessId: 2000);

        Assert.Empty(os.Log);
        Assert.Empty(journal.Entries);
        Assert.False(os.IsEfficient(1000));
        Assert.Contains("Dry run", receipt.IntegritySummary, StringComparison.Ordinal);

        // And it reports what WOULD happen. A preview that says "0 processes" cannot answer
        // the only question the user is asking before they commit.
        Assert.Equal(3, receipt.ProcessesDemoted);
        Assert.Equal(3, receipt.ProcessesConfined);
        Assert.True(receipt.Partitioned);
    }

    [Fact]
    public void The_receipt_never_claims_a_performance_gain()
    {
        // Charter Article VII. The receipt says what was done; what it gained belongs to the
        // measurement harness and nowhere else.
        var receipt = new SessionReceipt
        {
            SessionId = "s1",
            Applied = ["ecoqos:background"],
            Refused = [],
            Failed = [],
            IntegritySummary = "Ambient only",
            ProcessesDemoted = 12,
            ProcessesConfined = 12,
            Partitioned = true,
        };

        var text = receipt.Explain();

        foreach (var claim in new[] { "faster", "fps", "%", "improve", "boost", "gain", "smoother" })
        {
            Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_idle_machine_is_told_plainly_that_nothing_was_needed()
    {
        var receipt = new SessionReceipt
        {
            SessionId = "s1",
            Applied = [],
            Refused = [],
            Failed = [],
            IntegritySummary = "Ambient only",
        };

        Assert.Contains("Nothing needed changing", receipt.Explain(), StringComparison.Ordinal);
    }

    /// <summary>Used where the test never reverts, so nothing needs rebuilding.</summary>
    private sealed class NullResolver : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key) => null;
    }

    /// <summary>
    /// Rebuilds ambient mutations for a cold revert, standing in for the real registry that
    /// maps a journalled type name back to an implementation.
    /// </summary>
    private sealed class AmbientResolver(FakeAmbientOperations os) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key)
        {
            if (key.StartsWith("service:", StringComparison.Ordinal))
            {
                return new ServiceSuspensionMutation(os, key["service:".Length..]);
            }

            return key switch
            {
                "ecoqos:background" => new EfficiencyModeMutation(os, ImmutableArray<ProcessInfo>.Empty),
                "power:scheme" => new PowerSchemeMutation(os, "GamerGod"),
                "confine:ambient-domain" => new ColdConfinementRevert(os),

                // Affinity outlives the process that set it, so the captured masks in the
                // journal are what put it back.
                "affinity:ambient-domain" => new AffinityConfinementMutation(
                    os,
                    default,
                    ImmutableArray<ProcessInfo>.Empty,
                    PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D())),
                _ => null,
            };
        }
    }

    /// <summary>
    /// Models a cold recovery of a confinement: the process that held the job object is
    /// gone, so Windows already lifted the limits. Nothing to undo, and saying so is correct.
    /// </summary>
    private sealed class ColdConfinementRevert(FakeAmbientOperations os) : IMutation
    {
        public string Key => "confine:ambient-domain";

        public MutationTier Tier => MutationTier.ProcessDemotion;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => false;

        public string Describe() => "release ambient confinement";

        public ValueTask<System.Text.Json.JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

        public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async ValueTask RevertAsync(
            System.Text.Json.JsonElement capture, CancellationToken cancellationToken)
        {
            if (os.ActiveConfinement is { } confinement)
            {
                await confinement.DisposeAsync();
            }
        }
    }
}
