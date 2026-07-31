using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// Affinity above 64 logical processors, where Windows splits the machine into processor
/// groups and a bare 64-bit mask stops being an address.
///
/// <para>
/// Bit 3 of group 0 and bit 3 of group 1 are different processors. Windows applies a mask
/// relative to the group the process already belongs to, so writing the game group's mask
/// onto a process in another group moves it to cores nobody chose — and journals a restore
/// point that process never had. Revert-exactly is the whole guarantee, and this is the one
/// machine class where it silently failed.
/// </para>
///
/// <para>
/// Invisible on the 32-thread reference machine, which is precisely why it needs a synthetic
/// one. Nobody finds this by using GamerGod; you need a Threadripper or a dual-socket Xeon.
/// </para>
/// </summary>
public sealed class MultiGroupAffinityTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>A 48-core / 96-thread part: two processor groups, two cache domains each.</summary>
    private static CpuTopology DualGroup()
    {
        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();

        foreach (ushort group in (ushort[])[0, 1])
        {
            for (var core = 0; core < 24; core++)
            {
                cores.Add(new PhysicalCore(
                    ProcessorMask.FromLogicalProcessors(group, core * 2, (core * 2) + 1),
                    EfficiencyClass: 0));
            }
        }

        return PerformanceDomainClassifier.Classify(new ProcessorSnapshot
        {
            ProcessorName = "Dual-group workstation, 48C/96T",
            Cores = cores.ToImmutable(),
            Caches =
            [
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 0, 23)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 24, 47)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(1, 0, 23)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(1, 24, 47)),
            ],
        });
    }

    private static ushort OtherGroup(CpuTopology topology) =>
        (ushort)(topology.AmbientMask.Group == 0 ? 1 : 0);

    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>
    /// Two background processes in the game's group and one in the other group, each with a
    /// distinct starting mask so a wrong restore is unambiguous.
    /// </summary>
    private static FakeAmbientOperations SplitDesktop(CpuTopology topology)
    {
        var here = topology.AmbientMask.Group;
        var there = OtherGroup(topology);

        var os = new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800)
            .AddProcess(1001, "msedge", 400)
            .AddProcess(1002, "backup", 300);

        os.PlaceInGroup(1000, here, 0x0000FFFFFFFFFFFFUL);
        os.PlaceInGroup(1001, here, 0x0000_0000_0000_00FFUL);
        os.PlaceInGroup(1002, there, 0x0000FFFFFFFFFFFFUL);

        return os;
    }

    [Fact]
    public async Task Background_work_in_another_group_never_receives_the_game_groups_mask()
    {
        var topology = DualGroup();
        var os = SplitDesktop(topology);
        var before = os.AffinityOf(1002);

        var mutation = new AffinityConfinementMutation(
            os, topology.AmbientMask, [.. os.Processes], topology);

        await mutation.CaptureAsync(default);
        await mutation.ApplyAsync(default);

        Assert.Equal(topology.AmbientMask, os.AffinityOf(1000));
        Assert.Equal(topology.AmbientMask, os.AffinityOf(1001));

        // Untouched, because there is nothing meaningful GamerGod could write here: the
        // ambient mask names processors in a group this process does not run in.
        Assert.Equal(before, os.AffinityOf(1002));
    }

    [Fact]
    public async Task The_capture_records_the_group_each_process_was_actually_in()
    {
        // The defect this replaces hard-coded group 0. On a machine with more than 64
        // logical processors that journals a restore point for the wrong processors.
        var topology = DualGroup();
        var os = SplitDesktop(topology);

        var capture = await new AffinityConfinementMutation(
                os, topology.AmbientMask, [.. os.Processes], topology)
            .CaptureAsync(default);

        var records = capture.GetProperty("Processes");

        Assert.All(
            records.EnumerateArray(),
            r => Assert.Equal(topology.AmbientMask.Group, r.GetProperty("Group").GetUInt16()));

        // And nothing from the other group was captured, because nothing there will change.
        Assert.DoesNotContain(
            records.EnumerateArray().Select(r => r.GetProperty("Id").GetInt32()),
            id => id == 1002);
    }

    [Fact]
    public async Task Revert_puts_every_process_back_into_the_group_it_came_from()
    {
        var topology = DualGroup();
        var os = SplitDesktop(topology);
        var before = os.Processes.ToDictionary(p => p.Id, p => os.AffinityOf(p.Id));

        var journal = new InMemoryJournal();
        var resolver = new AmbientMutationResolver(os, topology);
        var ledger = new MutationLedger(journal, resolver);

        await ledger.ApplyAsync(
            "s1",
            [new AffinityConfinementMutation(os, topology.AmbientMask, [.. os.Processes], topology)],
            Permit());

        Assert.NotEqual(before[1000], os.AffinityOf(1000));

        var report = await ledger.RevertAsync();

        Assert.True(report.IsClean, string.Join(", ", report.Failed.Select(f => $"{f.Key}: {f.Error}")));

        foreach (var (id, mask) in before)
        {
            Assert.Equal(mask, os.AffinityOf(id));
        }
    }

    [Fact]
    public async Task A_mask_addressed_at_the_wrong_group_is_refused_rather_than_written()
    {
        // The simulated machine refuses it the way Windows would be asked to, so the
        // mutation can never rely on the operating system quietly reinterpreting a mask.
        var topology = DualGroup();
        var os = SplitDesktop(topology);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await os.SetAffinityAsync(
                1002, new ProcessorMask(topology.AmbientMask.Group, 0xFFUL), default));
    }

    [Fact]
    public async Task A_process_that_migrates_groups_between_capture_and_apply_is_skipped()
    {
        // Vanishingly rare, and the reason apply re-reads rather than trusting the capture.
        var topology = DualGroup();
        var os = SplitDesktop(topology);

        var mutation = new AffinityConfinementMutation(
            os, topology.AmbientMask, [.. os.Processes], topology);

        await mutation.CaptureAsync(default);

        os.PlaceInGroup(1001, OtherGroup(topology), 0xFFUL);
        var moved = os.AffinityOf(1001);

        await mutation.ApplyAsync(default);

        Assert.Equal(moved, os.AffinityOf(1001));
        Assert.Equal(topology.AmbientMask, os.AffinityOf(1000));
    }

    [Fact]
    public async Task Reverting_a_capture_from_another_group_does_not_write_into_this_one()
    {
        // A journal written on a different machine, or before a BIOS change that altered the
        // group layout. Restoring group 1 bits onto a group 0 process would be a change, not
        // a revert, so it must fail loudly rather than succeed wrongly.
        var topology = DualGroup();
        var os = SplitDesktop(topology);
        var before = os.AffinityOf(1000);

        var capture = JsonDocument.Parse(
            $$"""{"Processes":[{"Id":1000,"Name":"chrome","Group":{{OtherGroup(topology)}},"Mask":255}]}""");

        await new AffinityConfinementMutation(os, topology.AmbientMask, [], topology)
            .RevertAsync(capture.RootElement, default);

        Assert.Equal(before, os.AffinityOf(1000));
    }

    [Fact]
    public async Task A_single_group_machine_behaves_exactly_as_it_always_did()
    {
        // The reference machine has 32 logical processors and one group. None of the above
        // may change what happens there.
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());
        var os = new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800)
            .AddProcess(1001, "msedge", 400);

        var mutation = new AffinityConfinementMutation(
            os, topology.AmbientMask, [.. os.Processes], topology);

        await mutation.CaptureAsync(default);
        await mutation.ApplyAsync(default);

        Assert.Equal(0, topology.AmbientMask.Group);
        Assert.Equal(0x00000000FFFF0000UL, os.AffinityOf(1000).Mask);
        Assert.Equal(0x00000000FFFF0000UL, os.AffinityOf(1001).Mask);
    }

    [Fact]
    public async Task A_process_that_exits_during_capture_is_skipped_not_fatal()
    {
        var topology = DualGroup();
        var os = SplitDesktop(topology);
        os.FailEfficiencyFor.Add(1001);

        var capture = await new AffinityConfinementMutation(
                os, topology.AmbientMask, [.. os.Processes], topology)
            .CaptureAsync(default);

        var ids = capture.GetProperty("Processes").EnumerateArray()
            .Select(r => r.GetProperty("Id").GetInt32())
            .ToArray();

        Assert.Equal([1000], ids);
    }
}
