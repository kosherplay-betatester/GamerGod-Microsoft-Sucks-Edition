using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// What the restore-point gate is allowed to stop.
///
/// <para>
/// It exists for one thing: a change that outlives a reboot must not be made on a machine with
/// System Protection turned off, because the journal is then the only way back and there is no
/// second net under it. That is worth blocking.
/// </para>
///
/// <para>
/// It is not worth blocking anything else, and it used to block everything else. The power lever
/// is boot-persistent — the active scheme is a registry value — System Protection is off on most
/// gaming installs, so the gate blocked, and the engine discarded the confinement and the
/// efficiency demotion along with it. Those are Ambient, they die with the processes that carry
/// them, and the gate was never worried about them.
/// </para>
///
/// <para>
/// The user-visible result was that Game Mode could not be turned on at all: the command reported
/// success, applied nothing, and the switch in the app flipped straight back to off because the
/// journal was empty. Found on a real machine, from the report "when i turn it on it immediately
/// turns off".
/// </para>
/// </summary>
public sealed class SafetyGateScopeTests
{
    /// <summary>An ordinary gaming install: System Protection off, so no restore point.</summary>
    private static RestoreStatus NoProtection() => new()
    {
        Availability = RestoreAvailability.Disabled,
        Detail = "System Protection is off for this drive",
    };

    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    private static (FakeAmbientOperations Os, AmbientEngine Engine, InMemoryJournal Journal, CpuTopology Topology)
        Machine()
    {
        var os = new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800)
            .AddProcess(1001, "msedge", 400)
            .AddProcess(1002, "discord", 300);

        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());
        var journal = new InMemoryJournal();
        var ledger = new MutationLedger(journal, new AmbientMutationResolver(os, topology));

        return (os, new AmbientEngine(os, ledger), journal, topology);
    }

    private static AmbientOptions WithPower() => new()
    {
        ConfineToAmbientDomain = true,
        DemoteToEfficiencyMode = true,
        ManagePowerScheme = true,
        Services = [],
        CallerStaysResident = false,
    };

    [Fact]
    public async Task The_ambient_levers_still_apply_when_the_power_lever_is_refused()
    {
        var (os, engine, _, topology) = Machine();

        var receipt = await engine.EnterAsync(
            "s1", topology, AmbientOnly(), WithPower(), NoProtection());

        // The thing the whole product does still happened.
        Assert.Contains("affinity:ambient-domain", receipt.Applied);
        Assert.Contains("ecoqos:background", receipt.Applied);
        Assert.True(receipt.Partitioned);
        Assert.Equal(topology.AmbientMask, os.AffinityOf(1000));
    }

    [Fact]
    public async Task The_power_lever_itself_is_refused_and_named()
    {
        var (os, engine, _, topology) = Machine();

        var receipt = await engine.EnterAsync(
            "s1", topology, AmbientOnly(), WithPower(), NoProtection());

        Assert.Contains("power:scheme", receipt.Refused);
        Assert.DoesNotContain("power:scheme", receipt.Applied);

        // And nothing touched the machine's power configuration.
        Assert.Equal(0, os.SchemeCount);
    }

    [Fact]
    public async Task The_receipt_does_not_claim_nothing_needed_changing()
    {
        // The sentence a user actually saw over a machine that had just refused to do anything.
        var (_, engine, _, topology) = Machine();

        var receipt = await engine.EnterAsync(
            "s1", topology, AmbientOnly(), WithPower(), NoProtection());

        var explained = receipt.Explain();

        Assert.DoesNotContain("Nothing needed changing", explained, StringComparison.Ordinal);
        Assert.Contains("refused", explained, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("power:scheme", explained, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_journal_records_the_session_so_the_switch_stays_on()
    {
        // The symptom, stated as the ledger sees it. An empty journal is what made the app read
        // the machine as unarmed a moment after arming it.
        var (_, engine, journal, topology) = Machine();

        await engine.EnterAsync("s1", topology, AmbientOnly(), WithPower(), NoProtection());

        Assert.True(await new MutationLedger(journal, new NoResolver()).HasOutstandingChangesAsync());
    }

    [Fact]
    public async Task A_session_that_is_only_boot_persistent_work_still_applies_nothing()
    {
        // The guard must not have become a bypass: with nothing left after the refusal, the gate
        // still wins and the machine is untouched.
        var (os, engine, journal, topology) = Machine();

        var receipt = await engine.EnterAsync(
            "s1",
            topology,
            AmbientOnly(),
            new AmbientOptions
            {
                ConfineToAmbientDomain = false,
                DemoteToEfficiencyMode = false,
                ManagePowerScheme = true,
                Services = [],
                CallerStaysResident = false,
            },
            NoProtection());

        Assert.Empty(receipt.Applied);
        Assert.Contains("power:scheme", receipt.Refused);
        Assert.Equal(0, os.SchemeCount);
        Assert.False(await new MutationLedger(journal, new NoResolver()).HasOutstandingChangesAsync());
    }

    [Fact]
    public async Task With_protection_available_nothing_is_refused_at_all()
    {
        var (_, engine, _, topology) = Machine();

        var receipt = await engine.EnterAsync(
            "s1",
            topology,
            AmbientOnly(),
            WithPower(),
            new RestoreStatus { Availability = RestoreAvailability.Enabled, Detail = "ready" });

        Assert.Empty(receipt.Refused);
        Assert.Contains("power:scheme", receipt.Applied);
    }

    private sealed class NoResolver : IMutationResolver
    {
        public GamerGod.Core.Mutations.IMutation? Resolve(string mutationType, string key) => null;
    }
}
