using System.Collections.Immutable;
using GodMode.Core.Hardware;

namespace GodMode.Core.Tests.Hardware;

/// <summary>
/// Processor layouts for real, shipping CPUs, expressed exactly as
/// <c>GetLogicalProcessorInformationEx</c> reports them. These are the fixtures that keep
/// domain classification honest across vendors — a change that improves AMD X3D detection
/// but breaks Intel hybrid will fail here.
/// </summary>
internal static class KnownCpus
{
    private const long Mb = 1024L * 1024L;

    /// <summary>
    /// Builds a snapshot from a description of core clusters.
    /// Each cluster is (physicalCoreCount, logicalProcessorsPerCore, efficiencyClass).
    /// Logical processors are allocated contiguously in cluster order, which matches how
    /// Windows enumerates every part we have measured.
    /// </summary>
    private static (ImmutableArray<PhysicalCore> Cores, ImmutableArray<int> AllLps) BuildCores(
        params (int PhysicalCores, int LpsPerCore, byte EfficiencyClass)[] clusters)
    {
        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        var allLps = ImmutableArray.CreateBuilder<int>();
        var nextLp = 0;

        foreach (var (physicalCores, lpsPerCore, efficiencyClass) in clusters)
        {
            for (var c = 0; c < physicalCores; c++)
            {
                var lps = new int[lpsPerCore];
                for (var t = 0; t < lpsPerCore; t++)
                {
                    lps[t] = nextLp;
                    allLps.Add(nextLp);
                    nextLp++;
                }

                cores.Add(new PhysicalCore(ProcessorMask.FromLogicalProcessors(0, lps), efficiencyClass));
            }
        }

        return (cores.ToImmutable(), allLps.ToImmutable());
    }

    /// <summary>
    /// AMD Ryzen 9 7950X3D — the reference machine. Two CCDs, asymmetric cache.
    /// CCD0 carries stacked 3D V-Cache: 96 MB across cores 0-7 (LP 0-15).
    /// CCD1 is a standard 32 MB die across cores 8-15 (LP 16-31).
    /// </summary>
    public static ProcessorSnapshot Ryzen7950X3D()
    {
        var (cores, _) = BuildCores((8, 2, 0), (8, 2, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "AMD Ryzen 9 7950X3D 16-Core Processor",
            MaxFrequencyMhz = 4201,
            Cores = cores,
            Caches =
            [
                new CacheInfo(3, 96 * Mb, ProcessorMask.Range(0, 0, 15)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 16, 31)),
                new CacheInfo(2, 1 * Mb, ProcessorMask.Range(0, 0, 1)),
            ],
            PreferredCoreOrder = [16, 18, 0, 2],
        };
    }

    /// <summary>
    /// The same part with the V-Cache die enumerated second. Firmware revisions have moved
    /// this ordering, so any classifier that assumes "domain 0 is the cache die" is wrong.
    /// </summary>
    public static ProcessorSnapshot Ryzen7950X3DInverted()
    {
        var (cores, _) = BuildCores((8, 2, 0), (8, 2, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "AMD Ryzen 9 7950X3D 16-Core Processor",
            MaxFrequencyMhz = 4201,
            Cores = cores,
            Caches =
            [
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 0, 15)),
                new CacheInfo(3, 96 * Mb, ProcessorMask.Range(0, 16, 31)),
            ],
        };
    }

    /// <summary>AMD Ryzen 7 7800X3D — single CCD, 96 MB. Nothing to partition.</summary>
    public static ProcessorSnapshot Ryzen7800X3D()
    {
        var (cores, _) = BuildCores((8, 2, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "AMD Ryzen 7 7800X3D 8-Core Processor",
            MaxFrequencyMhz = 4200,
            Cores = cores,
            Caches = [new CacheInfo(3, 96 * Mb, ProcessorMask.Range(0, 0, 15))],
        };
    }

    /// <summary>
    /// AMD Ryzen 9 7950X — two identical 32 MB CCDs. Partitioning still pays, because
    /// cross-CCD traffic costs latency, but neither domain is inherently better.
    /// </summary>
    public static ProcessorSnapshot Ryzen7950X()
    {
        var (cores, _) = BuildCores((8, 2, 0), (8, 2, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "AMD Ryzen 9 7950X 16-Core Processor",
            MaxFrequencyMhz = 4500,
            Cores = cores,
            Caches =
            [
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 0, 15)),
                new CacheInfo(3, 32 * Mb, ProcessorMask.Range(0, 16, 31)),
            ],
            PreferredCoreOrder = [16, 0, 18, 2],
        };
    }

    /// <summary>
    /// Intel Core i9-12900K — 8 P-cores (SMT, efficiency class 1) and 8 E-cores (no SMT,
    /// efficiency class 0), all sharing a single 30 MB L3.
    ///
    /// This is the fixture that proves cache-only grouping is insufficient: grouping by L3
    /// alone yields one domain here and misses the entire point of a hybrid CPU.
    /// </summary>
    public static ProcessorSnapshot CoreI9_12900K()
    {
        var (cores, _) = BuildCores((8, 2, 1), (8, 1, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "12th Gen Intel(R) Core(TM) i9-12900K",
            MaxFrequencyMhz = 3200,
            Cores = cores,
            Caches = [new CacheInfo(3, 30 * Mb, ProcessorMask.Range(0, 0, 23))],
            PreferredCoreOrder = [0, 2, 4, 6],
        };
    }

    /// <summary>
    /// Intel Core Ultra 7 155H (Meteor Lake) — three performance tiers:
    /// 6 P-cores (SMT, class 2), 8 E-cores (class 1), and 2 low-power E-cores on the SoC
    /// tile (class 0) which sit outside the compute tile's L3 entirely.
    /// </summary>
    public static ProcessorSnapshot CoreUltra7_155H()
    {
        var (cores, _) = BuildCores((6, 2, 2), (8, 1, 1), (2, 1, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "Intel(R) Core(TM) Ultra 7 155H",
            MaxFrequencyMhz = 3800,
            Cores = cores,

            // The two LP-E cores (LP 20-21) are deliberately absent from the L3 mask.
            Caches = [new CacheInfo(3, 24 * Mb, ProcessorMask.Range(0, 0, 19))],
        };
    }

    /// <summary>A minimal dual-core with no L3 reported at all.</summary>
    public static ProcessorSnapshot NoLastLevelCache()
    {
        var (cores, _) = BuildCores((2, 1, 0));
        return new ProcessorSnapshot
        {
            ProcessorName = "Generic Dual Core",
            Cores = cores,
            Caches = [new CacheInfo(2, 512 * 1024, ProcessorMask.Range(0, 0, 1))],
        };
    }
}
