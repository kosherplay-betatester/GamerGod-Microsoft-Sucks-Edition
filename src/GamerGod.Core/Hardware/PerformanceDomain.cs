using System.Collections.Immutable;

namespace GamerGod.Core.Hardware;

/// <summary>
/// How the scheduler should regard a domain relative to the others on the same machine.
/// </summary>
public enum DomainClass
{
    /// <summary>The best domain available for latency-sensitive foreground work.</summary>
    Performance,

    /// <summary>Slower or smaller-cache cores. Where GamerGod evicts background work to.</summary>
    Efficiency,

    /// <summary>
    /// Low-power efficiency cores that live outside the main compute tile, such as Intel's
    /// SoC-tile LP-E cores. Never used for game work.
    /// </summary>
    LowPowerEfficiency,
}

/// <summary>
/// A set of logical processors that share a cache hierarchy and a performance character.
/// This is GamerGod's universal replacement for vendor-specific concepts like "CCD",
/// "P-core cluster" or "big cluster" — one abstraction that covers AMD multi-CCD and
/// X3D asymmetric-cache parts, Intel hybrid P/E/LP-E parts, and ARM big.LITTLE.
/// </summary>
public sealed record PerformanceDomain
{
    /// <summary>Stable index, ascending by lowest logical processor.</summary>
    public required int Id { get; init; }

    public required ProcessorMask Processors { get; init; }

    /// <summary>Group-relative logical processor indices in this domain, ascending.</summary>
    public required ImmutableArray<int> LogicalProcessors { get; init; }

    /// <summary>
    /// One entry per physical core; each entry lists that core's logical processors.
    /// Length greater than <see cref="LogicalProcessors"/> halved implies no SMT.
    /// </summary>
    public required ImmutableArray<ImmutableArray<int>> PhysicalCores { get; init; }

    /// <summary>Size of the last-level cache shared by this domain, in bytes.</summary>
    public required long LastLevelCacheBytes { get; init; }

    /// <summary>Windows' efficiency class for the cores in this domain. Higher is faster.</summary>
    public required byte EfficiencyClass { get; init; }

    public required DomainClass Class { get; init; }

    /// <summary>
    /// This domain's logical processors ordered by CPPC / favoured-core ranking, best first.
    /// Falls back to ascending order when the platform exposes no ranking.
    /// </summary>
    public required ImmutableArray<int> PreferredCoreOrder { get; init; }

    public int LogicalProcessorCount => LogicalProcessors.Length;

    public int PhysicalCoreCount => PhysicalCores.Length;

    public bool IsSimultaneousMultiThreaded => PhysicalCores.Any(c => c.Length > 1);

    /// <summary>Cache available per physical core — the metric that matters for game workloads.</summary>
    public long CacheBytesPerPhysicalCore =>
        PhysicalCoreCount == 0 ? 0 : LastLevelCacheBytes / PhysicalCoreCount;

    /// <summary>
    /// A mask containing only the first logical processor of each physical core, for
    /// SMT-excluded routing policies.
    /// </summary>
    public ProcessorMask PhysicalCoresOnlyMask
    {
        get
        {
            var mask = 0UL;
            foreach (var core in PhysicalCores)
            {
                mask |= 1UL << core[0];
            }

            return new ProcessorMask(Processors.Group, mask);
        }
    }

    public override string ToString() =>
        $"D{Id} [{Class}] {LogicalProcessorCount}LP/{PhysicalCoreCount}C " +
        $"{LastLevelCacheBytes / (1024 * 1024)}MB L3";
}
