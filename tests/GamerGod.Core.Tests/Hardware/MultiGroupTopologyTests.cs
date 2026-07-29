using System.Collections.Immutable;
using GamerGod.Core.Hardware;
using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Hardware;

/// <summary>
/// Regression cover for defects that only appear above 64 logical processors, where Windows
/// splits the machine into processor groups.
///
/// <para>
/// Nobody would find these by using GamerGod, because they need a Threadripper or a
/// dual-socket workstation to reproduce. They are exactly the kind of bug that ships.
/// </para>
/// </summary>
public sealed class MultiGroupTopologyTests
{
    private const long Mb = 1024L * 1024L;

    /// <summary>
    /// A 96-core / 192-thread part: two processor groups, each with its own cores and
    /// caches, and each numbering its logical processors from zero.
    /// </summary>
    private static ProcessorSnapshot DualGroupWorkstation()
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

        return new ProcessorSnapshot
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
        };
    }

    [Fact]
    public void Cores_in_different_groups_never_merge_into_one_domain()
    {
        // The defect: bucketing on (cache, efficiency class) alone merged group 0 and
        // group 1 cores whose keys happened to match. Logical processor 3 of group 0 and
        // logical processor 3 of group 1 are different processors that set the same bit,
        // so the resulting mask addressed the wrong cores entirely.
        var topology = PerformanceDomainClassifier.Classify(DualGroupWorkstation());

        foreach (var domain in topology.Domains)
        {
            var groups = domain.PhysicalCores
                .SelectMany(core => core)
                .Select(_ => domain.Processors.Group)
                .Distinct()
                .ToArray();

            Assert.Single(groups);
        }

        // Four distinct caches across two groups means four domains, not two.
        Assert.Equal(4, topology.Domains.Length);
        Assert.Equal(96, topology.LogicalProcessorCount);
        Assert.Equal(48, topology.PhysicalCoreCount);
    }

    [Fact]
    public void Domains_are_ordered_by_group_then_by_processor()
    {
        var topology = PerformanceDomainClassifier.Classify(DualGroupWorkstation());

        var keys = topology.Domains
            .Select(d => (d.Processors.Group, First: d.LogicalProcessors[0]))
            .ToArray();

        Assert.Equal(keys.OrderBy(k => k.Group).ThenBy(k => k.First), keys);
    }

    [Fact]
    public void The_ambient_mask_only_ever_covers_the_game_domains_own_group()
    {
        // A mask cannot span processor groups. Combining them regardless would produce bits
        // that mean different processors depending on which group Windows applied them to.
        var topology = PerformanceDomainClassifier.Classify(DualGroupWorkstation());

        var ambient = topology.AmbientMask;

        Assert.Equal(topology.GameDomain.Processors.Group, ambient.Group);

        foreach (var domain in topology.AmbientDomains.Where(d =>
                     d.Processors.Group != topology.GameDomain.Processors.Group))
        {
            // Domains in another group contribute nothing, rather than contributing bits
            // that would be misread.
            Assert.Equal(0UL, ambient.Mask & domain.Processors.Mask & ~InGroupMask(topology));
        }
    }

    [Fact]
    public void A_multi_group_machine_still_produces_a_safe_routing_plan()
    {
        var topology = PerformanceDomainClassifier.Classify(DualGroupWorkstation());
        var result = RoutingSafety.ValidateTopologyPlan(topology);

        Assert.True(result.IsSafe, result.Explain());
    }

    [Fact]
    public void Partitioning_is_declined_when_the_game_group_has_no_ambient_domain()
    {
        // A single domain in group 0 and everything else in group 1. There is nothing in
        // the game's own group to evict background work to, so the honest answer is that
        // this machine cannot be partitioned - not a mask that addresses the wrong group.
        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        cores.Add(new PhysicalCore(ProcessorMask.FromLogicalProcessors(0, 0, 1), 0));
        for (var core = 0; core < 8; core++)
        {
            cores.Add(new PhysicalCore(
                ProcessorMask.FromLogicalProcessors(1, core * 2, (core * 2) + 1), 0));
        }

        var snapshot = new ProcessorSnapshot
        {
            ProcessorName = "Lopsided dual-group machine",
            Cores = cores.ToImmutable(),
            Caches =
            [
                new CacheInfo(3, 96 * Mb, ProcessorMask.Range(0, 0, 1)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(1, 0, 15)),
            ],
        };

        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.Equal(96 * Mb, topology.GameDomain.LastLevelCacheBytes);
        Assert.Equal(0, topology.GameDomain.Processors.Group);
        Assert.True(topology.AmbientMask.IsEmpty);
        Assert.False(topology.CanPartition);
        Assert.True(RoutingSafety.ValidateTopologyPlan(topology).IsSafe);
    }

    private static ulong InGroupMask(CpuTopology topology)
    {
        var group = topology.GameDomain.Processors.Group;
        var mask = 0UL;

        foreach (var domain in topology.Domains.Where(d => d.Processors.Group == group))
        {
            mask |= domain.Processors.Mask;
        }

        return mask;
    }
}

