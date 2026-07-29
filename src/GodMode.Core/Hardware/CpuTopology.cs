using System.Collections.Immutable;

namespace GodMode.Core.Hardware;

/// <summary>
/// Why this machine's domains differ from one another, which determines both how GodMode
/// routes work and what it tells the user.
/// </summary>
public enum TopologyKind
{
    /// <summary>
    /// One domain. No partitioning is possible or useful; ambient levers still apply in full.
    /// Examples: Ryzen 7800X3D, Ryzen 5 7600, most laptops before Intel 12th gen.
    /// </summary>
    Uniform,

    /// <summary>
    /// Multiple domains differing in last-level cache size. The larger-cache domain is
    /// materially better for games. Example: Ryzen 9 7950X3D (96 MB + 32 MB).
    /// </summary>
    AsymmetricCache,

    /// <summary>
    /// Multiple domains differing in core performance class. Example: Intel 12th gen and
    /// later (P-cores + E-cores, plus LP-E cores from Meteor Lake), ARM big.LITTLE.
    /// </summary>
    Hybrid,

    /// <summary>
    /// Multiple equivalent domains. Partitioning still helps by avoiding cross-domain
    /// cache and interconnect latency. Example: Ryzen 9 7950X (two identical 32 MB CCDs).
    /// </summary>
    SymmetricMultiDomain,
}

/// <summary>
/// The classified processor layout, plus GodMode's routing decision for it.
/// </summary>
public sealed record CpuTopology
{
    public required string ProcessorName { get; init; }

    public required TopologyKind Kind { get; init; }

    /// <summary>All domains, ordered by <see cref="PerformanceDomain.Id"/>.</summary>
    public required ImmutableArray<PerformanceDomain> Domains { get; init; }

    /// <summary>
    /// The domain games should run on. On <see cref="TopologyKind.Uniform"/> machines this is
    /// the only domain, and <see cref="CanPartition"/> is false.
    /// </summary>
    public required PerformanceDomain GameDomain { get; init; }

    /// <summary>
    /// The domains background work is evicted to. Empty on uniform machines.
    /// </summary>
    public required ImmutableArray<PerformanceDomain> AmbientDomains { get; init; }

    public int MaxFrequencyMhz { get; init; }

    public int LogicalProcessorCount => Domains.Sum(d => d.LogicalProcessorCount);

    public int PhysicalCoreCount => Domains.Sum(d => d.PhysicalCoreCount);

    /// <summary>
    /// True when there is somewhere to evict background work to. When false, GodMode
    /// applies every ambient lever except domain confinement, and says so in the UI
    /// rather than pretending to partition a machine that cannot be partitioned.
    /// </summary>
    public bool CanPartition => AmbientDomains.Length > 0;

    /// <summary>Combined mask of every ambient domain — the background job object's affinity.</summary>
    public ProcessorMask AmbientMask
    {
        get
        {
            if (AmbientDomains.Length == 0)
            {
                return default;
            }

            var group = AmbientDomains[0].Processors.Group;
            var mask = 0UL;
            foreach (var domain in AmbientDomains)
            {
                // Cross-group ambient sets are not representable in a single mask; GodMode
                // targets consumer hardware, which is always a single processor group.
                if (domain.Processors.Group != group)
                {
                    continue;
                }

                mask |= domain.Processors.Mask;
            }

            return new ProcessorMask(group, mask);
        }
    }

    /// <summary>
    /// A one-line human summary, e.g.
    /// "AMD Ryzen 9 7950X3D — AsymmetricCache, 2 domains, game on D0 (96 MB, 16 LP)".
    /// </summary>
    public string Summary()
    {
        var partition = CanPartition
            ? $"game on D{GameDomain.Id} ({GameDomain.LastLevelCacheBytes / (1024 * 1024)} MB, " +
              $"{GameDomain.LogicalProcessorCount} LP), ambient on " +
              string.Join("+", AmbientDomains.Select(d => $"D{d.Id}"))
            : "single domain, no partitioning available";

        return $"{ProcessorName} — {Kind}, {Domains.Length} domain(s), {partition}";
    }
}
