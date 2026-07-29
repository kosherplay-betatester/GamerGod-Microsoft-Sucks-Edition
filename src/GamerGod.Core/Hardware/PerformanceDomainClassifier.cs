using System.Collections.Immutable;

namespace GamerGod.Core.Hardware;

/// <summary>
/// Turns a raw <see cref="ProcessorSnapshot"/> into a classified <see cref="CpuTopology"/>
/// with a routing decision.
///
/// <para>
/// The central rule is that a performance domain is a set of logical processors sharing
/// <em>both</em> a last-level cache instance <em>and</em> an efficiency class. Grouping by
/// cache alone is the obvious approach and it is wrong: on Intel hybrid parts every P-core
/// and E-core shares one L3, so cache-only grouping reports a single domain and misses the
/// entire point of the CPU. Grouping by efficiency class alone is equally wrong: AMD X3D
/// parts report a uniform efficiency class across both dies, so the 96 MB and 32 MB dies
/// would collapse into one domain.
/// </para>
///
/// <para>
/// Nothing here is derived from a processor name, a vendor check, or a hardcoded core
/// index. "Domain 0 is the V-Cache die" is true on some firmware revisions and false on
/// others, and assuming it is a silent double-digit performance regression.
/// </para>
/// </summary>
public static class PerformanceDomainClassifier
{
    /// <summary>
    /// Ratio above which two last-level caches count as materially different sizes.
    /// 96 MB vs 32 MB is 3.0; identical dies are 1.0. Nothing shipping sits near 1.25.
    /// </summary>
    private const double AsymmetricCacheRatio = 1.25;

    public static CpuTopology Classify(ProcessorSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Cores.IsDefaultOrEmpty)
        {
            throw new ArgumentException(
                "Processor snapshot contains no cores; topology cannot be classified.",
                nameof(snapshot));
        }

        var lastLevelCaches = SelectLastLevelCaches(snapshot.Caches);
        var domains = BuildDomains(snapshot, lastLevelCaches);
        var kind = DetermineKind(domains);
        var gameDomainIndex = SelectGameDomain(domains, kind, snapshot.PreferredCoreOrder);

        domains = AssignClasses(domains, kind, gameDomainIndex);

        var gameDomain = domains[gameDomainIndex];
        var ambient = domains.Where((_, i) => i != gameDomainIndex).ToImmutableArray();

