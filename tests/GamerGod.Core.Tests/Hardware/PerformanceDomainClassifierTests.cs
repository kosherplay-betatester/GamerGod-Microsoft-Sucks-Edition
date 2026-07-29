using GamerGod.Core.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Hardware;

/// <summary>
/// Domain classification is the foundation of everything GamerGod does. A wrong answer here
/// is a silent double-digit performance regression that no other test would catch, so these
/// fixtures cover every shipping topology shape we know about.
/// </summary>
public sealed class PerformanceDomainClassifierTests
{
    private const long Mb = 1024L * 1024L;

    // ---------------------------------------------------------------------
    // AMD X3D — asymmetric cache. The reference machine.
    // ---------------------------------------------------------------------

    [Fact]
    public void Ryzen7950X3D_finds_two_domains_split_by_cache_size()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

        Assert.Equal(TopologyKind.AsymmetricCache, topology.Kind);
        Assert.Equal(2, topology.Domains.Length);
        Assert.Equal(32, topology.LogicalProcessorCount);
        Assert.Equal(16, topology.PhysicalCoreCount);
    }

    [Fact]
    public void Ryzen7950X3D_routes_games_to_the_vcache_domain()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

        Assert.Equal(96 * Mb, topology.GameDomain.LastLevelCacheBytes);
        Assert.Equal(16, topology.GameDomain.LogicalProcessorCount);
        Assert.Equal(8, topology.GameDomain.PhysicalCoreCount);
        Assert.Equal(Enumerable.Range(0, 16), topology.GameDomain.LogicalProcessors);
        Assert.Equal(DomainClass.Performance, topology.GameDomain.Class);
    }

    [Fact]
    public void Ryzen7950X3D_evicts_background_work_to_the_frequency_domain()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

        var ambient = Assert.Single(topology.AmbientDomains);
        Assert.Equal(32 * Mb, ambient.LastLevelCacheBytes);
        Assert.Equal(Enumerable.Range(16, 16), ambient.LogicalProcessors);
        Assert.Equal(DomainClass.Efficiency, ambient.Class);
        Assert.True(topology.CanPartition);

        // Bits 16..31 set, nothing else. This mask becomes the background job object's affinity.
        Assert.Equal(0x00000000FFFF0000UL, topology.AmbientMask.Mask);
    }

    [Fact]
    public void Vcache_domain_is_found_by_cache_size_not_by_enumeration_order()
    {
        // Same silicon, dies enumerated the other way round. Anything that assumes
        // "domain 0 is the cache die" fails here — which is exactly the point.
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3DInverted());

        Assert.Equal(TopologyKind.AsymmetricCache, topology.Kind);
        Assert.Equal(96 * Mb, topology.GameDomain.LastLevelCacheBytes);
        Assert.Equal(Enumerable.Range(16, 16), topology.GameDomain.LogicalProcessors);

        var ambient = Assert.Single(topology.AmbientDomains);
        Assert.Equal(Enumerable.Range(0, 16), ambient.LogicalProcessors);
    }

    // ---------------------------------------------------------------------
    // Single domain — nothing to partition, and we must say so honestly.
    // ---------------------------------------------------------------------

    [Fact]
    public void Ryzen7800X3D_is_uniform_and_cannot_partition()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7800X3D());

        Assert.Equal(TopologyKind.Uniform, topology.Kind);
        Assert.Single(topology.Domains);
        Assert.False(topology.CanPartition);
        Assert.Empty(topology.AmbientDomains);
        Assert.Equal(96 * Mb, topology.GameDomain.LastLevelCacheBytes);
        Assert.Equal(DomainClass.Performance, topology.GameDomain.Class);
    }

    [Fact]
    public void Cpu_without_any_l3_still_produces_one_usable_domain()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.NoLastLevelCache());

        Assert.Equal(TopologyKind.Uniform, topology.Kind);
        Assert.Single(topology.Domains);
        Assert.Equal(2, topology.GameDomain.LogicalProcessorCount);
        Assert.Equal(0, topology.GameDomain.LastLevelCacheBytes);
        Assert.False(topology.CanPartition);
    }

    // ---------------------------------------------------------------------
    // Symmetric multi-domain — partition anyway, to avoid cross-die latency.
    // ---------------------------------------------------------------------

    [Fact]
    public void Ryzen7950X_has_two_equivalent_domains_and_still_partitions()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X());

        Assert.Equal(TopologyKind.SymmetricMultiDomain, topology.Kind);
        Assert.Equal(2, topology.Domains.Length);
        Assert.True(topology.CanPartition);
        Assert.All(topology.Domains, d => Assert.Equal(32 * Mb, d.LastLevelCacheBytes));
    }

    [Fact]
    public void Symmetric_domains_break_the_tie_using_the_preferred_core_ranking()
    {
        // PreferredCoreOrder starts at logical processor 16, so the second domain wins.
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X());

        Assert.Equal(Enumerable.Range(16, 16), topology.GameDomain.LogicalProcessors);
    }

    // ---------------------------------------------------------------------
    // Intel hybrid — the case cache-only grouping gets catastrophically wrong.
    // ---------------------------------------------------------------------

    [Fact]
    public void CoreI9_12900K_splits_p_and_e_cores_despite_one_shared_l3()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.CoreI9_12900K());

        Assert.Equal(TopologyKind.Hybrid, topology.Kind);
        Assert.Equal(2, topology.Domains.Length);
        Assert.Equal(24, topology.LogicalProcessorCount);
    }

    [Fact]
    public void CoreI9_12900K_routes_games_to_the_p_cores()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.CoreI9_12900K());

        Assert.Equal(1, topology.GameDomain.EfficiencyClass);
        Assert.Equal(16, topology.GameDomain.LogicalProcessorCount);
        Assert.Equal(8, topology.GameDomain.PhysicalCoreCount);
        Assert.True(topology.GameDomain.IsSimultaneousMultiThreaded);
        Assert.Equal(DomainClass.Performance, topology.GameDomain.Class);

        var eCores = Assert.Single(topology.AmbientDomains);
        Assert.Equal(0, eCores.EfficiencyClass);
        Assert.Equal(8, eCores.LogicalProcessorCount);
        Assert.False(eCores.IsSimultaneousMultiThreaded);
        Assert.Equal(DomainClass.Efficiency, eCores.Class);
    }

    [Fact]
    public void CoreUltra7_155H_separates_p_cores_e_cores_and_low_power_e_cores()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.CoreUltra7_155H());

        Assert.Equal(TopologyKind.Hybrid, topology.Kind);
        Assert.Equal(3, topology.Domains.Length);
        Assert.Equal(22, topology.LogicalProcessorCount);

        Assert.Equal(2, topology.GameDomain.EfficiencyClass);
        Assert.Equal(12, topology.GameDomain.LogicalProcessorCount);

        var classes = topology.Domains.Select(d => d.Class).ToArray();
        Assert.Contains(DomainClass.Performance, classes);
        Assert.Contains(DomainClass.Efficiency, classes);
        Assert.Contains(DomainClass.LowPowerEfficiency, classes);
    }

    [Fact]
    public void Low_power_cores_outside_the_l3_are_still_assigned_to_a_domain()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.CoreUltra7_155H());

        var lpe = Assert.Single(topology.Domains, d => d.Class == DomainClass.LowPowerEfficiency);
        Assert.Equal(2, lpe.LogicalProcessorCount);
        Assert.Equal(0, lpe.LastLevelCacheBytes);
        Assert.Equal(new[] { 20, 21 }, lpe.LogicalProcessors);
    }

    // ---------------------------------------------------------------------
    // Invariants that must hold for every topology we can classify.
    // ---------------------------------------------------------------------

    public static TheoryData<string, ProcessorSnapshot> AllKnownCpus() => new()
    {
        { "7950X3D", KnownCpus.Ryzen7950X3D() },
        { "7950X3D-inverted", KnownCpus.Ryzen7950X3DInverted() },
        { "7800X3D", KnownCpus.Ryzen7800X3D() },
        { "7950X", KnownCpus.Ryzen7950X() },
        { "12900K", KnownCpus.CoreI9_12900K() },
        { "155H", KnownCpus.CoreUltra7_155H() },
        { "no-l3", KnownCpus.NoLastLevelCache() },
    };

    [Theory]
    [MemberData(nameof(AllKnownCpus))]
    public void Every_logical_processor_belongs_to_exactly_one_domain(string name, ProcessorSnapshot snapshot)
    {
        var topology = PerformanceDomainClassifier.Classify(snapshot);

        var assigned = topology.Domains.SelectMany(d => d.LogicalProcessors).ToArray();
        var expected = snapshot.Cores.SelectMany(c => c.Processors.LogicalProcessors).OrderBy(x => x).ToArray();

        Assert.Equal(expected.Length, assigned.Length);
        Assert.Equal(expected, assigned.OrderBy(x => x));
        Assert.Equal(assigned.Length, assigned.Distinct().Count());
        Assert.Equal(snapshot.LogicalProcessorCount, topology.LogicalProcessorCount);
        Assert.NotEmpty(name);
    }

    [Theory]
    [MemberData(nameof(AllKnownCpus))]
    public void Game_domain_and_ambient_domains_are_disjoint_and_cover_everything(
        string name, ProcessorSnapshot snapshot)
    {
        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.DoesNotContain(topology.GameDomain, topology.AmbientDomains);
        Assert.Equal(topology.Domains.Length, topology.AmbientDomains.Length + 1);
        Assert.Equal(0UL, topology.GameDomain.Processors.Mask & topology.AmbientMask.Mask);
        Assert.NotEmpty(name);
    }

    [Theory]
    [MemberData(nameof(AllKnownCpus))]
    public void Game_domain_is_never_an_efficiency_domain(string name, ProcessorSnapshot snapshot)
    {
        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.Equal(DomainClass.Performance, topology.GameDomain.Class);
        Assert.All(
            topology.AmbientDomains,
            d => Assert.NotEqual(DomainClass.Performance, d.Class));
        Assert.NotEmpty(name);
    }

    [Theory]
    [MemberData(nameof(AllKnownCpus))]
    public void Domain_ids_are_stable_ascending_and_summary_is_never_empty(
        string name, ProcessorSnapshot snapshot)
    {
        var topology = PerformanceDomainClassifier.Classify(snapshot);

        Assert.Equal(
            Enumerable.Range(0, topology.Domains.Length),
            topology.Domains.Select(d => d.Id));

        // Ids follow ascending lowest-logical-processor, so classification is deterministic.
        var firstLps = topology.Domains.Select(d => d.LogicalProcessors[0]).ToArray();
        Assert.Equal(firstLps.OrderBy(x => x), firstLps);

        Assert.False(string.IsNullOrWhiteSpace(topology.Summary()));
        Assert.Contains(snapshot.ProcessorName, topology.Summary(), StringComparison.Ordinal);
        Assert.NotEmpty(name);
    }

    [Theory]
    [MemberData(nameof(AllKnownCpus))]
    public void Physical_cores_only_mask_selects_one_processor_per_core(
        string name, ProcessorSnapshot snapshot)
    {
        var topology = PerformanceDomainClassifier.Classify(snapshot);

        foreach (var domain in topology.Domains)
        {
            Assert.Equal(domain.PhysicalCoreCount, domain.PhysicalCoresOnlyMask.Count);

            // Every selected processor must belong to the domain it came from.
            Assert.Equal(
                domain.PhysicalCoresOnlyMask.Mask,
                domain.PhysicalCoresOnlyMask.Mask & domain.Processors.Mask);
        }

        Assert.NotEmpty(name);
    }

    [Fact]
    public void Classify_rejects_a_snapshot_with_no_cores()
    {
        var empty = new ProcessorSnapshot { Cores = [], Caches = [] };

        Assert.Throws<ArgumentException>(() => PerformanceDomainClassifier.Classify(empty));
    }
}
