using System.Collections.Immutable;
using GamerGod.Core.Forensics;

namespace GamerGod.Core.Measurement;

/// <summary>Where frame data comes from, and whether it is available at all.</summary>
public sealed record CaptureSource
{
    public required string Name { get; init; }

    public required bool IsAvailable { get; init; }

    /// <summary>Why it is unavailable, in words a user can act on. Null when available.</summary>
    public string? Unavailable { get; init; }

    /// <summary>What the user could do about it. Null when there is nothing to do.</summary>
    public string? Remedy { get; init; }
}

/// <summary>
/// Captures frame times for a running application.
///
/// <para>
/// The contract has one unusual rule and it is the important one: an implementation must never
/// invent a sample. If no source is available it says so and returns nothing. Interpolating,
/// simulating, or deriving a plausible-looking series would produce numbers that look like
/// measurements, get quoted as measurements, and be indistinguishable from the invented
/// figures this whole project exists as a reaction to.
/// </para>
/// </summary>
public interface IFrameCapture
{
    CaptureSource Source { get; }

    /// <summary>
    /// Captures for a duration. Returns an empty capture when no data could be collected —
    /// never a fabricated one.
    ///
    /// <para>
    /// The per-frame rows come back alongside the statistics rather than being discarded once
    /// the frame times have been read out of them. Without them there is no path from a capture
    /// a user actually took to a stutter attribution.
    /// </para>
    /// </summary>
    ValueTask<CapturedFrames> CaptureAsync(
        int? processId, TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>
/// The implementation used when nothing can measure this machine.
///
/// <para>
/// It exists so the absence of a capture source is a first-class, testable state rather than a
/// null reference or a silent zero. Everything downstream then reports "unmeasured" honestly
/// instead of showing a confident 0.0%.
/// </para>
/// </summary>
public sealed class UnavailableFrameCapture(string reason, string? remedy = null) : IFrameCapture
{
    public CaptureSource Source { get; } = new()
    {
        Name = "none",
        IsAvailable = false,
        Unavailable = reason,
        Remedy = remedy,
    };

    public ValueTask<CapturedFrames> CaptureAsync(
        int? processId, TimeSpan duration, CancellationToken cancellationToken) =>
        ValueTask.FromResult(CapturedFrames.Empty);
}

/// <summary>
/// Runs an interleaved A/B measurement.
///
/// <para>
/// Interleaved, not sequential. Running every A then every B bakes thermal drift, clock
/// behaviour and cache state straight into the difference, and reliably flatters whichever arm
/// went second. Alternating them costs nothing and removes the largest systematic error
/// available to this kind of test.
/// </para>
/// </summary>
public sealed class AbRunner(IFrameCapture capture)
{
    /// <summary>
    /// Applies each arm in turn and measures. <paramref name="applyBaseline"/> and
    /// <paramref name="applyCandidate"/> put the machine into each state; the runner does not
    /// know or care what they do.
    /// </summary>
    /// <param name="repetitions">Runs per arm. Below three no verdict is offered.</param>
    public async ValueTask<AbRun> RunAsync(
        Func<CancellationToken, ValueTask> applyBaseline,
        Func<CancellationToken, ValueTask> applyCandidate,
        int? processId,
        TimeSpan perRun,
        int repetitions = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(applyBaseline);
        ArgumentNullException.ThrowIfNull(applyCandidate);
        ArgumentOutOfRangeException.ThrowIfLessThan(repetitions, 1);

        if (!capture.Source.IsAvailable)
        {
            return new AbRun
            {
                Source = capture.Source,
                Baseline = [],
                Candidate = [],
            };
        }

        var baseline = ImmutableArray.CreateBuilder<CapturedFrames>(repetitions);
        var candidate = ImmutableArray.CreateBuilder<CapturedFrames>(repetitions);

        for (var i = 0; i < repetitions; i++)
        {
            // ABBA rather than ABAB: alternating the order of the pair as well as the arms
            // cancels first-position advantage, which ABAB leaves entirely intact.
            var baselineFirst = i % 2 == 0;

            if (baselineFirst)
            {
                baseline.Add(await MeasureAsync(applyBaseline, processId, perRun, cancellationToken));
                candidate.Add(await MeasureAsync(applyCandidate, processId, perRun, cancellationToken));
            }
            else
            {
                candidate.Add(await MeasureAsync(applyCandidate, processId, perRun, cancellationToken));
                baseline.Add(await MeasureAsync(applyBaseline, processId, perRun, cancellationToken));
            }
        }

        return new AbRun
        {
            Source = capture.Source,
            Baseline = baseline.ToImmutable(),
            Candidate = candidate.ToImmutable(),
        };
    }

    private async ValueTask<CapturedFrames> MeasureAsync(
        Func<CancellationToken, ValueTask> apply,
        int? processId,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        await apply(cancellationToken).ConfigureAwait(false);
        return await capture.CaptureAsync(processId, duration, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Both arms of a completed run, and where the data came from.</summary>
public sealed record AbRun
{
    public required CaptureSource Source { get; init; }

    public required ImmutableArray<CapturedFrames> Baseline { get; init; }

    public required ImmutableArray<CapturedFrames> Candidate { get; init; }

    public bool HasData => Source.IsAvailable && !Baseline.IsEmpty && !Candidate.IsEmpty;

    /// <summary>Compares every metric worth reporting.</summary>
    public ImmutableArray<AbResult> Compare(double confidenceLevel = 0.95)
    {
        if (!HasData)
        {
            return [];
        }

        var baseline = Baseline.Select(c => c.Series).ToImmutableArray();
        var candidate = Candidate.Select(c => c.Series).ToImmutableArray();

        return
        [
            AbComparison.Compare(Metric.OnePercentLowFps, baseline, candidate, confidenceLevel),
            AbComparison.Compare(Metric.PointOnePercentLowFps, baseline, candidate, confidenceLevel),
            AbComparison.Compare(Metric.ConsistencyPercent, baseline, candidate, confidenceLevel),
            AbComparison.Compare(Metric.AverageFps, baseline, candidate, confidenceLevel),
        ];
    }

    /// <summary>
    /// Classifies the hitches in the candidate arm, over exactly the frames its half of the
    /// verdict was computed from.
    ///
    /// <para>
    /// The candidate arm alone, not both: pooling them would compute one median across two
    /// different machine states and report causes for a machine that never existed. The arm's
    /// runs <em>are</em> pooled with each other — they are the same state, measured repeatedly,
    /// and a count of causes across the arm is the thing worth reading.
    /// </para>
    /// </summary>
    public StutterReport AttributeCandidate(
        double hitchMultiple = StutterAttributor.DefaultHitchMultiple) =>
        StutterAttributor.Attribute(Candidate.SelectMany(c => c.Frames), hitchMultiple);

    /// <summary>
    /// A single account of the run. When there is no capture source this says so plainly
    /// instead of reporting zeroes.
    /// </summary>
    public string Explain()
    {
        if (!Source.IsAvailable)
        {
            return $"Unmeasured. {Source.Unavailable}"
                   + (Source.Remedy is null ? string.Empty : $" {Source.Remedy}");
        }

        if (!HasData)
        {
            return "Unmeasured. The capture source is available but returned no frames.";
        }

        // 1% low first, deliberately. It is the number that correlates with how a game felt,
        // and putting average frame rate at the top would repeat the mistake this exists to fix.
        return string.Join(" ", Compare().Select(r => r.Explain()));
    }
}
