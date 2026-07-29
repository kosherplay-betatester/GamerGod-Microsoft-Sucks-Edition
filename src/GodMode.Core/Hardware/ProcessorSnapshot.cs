using System.Collections.Immutable;

namespace GodMode.Core.Hardware;

/// <summary>
/// A processor-group-relative affinity mask, mirroring Win32 <c>GROUP_AFFINITY</c>.
/// Windows partitions machines with more than 64 logical processors into groups,
/// so a bare <see cref="ulong"/> is not sufficient to identify a set of processors.
/// </summary>
/// <param name="Group">Processor group index. Zero on every consumer machine.</param>
/// <param name="Mask">Bit <c>n</c> set means logical processor <c>n</c> of that group.</param>
public readonly record struct ProcessorMask(ushort Group, ulong Mask)
{
    public bool IsEmpty => Mask == 0;

    public int Count => System.Numerics.BitOperations.PopCount(Mask);

    /// <summary>Group-relative logical processor indices, ascending.</summary>
    public IEnumerable<int> LogicalProcessors
    {
        get
        {
            for (var bit = 0; bit < 64; bit++)
            {
                if ((Mask & (1UL << bit)) != 0)
                {
                    yield return bit;
                }
            }
        }
    }

    public bool Intersects(ProcessorMask other) =>
        Group == other.Group && (Mask & other.Mask) != 0;

    public static ProcessorMask FromLogicalProcessors(ushort group, params int[] logicalProcessors)
    {
        var mask = 0UL;
        foreach (var lp in logicalProcessors)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lp, 64);
            ArgumentOutOfRangeException.ThrowIfNegative(lp);
            mask |= 1UL << lp;
        }

        return new ProcessorMask(group, mask);
    }

    /// <summary>Contiguous range helper, e.g. <c>Range(0, 0, 15)</c> for logical processors 0..15.</summary>
    public static ProcessorMask Range(ushort group, int firstLogicalProcessor, int lastLogicalProcessor)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstLogicalProcessor, lastLogicalProcessor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(lastLogicalProcessor, 64);
        ArgumentOutOfRangeException.ThrowIfNegative(firstLogicalProcessor);

        var mask = 0UL;
        for (var lp = firstLogicalProcessor; lp <= lastLogicalProcessor; lp++)
        {
            mask |= 1UL << lp;
        }

        return new ProcessorMask(group, mask);
    }

    public override string ToString() => $"G{Group}:0x{Mask:X16}";
}

/// <summary>A processor cache as reported by <c>RelationCache</c>.</summary>
/// <param name="Level">1, 2, 3 or 4.</param>
/// <param name="SizeBytes">Total size of this cache instance.</param>
/// <param name="Processors">The logical processors that share this cache instance.</param>
public sealed record CacheInfo(byte Level, long SizeBytes, ProcessorMask Processors);

/// <summary>A physical core as reported by <c>RelationProcessorCore</c>.</summary>
/// <param name="Processors">
/// The logical processors belonging to this core. More than one implies SMT / hyper-threading.
/// </param>
/// <param name="EfficiencyClass">
/// Windows' relative performance ranking for this core, from <c>PROCESSOR_RELATIONSHIP</c>.
/// Higher is more performant. Zero on every core of a homogeneous CPU; varies on Intel
/// hybrid parts and ARM big.LITTLE designs.
/// </param>
public sealed record PhysicalCore(ProcessorMask Processors, byte EfficiencyClass)
{
    public bool IsSimultaneousMultiThreaded => Processors.Count > 1;
}

/// <summary>
/// The raw processor layout, expressed as plain data so that domain classification can be
/// unit tested without touching Win32. Produced by <c>GodMode.Windows</c> from
/// <c>GetLogicalProcessorInformationEx</c>; consumed by <see cref="PerformanceDomainClassifier"/>.
/// </summary>
public sealed record ProcessorSnapshot
{
    public required ImmutableArray<CacheInfo> Caches { get; init; }

    public required ImmutableArray<PhysicalCore> Cores { get; init; }

    /// <summary>
    /// CPPC / favoured-core ordering, best first, as group-relative logical processor indices.
    /// Empty when the platform does not expose a ranking; classification then falls back to
    /// ascending processor order, which is a stable but arbitrary tiebreak.
    /// </summary>
    public ImmutableArray<int> PreferredCoreOrder { get; init; } = [];

    /// <summary>Nominal maximum frequency in MHz, for reporting only. Zero when unknown.</summary>
    public int MaxFrequencyMhz { get; init; }

    /// <summary>Processor brand string, for reporting and profile matching.</summary>
    public string ProcessorName { get; init; } = "Unknown Processor";

    // Both guard against a default-valued ImmutableArray. These are required properties, but
    // "required" only forces an assignment - it does not stop that assignment being
    // default, and a NullReferenceException from a count is a poor way to learn that.
    public int LogicalProcessorCount =>
        Cores.IsDefaultOrEmpty ? 0 : Cores.Sum(c => c.Processors.Count);

    public int PhysicalCoreCount => Cores.IsDefaultOrEmpty ? 0 : Cores.Length;
}