        return new CpuTopology
        {
            ProcessorName = snapshot.ProcessorName,
            Kind = kind,
            Domains = domains,
            GameDomain = gameDomain,
            AmbientDomains = ambient,
            MaxFrequencyMhz = snapshot.MaxFrequencyMhz,
        };
    }

    /// <summary>
    /// The deepest cache level present. Usually L3; L2 on parts without an L3, and none at
    /// all on some virtualised or minimal platforms.
    /// </summary>
    private static ImmutableArray<CacheInfo> SelectLastLevelCaches(ImmutableArray<CacheInfo> caches)
    {
        if (caches.IsDefaultOrEmpty)
        {
            return [];
        }

        // Only L3 and deeper are meaningful domain boundaries. An L2 shared by two SMT
        // siblings would otherwise shatter the machine into one domain per core.
        var candidates = caches.Where(c => c.Level >= 3).ToImmutableArray();
        if (candidates.IsEmpty)
        {
            return [];
        }

        // L3 is the boundary that matters, and it is preferred over anything deeper.
        // A victim L4 or eDRAM spans the whole package, so selecting "the deepest cache"
        // would collapse a genuinely split L3 - the 96 MB and 32 MB dies of an X3D part,
        // say - into one domain and silently disable partitioning on exactly the hardware
        // it helps most.
        var level = candidates.Any(c => c.Level == 3) ? (byte)3 : candidates.Max(c => c.Level);

        return candidates
            .Where(c => c.Level == level)
            .OrderBy(c => c.Processors.Group)
            .ThenBy(c => c.Processors.Mask)
            .ToImmutableArray();
    }

    private static ImmutableArray<PerformanceDomain> BuildDomains(
        ProcessorSnapshot snapshot,
        ImmutableArray<CacheInfo> lastLevelCaches)
    {
        // Key: (processor group, index of the shared last-level cache instance, efficiency
        // class). -1 for the cache means the core sits outside every last-level cache, as
        // Intel's SoC-tile LP-E cores do.
        //
        // The group is part of the key because a mask is only meaningful within its own
        // group: logical processor 3 of group 0 and logical processor 3 of group 1 are
        // different processors that set the same bit. Merging them would produce a mask
        // that silently addresses the wrong cores on any machine with more than 64 threads.
        var buckets = new Dictionary<(ushort Group, int Cache, byte Efficiency), List<PhysicalCore>>();

        foreach (var core in snapshot.Cores)
        {
            var cacheIndex = -1;
            for (var i = 0; i < lastLevelCaches.Length; i++)
            {
                if (lastLevelCaches[i].Processors.Intersects(core.Processors))
                {
                    cacheIndex = i;
                    break;
                }
            }

            var key = (core.Processors.Group, cacheIndex, core.EfficiencyClass);
            if (!buckets.TryGetValue(key, out var members))
            {
                members = [];
                buckets[key] = members;
            }

            members.Add(core);
        }

        var ordered = buckets
            .Select(bucket => new
            {
                bucket.Key,
                Cores = bucket.Value,
                LowestLogicalProcessor = bucket.Value
                    .SelectMany(c => c.Processors.LogicalProcessors)
                    .Min(),
            })
            .OrderBy(b => b.Key.Group)
            .ThenBy(b => b.LowestLogicalProcessor)
            .ToArray();

        var domains = ImmutableArray.CreateBuilder<PerformanceDomain>(ordered.Length);

        for (var id = 0; id < ordered.Length; id++)
        {
            var bucket = ordered[id];
            var group = bucket.Cores[0].Processors.Group;

            var physicalCores = bucket.Cores
                .Select(c => c.Processors.LogicalProcessors.ToImmutableArray())
                .OrderBy(lps => lps[0])
                .ToImmutableArray();

            var logicalProcessors = physicalCores
                .SelectMany(lps => lps)
                .OrderBy(lp => lp)
                .ToImmutableArray();

            var mask = 0UL;
            foreach (var lp in logicalProcessors)
            {
                mask |= 1UL << lp;
            }

            var cacheBytes = bucket.Key.Cache >= 0
                ? lastLevelCaches[bucket.Key.Cache].SizeBytes
                : 0L;

            domains.Add(new PerformanceDomain
            {
                Id = id,
                Processors = new ProcessorMask(group, mask),
                LogicalProcessors = logicalProcessors,
                PhysicalCores = physicalCores,
                LastLevelCacheBytes = cacheBytes,
                EfficiencyClass = bucket.Key.Efficiency,
                Class = DomainClass.Performance, // replaced by AssignClasses
                PreferredCoreOrder = OrderByPreference(logicalProcessors, snapshot.PreferredCoreOrder),
            });
        }

        return domains.MoveToImmutable();
    }

    /// <summary>
    /// Orders a domain's logical processors by the platform's CPPC / favoured-core ranking,
    /// appending anything the ranking does not mention in ascending order so the result is
    /// always a complete, deterministic permutation.
    /// </summary>
    private static ImmutableArray<int> OrderByPreference(
        ImmutableArray<int> logicalProcessors,
        ImmutableArray<int> globalPreference)
    {
        if (globalPreference.IsDefaultOrEmpty)
        {
            return logicalProcessors;
        }

        var members = logicalProcessors.ToHashSet();
        var ordered = ImmutableArray.CreateBuilder<int>(logicalProcessors.Length);

        foreach (var lp in globalPreference)
        {
            if (members.Remove(lp))
            {
                ordered.Add(lp);
            }
        }

        foreach (var lp in logicalProcessors)
        {
            if (members.Contains(lp))
            {
                ordered.Add(lp);
            }
        }

        return ordered.MoveToImmutable();
    }

    private static TopologyKind DetermineKind(ImmutableArray<PerformanceDomain> domains)
    {
        if (domains.Length <= 1)
        {
            return TopologyKind.Uniform;
        }

        // Efficiency class is checked first: on a hybrid CPU it is the dominant difference,
        // and P/E domains frequently share one cache instance so the cache test would miss it.
        if (domains.Select(d => d.EfficiencyClass).Distinct().Count() > 1)
        {
            return TopologyKind.Hybrid;
        }

        var largest = domains.Max(d => d.LastLevelCacheBytes);
        var smallest = domains.Min(d => d.LastLevelCacheBytes);

        var materiallyDifferent = smallest == 0
            ? largest > 0
            : largest > smallest * AsymmetricCacheRatio;

        return materiallyDifferent ? TopologyKind.AsymmetricCache : TopologyKind.SymmetricMultiDomain;
    }

    private static int SelectGameDomain(
        ImmutableArray<PerformanceDomain> domains,
        TopologyKind kind,
        ImmutableArray<int> preferredCoreOrder)
    {
        if (domains.Length == 1)
        {
            return 0;
        }

        return kind switch
        {
            // Fastest cores win outright. Cache is shared on these parts anyway.
            TopologyKind.Hybrid => BestBy(
                domains,
                d => ((long)d.EfficiencyClass, d.LastLevelCacheBytes, (long)d.LogicalProcessorCount)),

            // Cache wins, and it wins over the favoured-core ranking. That is the entire
            // premise of a V-Cache part: the frequency die clocks higher and still loses.
            TopologyKind.AsymmetricCache => BestBy(
                domains,
                d => (d.LastLevelCacheBytes, d.CacheBytesPerPhysicalCore, (long)d.LogicalProcessorCount)),

            // Nothing separates them on paper, so defer to the platform's own ranking of
            // which physical cores boost highest.
            _ => PreferenceWinner(domains, preferredCoreOrder),
        };
    }

    private static int BestBy(
        ImmutableArray<PerformanceDomain> domains,
        Func<PerformanceDomain, (long, long, long)> score)
    {
        var bestIndex = 0;
        var best = score(domains[0]);

        for (var i = 1; i < domains.Length; i++)
        {
            var candidate = score(domains[i]);
            if (candidate.CompareTo(best) > 0)
            {
                best = candidate;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static int PreferenceWinner(
        ImmutableArray<PerformanceDomain> domains,
        ImmutableArray<int> preferredCoreOrder)
    {
        foreach (var lp in preferredCoreOrder)
        {
            for (var i = 0; i < domains.Length; i++)
            {
                if (domains[i].LogicalProcessors.Contains(lp))
                {
                    return i;
                }
            }
        }

        // No ranking available: the lowest-numbered domain is as good a choice as any, and
        // being deterministic matters more than being clever.
        return 0;
    }

    private static ImmutableArray<PerformanceDomain> AssignClasses(
        ImmutableArray<PerformanceDomain> domains,
        TopologyKind kind,
        int gameDomainIndex)
    {
        if (domains.Length == 1)
        {
            return [domains[0] with { Class = DomainClass.Performance }];
        }

        // Distinct efficiency classes, weakest first. Only meaningful on hybrid parts.
        var efficiencyRanks = domains
            .Select(d => d.EfficiencyClass)
            .Distinct()
            .OrderBy(c => c)
            .ToArray();

        var result = ImmutableArray.CreateBuilder<PerformanceDomain>(domains.Length);

        for (var i = 0; i < domains.Length; i++)
        {
            var domain = domains[i];

            DomainClass assigned;
            if (i == gameDomainIndex)
            {
                assigned = DomainClass.Performance;
            }
            else if (kind == TopologyKind.Hybrid
                     && efficiencyRanks.Length >= 3
                     && domain.EfficiencyClass == efficiencyRanks[0])
            {
                // The weakest tier of a three-tier hybrid part — Intel's SoC-tile LP-E
                // cores. Never used for game work, and worth distinguishing from ordinary
                // E-cores when deciding where background work is cheap to place.
                assigned = DomainClass.LowPowerEfficiency;
            }
            else
            {
                assigned = DomainClass.Efficiency;
            }

            result.Add(domain with { Class = assigned });
        }

        return result.MoveToImmutable();
    }
}
