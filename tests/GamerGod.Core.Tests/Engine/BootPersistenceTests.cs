using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Tests.Hardware;
using GamerGod.Core.Mutations;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// Whether each mutation's <see cref="IMutation.IsBootPersistent"/> matches what it actually
/// changes.
///
/// <para>
/// This is the flag the whole restore guarantee turns on, and getting it wrong in the
/// permissive direction is unrecoverable. The ledger reads a non-boot-persistent capture from a
/// previous boot as "Windows already undid this" and drops it — so a change that <em>does</em>
/// survive a reboot, marked as one that does not, is applied to somebody's machine for ever
/// while every GamerGod surface reports that nothing is changed.
/// </para>
///
/// <para>
/// Written after exactly that shipped. <c>PowerSchemeMutation</c> declared false while its
/// apply called <c>SetActivePowerScheme</c>, which Windows stores in the registry. There was a
/// test asserting a boot-persistent entry gets reverted, and it used a hand-written journal
/// line — so it proved the ledger's arithmetic and never touched the question of whether any
/// real mutation had the right answer.
/// </para>
/// </summary>
public sealed class BootPersistenceTests
{
    private static CpuTopology Topology() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    [Fact]
    public void The_power_scheme_survives_a_reboot_and_says_so()
    {
        // powercfg /setactive writes the registry. That is the point of the command.
        var mutation = new PowerSchemeMutation(new FakeAmbientOperations(), "GamerGod");

        Assert.True(
            mutation.IsBootPersistent,
            "the active power scheme is persisted by Windows and must be restored after a reboot");
    }

    [Fact]
    public void Efficiency_mode_dies_with_the_process_and_says_so()
    {
        // EcoQoS is per-process state. The process is gone after a restart, and so is this.
        var mutation = new EfficiencyModeMutation(new FakeAmbientOperations(), []);

        Assert.False(mutation.IsBootPersistent);
    }

    [Fact]
    public void Processor_affinity_dies_with_the_process_and_says_so()
    {
        // Re-applying a captured mask after a reboot would write it onto whichever process
        // inherited the id, which is the reason this flag exists at all.
        var mutation = new AffinityConfinementMutation(
            new FakeAmbientOperations(), default, [], Topology());

        Assert.False(mutation.IsBootPersistent);
    }

    [Fact]
    public void A_suspended_service_restarts_with_the_machine_and_says_so()
    {
        var mutation = new ServiceSuspensionMutation(new FakeAmbientOperations(), "WSearch");

        Assert.False(mutation.IsBootPersistent);
    }

    [Fact]
    public void Every_shipped_mutation_has_been_considered()
    {
        // A tripwire rather than a coverage metric. The named assertions above are the real
        // tests; this fails when somebody adds a fifth mutation, so the question gets asked
        // once rather than defaulting to whatever the first draft happened to write.
        var mutations = typeof(IMutation).Assembly
            .GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IMutation).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AffinityConfinementMutation",
                "DomainConfinementMutation",
                "EfficiencyModeMutation",
                "PowerSchemeMutation",
                "ReleasedConfinementMutation",
                "ServiceSuspensionMutation",
            ],
            mutations);
    }
}
