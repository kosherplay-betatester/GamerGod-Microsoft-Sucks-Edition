using System.Collections.Immutable;

namespace GamerGod.Core.Measurement;

/// <summary>Which statistic a comparison is about.</summary>
public enum Metric
{
    AverageFps,
    OnePercentLowFps,
    PointOnePercentLowFps,
    ConsistencyPercent,
}

/// <summary>
/// What a measurement is allowed to claim.
///
/// <para>
/// This exists as a type rather than a number so that "no measurable effect" cannot be
/// accidentally rendered as a small positive. A caller has to handle the case explicitly,
/// which is the only reliable way to keep Charter Article VII true once several people are
/// writing UI against the same result.
/// </para>
/// </summary>
public enum MeasurementVerdict
{
    /// <summary>Not enough data to say anything. Never presented as a result.</summary>
    Insufficient,

    /// <summary>
    /// The confidence interval includes zero. The honest report is that nothing measurable
    /// happened — regardless of which way the point estimate happens to lean.
    /// </summary>
    NoMeasurableEffect,

    /// <summary>The whole interval is above zero.</summary>
    Improvement,

    /// <summary>The whole interval is below zero. Reported as plainly as an improvement.</summary>
    Regression,
}

/// <summary>The result of comparing two arms of a measurement.</summary>
public sealed record AbResult
{
    public required Metric Metric { get; init; }

    public required MeasurementVerdict Verdict { get; init; }

    public required double BaselineValue { get; init; }

    public required double CandidateValue { get; init; }

    /// <summary>Point estimate of the change, as a percentage of baseline.</summary>
    public required double DeltaPercent { get; init; }

    /// <summary>Lower bound of the confidence interval, in percent.</summary>
    public required double LowerPercent { get; init; }

    /// <summary>Upper bound of the confidence interval, in percent.</summary>
    public required double UpperPercent { get; init; }

    public required int BaselineRuns { get; init; }

    public required int CandidateRuns { get; init; }

    public required double ConfidenceLevel { get; init; }

    /// <summary>
    /// The result in the words GamerGod is allowed to use. Never a bare percentage, because a
    /// percentage without its interval is exactly how this category earned its reputation.
    /// </summary>
    public string Explain() => Verdict switch
    {
        MeasurementVerdict.Insufficient =>
            $"Not enough data to judge {Name(Metric)}. "
            + $"{BaselineRuns} and {CandidateRuns} run(s) captured; at least 3 of each are needed.",

        MeasurementVerdict.NoMeasurableEffect =>
            $"No measurable effect on {Name(Metric)}. "
            + $"The result sits between {LowerPercent:+0.0;-0.0}% and {UpperPercent:+0.0;-0.0}%, "
            + "which includes zero — so nothing here can be claimed either way.",

        MeasurementVerdict.Improvement =>
            $"{Name(Metric)} improved by {DeltaPercent:0.0}% "
            + $"({LowerPercent:0.0}% to {UpperPercent:0.0}%, {ConfidenceLevel * 100:0}% confidence).",

        // Deliberately as plain as the improvement wording. A tool that softens its bad news
        // has no credibility left when it reports good news.

        MeasurementVerdict.Regression =>
            $"{Name(Metric)} got worse by {Math.Abs(DeltaPercent):0.0}% "
            + $"({Math.Abs(UpperPercent):0.0}% to {Math.Abs(LowerPercent):0.0}%, "
            + $"{ConfidenceLevel * 100:0}% confidence).",

        _ => "Unknown result.",
    };

    private static string Name(Metric metric) => metric switch
    {
        Metric.AverageFps => "average frame rate",
        Metric.OnePercentLowFps => "1% low frame rate",
        Metric.PointOnePercentLowFps => "0.1% low frame rate",
        Metric.ConsistencyPercent => "frame time consistency",
        _ => "this metric",
    };
}

/// <summary>
/// Compares two sets of runs and reports the difference with an interval around it.
///
/// <para>
/// A single run is an anecdote. Several runs with a confidence interval is a measurement, and
/// the difference between the two is the entire reason this project can say anything at all.
/// </para>
/// </summary>
public static class AbComparison
{
    /// <summary>
    /// Fewest runs per arm before a verdict is offered. Below this the honest answer is
    /// "insufficient", not a number with a wide interval that somebody will quote anyway.
    /// </summary>
    public const int MinimumRunsPerArm = 3;

    private const int BootstrapResamples = 4000;