/// <summary>
/// Regression cover for cache-level selection.
/// </summary>
public sealed class CacheLevelSelectionTests
{
    private const long Mb = 1024L * 1024L;

    [Fact]
    public void A_victim_l4_never_erases_a_split_l3()
    {
        // The defect: selecting "the deepest cache present". A package-wide L4 or eDRAM
        // spans every core, so it collapsed a genuinely split L3 into a single domain and
        // silently disabled partitioning on precisely the hardware it helps most.
        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        for (var core = 0; core < 16; core++)
        {
            cores.Add(new PhysicalCore(
                ProcessorMask.FromLogicalProcessors(0, core * 2, (core * 2) + 1), 0));
        }

        var snapshot = new ProcessorSnapshot
        {
            ProcessorName = "X3D part with a package-wide L4",
            Cores = cores.ToImmutable(),
            Caches =
            [
                new CacheInfo(3, 96 * Mb, ProcessorMask.Range(0, 0, 15)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 16, 31)),
                new CacheInfo(4, 128 * Mb, ProcessorMask.Range(0, 0, 31)),
            ],
        };

        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.Equal(TopologyKind.AsymmetricCache, topology.Kind);
        Assert.Equal(2, topology.Domains.Length);
        Assert.Equal(96 * Mb, topology.GameDomain.LastLevelCacheBytes);
        Assert.True(topology.CanPartition);
    }

    [Fact]
    public void A_machine_with_only_an_l4_still_uses_it_as_the_boundary()
    {
        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        for (var core = 0; core < 8; core++)
        {
            cores.Add(new PhysicalCore(ProcessorMask.FromLogicalProcessors(0, core), 0));
        }

        var snapshot = new ProcessorSnapshot
        {
            ProcessorName = "L4 only",
            Cores = cores.ToImmutable(),
            Caches =
            [
                new CacheInfo(4, 64 * Mb, ProcessorMask.Range(0, 0, 3)),
                new CacheInfo(4, 16 * Mb, ProcessorMask.Range(0, 4, 7)),
            ],
        };

        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.Equal(2, topology.Domains.Length);
        Assert.Equal(64 * Mb, topology.GameDomain.LastLevelCacheBytes);
    }
}

/// <summary>Robustness of the snapshot type itself.</summary>
public sealed class ProcessorSnapshotRobustnessTests
{
    [Fact]
    public void Counting_a_default_valued_snapshot_reports_zero_rather_than_throwing()
    {
        // "required" forces an assignment; it does not stop that assignment being default.
        var snapshot = new ProcessorSnapshot { Cores = default, Caches = default };

        Assert.Equal(0, snapshot.LogicalProcessorCount);
        Assert.Equal(0, snapshot.PhysicalCoreCount);
        Assert.Throws<ArgumentException>(() => PerformanceDomainClassifier.Classify(snapshot));
    }
}
