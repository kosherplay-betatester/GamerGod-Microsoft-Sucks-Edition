using System.Collections.Immutable;

namespace GamerGod.Core.Hardware;

/// <summary>
/// Why this machine's domains differ from one another, which determines both how GamerGod
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
/// The classified processor layout, plus GamerGod's routing decision for it.
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
    /// The fewest logical processors the rest of Windows may be confined to.
    ///
    /// <para>
    /// One is not survivable in practice. A single processor has to carry the shell, the
    /// audio engine, every driver's deferred work and GamerGod's own watchdog, so one
    /// spinning background thread stalls the entire machine — including the code that would
    /// undo the partition and the panic key that would trigger it.
    /// </para>
    /// </summary>
    public const int MinimumAmbientProcessors = 2;

    /// <summary>
    /// True when there is somewhere to evict background work to that is large enough to
    /// keep Windows responsive.
    ///
    /// <para>
    /// A second domain is not sufficient on its own. A processor whose ambient side holds a
    /// single logical processor would leave Windows unschedulable, so such a machine is
    /// reported as unpartitionable rather than partitioned unsafely. GamerGod then applies
    /// every ambient lever except domain confinement and says so, instead of pretending to
    /// partition a machine it cannot partition safely.
    /// </para>
    /// </summary>
    public bool CanPartition =>
        AmbientDomains.Length > 0 && AmbientMask.Count >= MinimumAmbientProcessors;

    /// <summary>
    /// Combined mask of the ambient domains — the background job object's affinity.
    ///
    /// <para>
    /// Restricted to the game domain's processor group. A single affinity mask cannot span
    /// groups, so on a machine large enough to have more than one, only the ambient domains
    /// sharing the game's group are addressable. Combining them regardless would produce a
    /// mask whose bits mean different processors depending on which group the operating
    /// system applied it to.
    /// </para>
    ///
    /// <para>
    /// When no ambient domain shares the game's group the result is empty, and
    /// <see cref="CanPartition"/> is then false — GamerGod declines to partition rather than
    /// confining background work to a mask that does not mean what it says.
    /// </para>
    /// </summary>
    public ProcessorMask AmbientMask
    {
        get
        {
            var group = GameDomain.Processors.Group;
            var mask = 0UL;

            foreach (var domain in AmbientDomains)
            {
                if (domain.Processors.Group == group)
                {
                    mask |= domain.Processors.Mask;
                }
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