    /// <summary>
    /// Compares a candidate against a baseline for one metric.
    /// </summary>
    /// <param name="seed">
    /// Fixed so a comparison is reproducible. A measurement that gives a different answer each
    /// time it is recomputed from the same samples is not a measurement.
    /// </param>
    public static AbResult Compare(
        Metric metric,
        IEnumerable<FrameSeries> baseline,
        IEnumerable<FrameSeries> candidate,
        double confidenceLevel = 0.95,
        int seed = 20260729)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(confidenceLevel, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(confidenceLevel, 1);

        var a = baseline.Where(s => !s.IsEmpty).Select(s => Value(metric, s)).ToImmutableArray();
        var b = candidate.Where(s => !s.IsEmpty).Select(s => Value(metric, s)).ToImmutableArray();

        if (a.Length < MinimumRunsPerArm || b.Length < MinimumRunsPerArm)
        {
            return new AbResult
            {
                Metric = metric,
                Verdict = MeasurementVerdict.Insufficient,
                BaselineValue = a.IsEmpty ? 0 : a.Average(),
                CandidateValue = b.IsEmpty ? 0 : b.Average(),
                DeltaPercent = 0,
                LowerPercent = 0,
                UpperPercent = 0,
                BaselineRuns = a.Length,
                CandidateRuns = b.Length,
                ConfidenceLevel = confidenceLevel,
            };
        }

        var baseMean = a.Average();
        var candidateMean = b.Average();

        if (baseMean <= 0)
        {
            return new AbResult
            {
                Metric = metric,
                Verdict = MeasurementVerdict.Insufficient,
                BaselineValue = baseMean,
                CandidateValue = candidateMean,
                DeltaPercent = 0,
                LowerPercent = 0,
                UpperPercent = 0,
                BaselineRuns = a.Length,
                CandidateRuns = b.Length,
                ConfidenceLevel = confidenceLevel,
            };
        }

        var delta = (candidateMean - baseMean) / baseMean * 100.0;
        var (lower, upper) = BootstrapInterval(a, b, confidenceLevel, seed);

        // The rule the whole product rests on. An interval spanning zero means the samples do
        // not distinguish the two arms, so no claim is available - not a small one, not a
        // hedged one, none.
        var verdict = lower > 0 && upper > 0
            ? MeasurementVerdict.Improvement
            : lower < 0 && upper < 0
                ? MeasurementVerdict.Regression
                : MeasurementVerdict.NoMeasurableEffect;

        return new AbResult
        {
            Metric = metric,
            Verdict = verdict,
            BaselineValue = baseMean,
            CandidateValue = candidateMean,
            DeltaPercent = delta,
            LowerPercent = lower,
            UpperPercent = upper,
            BaselineRuns = a.Length,
            CandidateRuns = b.Length,
            ConfidenceLevel = confidenceLevel,
        };
    }

    /// <summary>
    /// Percentile bootstrap over the relative difference in means.
    ///
    /// <para>
    /// Bootstrapping rather than a t-test because frame-time run summaries are not reliably
    /// normal and the sample counts are small — resampling makes no distributional assumption,
    /// which matters when the answer is going to be shown to somebody as a fact.
    /// </para>
    /// </summary>
    private static (double Lower, double Upper) BootstrapInterval(
        ImmutableArray<double> baseline,
        ImmutableArray<double> candidate,
        double confidenceLevel,
        int seed)
    {
        var random = new Random(seed);
        var deltas = new double[BootstrapResamples];

        for (var i = 0; i < BootstrapResamples; i++)
        {
            var a = ResampleMean(baseline, random);
            var b = ResampleMean(candidate, random);

            deltas[i] = a <= 0 ? 0 : (b - a) / a * 100.0;
        }

        Array.Sort(deltas);

        var tail = (1.0 - confidenceLevel) / 2.0;
        return (Quantile(deltas, tail), Quantile(deltas, 1.0 - tail));
    }

    private static double ResampleMean(ImmutableArray<double> values, Random random)
    {
        var total = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            total += values[random.Next(values.Length)];
        }

        return total / values.Length;
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (sorted.Length == 0)
        {
            return 0;
        }

        var rank = q * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Length - 1);

        return sorted[lower] + ((sorted[upper] - sorted[lower]) * (rank - lower));
    }

    private static double Value(Metric metric, FrameSeries series) => metric switch
    {
        Metric.AverageFps => series.AverageFps,
        Metric.OnePercentLowFps => series.OnePercentLowFps,
        Metric.PointOnePercentLowFps => series.PointOnePercentLowFps,
        Metric.ConsistencyPercent => series.ConsistencyPercent,
        _ => 0,
    };
}
