using System.Collections.Immutable;
using GamerGod.Core.Engine;

namespace GamerGod.Core.Autotune;

/// <summary>One arm of one comparison: a factor, and the settings to measure it under.</summary>
public sealed record TuningStep
{
    public required TuningFactor Factor { get; init; }

    /// <summary>Settings with the factor off.</summary>
    public required AmbientOptions Without { get; init; }

    /// <summary>The same settings with only that factor on.</summary>
    public required AmbientOptions With { get; init; }
}

/// <summary>
/// The search, which is deliberately the least interesting part of autotune.
///
/// <para>
/// One factor at a time, in a fixed order, each measured against the best configuration found so
/// far. Not a grid: four booleans is sixteen configurations, and at seven runs per arm and
/// thirty seconds a run that is over four hours of somebody's evening to answer a question that
/// one-factor-at-a-time answers in four comparisons.
/// </para>
///
/// <para>
/// The cost of one-factor-at-a-time is that it cannot see interactions — a pair that only helps
/// together is invisible to it. That is a real limitation and the honest trade: an interaction
/// worth four hours of measurement is not one this product should be finding unattended.
/// </para>
/// </summary>
public static class TuningPlan
{
    /// <summary>
    /// The starting point: everything off.
    ///
    /// <para>
    /// Not the shipped defaults. Autotune's whole purpose is to find out which settings earn
    /// their place on <em>this</em> machine, and starting from a configuration somebody already
    /// chose would measure the deltas around that choice rather than the choice itself.
    /// </para>
    /// </summary>
    public static AmbientOptions Nothing { get; } = new()
    {
        ConfineToAmbientDomain = false,
        DemoteToEfficiencyMode = false,
        ManagePowerScheme = false,
        Services = [],

        // Never during a tuning pass. A dry run measures nothing, and a resident caller would
        // make the arms differ by more than the factor under test.
        DryRun = false,
        CallerStaysResident = false,
    };

    /// <summary>
    /// The next comparison to run, given what has been decided so far.
    ///
    /// <para>
    /// Each step measures one factor against the best configuration established by the previous
    /// steps, so a factor that only pays off once another is on is still found — as long as the
    /// other comes first in the order, which is why the order is fixed and reasoned about rather
    /// than arbitrary.
    /// </para>
    /// </summary>
    public static TuningStep? Next(ImmutableArray<TuningDecision> decided)
    {
        var settled = decided.IsDefaultOrEmpty
            ? []
            : decided.Select(d => d.Factor).ToHashSet();

        foreach (var factor in TuningFactors.InTestOrder)
        {
            if (settled.Contains(factor))
            {
                continue;
            }

            var baseline = BestSoFar(decided);

            return new TuningStep
            {
                Factor = factor,
                Without = baseline.With(factor, on: false),
                With = baseline.With(factor, on: true),
            };
        }

        return null;
    }

    /// <summary>
    /// The configuration built from every factor adopted so far.
    ///
    /// <para>
    /// A factor that was measured and not adopted stays off, including one that regressed. There
    /// is no separate "rejected" state to carry: the recommendation is simply what survived.
    /// </para>
    /// </summary>
    public static AmbientOptions BestSoFar(ImmutableArray<TuningDecision> decided)
    {
        var options = Nothing;

        if (decided.IsDefaultOrEmpty)
        {
            return options;
        }

        foreach (var decision in decided.Where(d => d.Adopted))
        {
            options = options.With(decision.Factor, on: true);
        }

        return options;
    }

    /// <summary>Builds the finished report once every factor has been settled.</summary>
    public static TuningReport Conclude(ImmutableArray<TuningDecision> decided) => new()
    {
        Decisions = decided.IsDefault ? [] : decided,
        Recommended = BestSoFar(decided),
    };

    /// <summary>
    /// How many comparisons a full pass runs. Fixed in advance, and there is no peeking.
    ///
    /// <para>
    /// A budget decided before the first measurement is what stops a tuner running until it
    /// finds something — which, given enough attempts, it always will.
    /// </para>
    /// </summary>
    public static int TotalComparisons => TuningFactors.InTestOrder.Length;
}
