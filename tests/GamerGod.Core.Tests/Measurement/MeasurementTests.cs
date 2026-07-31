using System.Collections.Immutable;
using GamerGod.Core.Forensics;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Measurement;

/// <summary>
/// Statistics verified against values computed by hand. A percentile implementation that is
/// subtly wrong produces confident, plausible, incorrect numbers — the worst failure mode
/// available to this project, because nothing downstream can detect it.
/// </summary>
public sealed class FrameSeriesTests
{
    [Fact]
    public void Average_frame_rate_is_the_reciprocal_of_the_mean_frame_time()
    {
        // Ten frames at exactly 10 ms is exactly 100 fps.
        var series = FrameSeries.FromFrameTimes(Enumerable.Repeat(10.0, 10));

        Assert.Equal(10.0, series.AverageFrameTimeMs, 6);
        Assert.Equal(100.0, series.AverageFps, 6);
    }

    [Fact]
    public void Average_fps_is_not_the_mean_of_per_frame_rates()
    {
        // The classic error. Five frames at 10 ms and five at 100 ms average 55 ms, which is
        // 18.18 fps. Averaging the per-frame rates instead gives 55 fps - a threefold
        // overstatement, and the kind of mistake that makes every result look good.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 5).Concat(Enumerable.Repeat(100.0, 5)));

        Assert.Equal(55.0, series.AverageFrameTimeMs, 6);
        Assert.Equal(1000.0 / 55.0, series.AverageFps, 6);
        Assert.NotEqual(55.0, series.AverageFps, 1);
    }

    [Fact]
    public void Percentiles_interpolate_between_samples()
    {
        // 1..10 ms. The 50th percentile of a 10-sample set sits at rank 4.5, i.e. halfway
        // between the 5th and 6th values: 5.5.
        var series = FrameSeries.FromFrameTimes(Enumerable.Range(1, 10).Select(i => (double)i));

        Assert.Equal(1.0, series.Percentile(0), 6);
        Assert.Equal(5.5, series.Percentile(50), 6);
        Assert.Equal(10.0, series.Percentile(100), 6);
    }

    [Fact]
    public void The_one_percent_low_reflects_the_slow_frames_not_the_fast_ones()
    {
        // 99 frames at 10 ms and one at 100 ms. The average is barely affected; the 1% low must
        // collapse, because that single frame is exactly what the player noticed.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 99).Append(100.0));

        Assert.InRange(series.AverageFps, 90, 100);
        Assert.Equal(10.0, series.OnePercentLowFps, 3);   // the worst frame: 100 ms
        Assert.True(
            series.OnePercentLowFps < series.AverageFps,
            "the 1% low must be worse than the average, or it is measuring the wrong thing");
    }

    [Fact]
    public void Interpolation_would_have_hidden_that_outlier_which_is_why_it_is_not_used()
    {
        // The interpolated 99th percentile of this run is about 10.9 ms - roughly 92 fps -
        // because the single 100 ms frame is averaged against its 10 ms neighbour. That is a
        // defensible statistic and completely the wrong one to label "1% low".
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 99).Append(100.0));

        Assert.InRange(series.NinetyNinthPercentileFrameTimeMs, 10.0, 12.0);
        Assert.True(
            series.OnePercentLowFps < 1000.0 / series.NinetyNinthPercentileFrameTimeMs,
            "the reported 1% low must be the harsher of the two, not the flattering one");
    }

    [Fact]
    public void Both_conventions_are_available_and_named_for_what_they_are()
    {
        // Calling two different statistics by one name is how comparisons between tools become
        // nonsense, so each is exposed under its own name.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 90).Concat(Enumerable.Repeat(50.0, 10)));

        Assert.True(series.OnePercentLowFps > 0);
        Assert.True(series.NinetyNinthPercentileFrameTimeMs > 0);
    }

    [Fact]
    public void The_point_one_percent_low_is_at_least_as_harsh_as_the_one_percent_low()
    {
        // A smaller tail can only be worse or equal. If this ever inverts, the fraction maths
        // is wrong.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 990)
                .Concat(Enumerable.Repeat(40.0, 9))
                .Append(300.0));

        Assert.True(series.PointOnePercentLowFps <= series.OnePercentLowFps);
        Assert.Equal(1000.0 / 300.0, series.PointOnePercentLowFps, 2);
    }

    [Fact]
    public void A_steady_run_is_more_consistent_than_a_jittery_one()
    {
        var steady = FrameSeries.FromFrameTimes(Enumerable.Repeat(10.0, 100));
        var jittery = FrameSeries.FromFrameTimes(
            Enumerable.Range(0, 100).Select(i => i % 2 == 0 ? 6.0 : 14.0));

        Assert.Equal(100.0, steady.ConsistencyPercent, 3);
        Assert.True(
            jittery.ConsistencyPercent < 20,
            $"a run alternating 6 ms and 14 ms should score poorly, got {jittery.ConsistencyPercent}");
    }

    [Fact]
    public void One_hitch_does_not_dominate_consistency_the_way_it_would_a_standard_deviation()
    {
        // 999 identical frames and one enormous one. Standard deviation would be wrecked;
        // consistency should stay near 100%, because 999 frames out of 1000 did arrive on time.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(10.0, 999).Append(400.0));

        Assert.InRange(series.ConsistencyPercent, 99.0, 100.0);
    }

    [Fact]
    public void Hitches_are_frames_beyond_a_multiple_of_the_median()
    {
        var series = FrameSeries.FromFrameTimes(
            [10, 10, 10, 10, 10, 10, 10, 10, 45, 10, 10, 90]);

        var hitches = series.Hitches(multiple: 2.0);

        Assert.Equal(2, hitches.Length);
        Assert.Contains(45.0, hitches);
        Assert.Contains(90.0, hitches);
    }

    [Fact]
    public void Warm_up_frames_are_discarded()
    {
        // Shader compilation, first-time file reads and clock ramp make the opening seconds
        // unrepresentative. Two seconds of 100 ms frames, then a steady run.
        var series = FrameSeries.FromFrameTimes(
            Enumerable.Repeat(100.0, 20).Concat(Enumerable.Repeat(10.0, 100)),
            discardFirstSeconds: 2.0);

        Assert.Equal(100, series.Count);
        Assert.Equal(10.0, series.AverageFrameTimeMs, 3);
    }

    [Fact]
    public void Discarding_never_empties_the_series()
    {
        // A short run should be reported as short, not as nothing.
        var series = FrameSeries.FromFrameTimes(Enumerable.Repeat(10.0, 5), discardFirstSeconds: 60);

        Assert.False(series.IsEmpty);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Impossible_frame_times_are_rejected_rather_than_averaged_in(double bad)
    {
        var series = FrameSeries.FromFrameTimes([10.0, bad, 10.0]);

        Assert.Equal(2, series.Count);
        Assert.Equal(10.0, series.AverageFrameTimeMs, 6);
    }

    [Fact]
    public void An_empty_series_reports_zero_rather_than_throwing()
    {
        var series = FrameSeries.FromFrameTimes([]);

        Assert.True(series.IsEmpty);
        Assert.Equal(0, series.AverageFps);
        Assert.Equal(0, series.OnePercentLowFps);
        Assert.Equal(0, series.ConsistencyPercent);
        Assert.Empty(series.Hitches());
        Assert.Equal(0, series.Percentile(99));
    }
}

/// <summary>
/// The honesty rule, made mechanical. These are the tests that keep Charter Article VII true
/// once more than one person is writing code against a result.
/// </summary>
public sealed class AbComparisonTests
{
    private static FrameSeries Run(double frameTimeMs, double jitter = 0.0, int seed = 1)
    {
        var random = new Random(seed);
        return FrameSeries.FromFrameTimes(
            Enumerable.Range(0, 500)
                .Select(_ => frameTimeMs + ((random.NextDouble() - 0.5) * 2 * jitter)));
    }

    [Fact]
    public void Identical_arms_report_no_measurable_effect()
    {
        // The single most important assertion in the project. Two arms drawn from the same
        // behaviour must never produce a claim, however the point estimate happens to fall.
        var baseline = Enumerable.Range(0, 6).Select(i => Run(10.0, 0.5, i)).ToImmutableArray();
        var candidate = Enumerable.Range(100, 6).Select(i => Run(10.0, 0.5, i)).ToImmutableArray();

        var result = AbComparison.Compare(Metric.AverageFps, baseline, candidate);

        Assert.Equal(MeasurementVerdict.NoMeasurableEffect, result.Verdict);
        Assert.Contains("No measurable effect", result.Explain(), StringComparison.Ordinal);
        Assert.Contains("includes zero", result.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_large_real_difference_is_reported_as_an_improvement()
    {
        // 10 ms against 8 ms is a genuine 25% gain and must be detected, or the harness is
        // useless in the other direction.
        var baseline = Enumerable.Range(0, 6).Select(i => Run(10.0, 0.2, i)).ToImmutableArray();
        var candidate = Enumerable.Range(100, 6).Select(i => Run(8.0, 0.2, i)).ToImmutableArray();

        var result = AbComparison.Compare(Metric.AverageFps, baseline, candidate);

        Assert.Equal(MeasurementVerdict.Improvement, result.Verdict);
        Assert.InRange(result.DeltaPercent, 20, 30);
        Assert.True(result.LowerPercent > 0, "an improvement's interval must exclude zero");
    }

    [Fact]
    public void A_regression_is_reported_as_plainly_as_an_improvement()
    {
        var baseline = Enumerable.Range(0, 6).Select(i => Run(8.0, 0.2, i)).ToImmutableArray();
        var candidate = Enumerable.Range(100, 6).Select(i => Run(10.0, 0.2, i)).ToImmutableArray();

        var result = AbComparison.Compare(Metric.AverageFps, baseline, candidate);

        Assert.Equal(MeasurementVerdict.Regression, result.Verdict);
        Assert.True(result.UpperPercent < 0);
        Assert.Contains("got worse", result.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void Too_few_runs_produces_insufficient_rather_than_a_wide_guess()
    {
        // A number with a huge interval gets quoted without its interval. Refusing to produce
        // one is the only reliable defence.
        var baseline = new[] { Run(10.0, 0.2, 1), Run(10.0, 0.2, 2) };
        var candidate = new[] { Run(8.0, 0.2, 3), Run(8.0, 0.2, 4) };

        var result = AbComparison.Compare(Metric.AverageFps, baseline, candidate);

        Assert.Equal(MeasurementVerdict.Insufficient, result.Verdict);
        Assert.Equal(0, result.DeltaPercent);
        Assert.Contains("Not enough data", result.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_comparison_is_reproducible()
    {
        // A result that changes each time it is recomputed from the same samples is not a
        // measurement. The bootstrap is seeded for exactly this reason.
        var baseline = Enumerable.Range(0, 5).Select(i => Run(10.0, 1.0, i)).ToImmutableArray();
        var candidate = Enumerable.Range(50, 5).Select(i => Run(9.5, 1.0, i)).ToImmutableArray();

        var first = AbComparison.Compare(Metric.OnePercentLowFps, baseline, candidate);
        var second = AbComparison.Compare(Metric.OnePercentLowFps, baseline, candidate);

        Assert.Equal(first.Verdict, second.Verdict);
        Assert.Equal(first.LowerPercent, second.LowerPercent, 10);
        Assert.Equal(first.UpperPercent, second.UpperPercent, 10);
    }

    [Fact]
    public void No_explanation_ever_states_a_bare_percentage_without_its_interval()
    {
        var baseline = Enumerable.Range(0, 6).Select(i => Run(10.0, 0.2, i)).ToImmutableArray();

        foreach (var candidate in new[]
                 {
                     Enumerable.Range(100, 6).Select(i => Run(8.0, 0.2, i)).ToImmutableArray(),
                     Enumerable.Range(200, 6).Select(i => Run(10.0, 0.2, i)).ToImmutableArray(),
                     Enumerable.Range(300, 6).Select(i => Run(12.0, 0.2, i)).ToImmutableArray(),
                 })
        {
            var result = AbComparison.Compare(Metric.AverageFps, baseline, candidate);
            var text = result.Explain();

            if (result.Verdict is MeasurementVerdict.Improvement or MeasurementVerdict.Regression)
            {
                Assert.Contains("confidence", text, StringComparison.Ordinal);
                Assert.Contains(" to ", text, StringComparison.Ordinal);
            }

            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    [Fact]
    public void A_wider_confidence_level_produces_a_wider_interval()
    {
        var baseline = Enumerable.Range(0, 6).Select(i => Run(10.0, 1.0, i)).ToImmutableArray();
        var candidate = Enumerable.Range(100, 6).Select(i => Run(9.0, 1.0, i)).ToImmutableArray();

        var narrow = AbComparison.Compare(Metric.AverageFps, baseline, candidate, 0.80);
        var wide = AbComparison.Compare(Metric.AverageFps, baseline, candidate, 0.99);

        Assert.True(
            wide.UpperPercent - wide.LowerPercent > narrow.UpperPercent - narrow.LowerPercent,
            "99% confidence must span more than 80%");
    }
}

/// <summary>The capture seam, and what happens when there is nothing to measure with.</summary>
public sealed class FrameCaptureTests
{
    [Fact]
    public async Task An_unavailable_source_says_so_and_returns_nothing()
    {
        var capture = new UnavailableFrameCapture(
            "Intel PresentMon is not installed.",
            "Install it with: winget install --id Intel.PresentMon");

        Assert.False(capture.Source.IsAvailable);

        var result = await capture.CaptureAsync(null, TimeSpan.FromSeconds(1), default);

        // Empty, never fabricated. A plausible-looking invented series would be
        // indistinguishable from a measurement to everything downstream.
        Assert.True(result.Series.IsEmpty);
    }

    [Fact]
    public async Task A_run_without_a_capture_source_reports_unmeasured_not_zero()
    {
        var runner = new AbRunner(new UnavailableFrameCapture("No capture source is installed."));

        var run = await runner.RunAsync(
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            processId: null,
            perRun: TimeSpan.FromMilliseconds(1),
            repetitions: 3);

        Assert.False(run.HasData);
        Assert.Empty(run.Compare());

        var text = run.Explain();
        Assert.StartsWith("Unmeasured.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0%", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_runner_interleaves_the_arms_rather_than_sequencing_them()
    {
        // Sequential runs bake thermal drift into the difference and reliably flatter whichever
        // arm went second. The order must alternate.
        var order = new List<string>();
        var capture = new StubCapture(10.0);
        var runner = new AbRunner(capture);

        await runner.RunAsync(
            _ => { order.Add("A"); return ValueTask.CompletedTask; },
            _ => { order.Add("B"); return ValueTask.CompletedTask; },
            processId: null,
            perRun: TimeSpan.Zero,
            repetitions: 4);

        Assert.Equal(["A", "B", "B", "A", "A", "B", "B", "A"], order);
    }

    [Fact]
    public async Task Both_arms_get_the_requested_number_of_runs()
    {
        var runner = new AbRunner(new StubCapture(10.0));

        var run = await runner.RunAsync(
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            processId: null,
            perRun: TimeSpan.Zero,
            repetitions: 5);

        Assert.Equal(5, run.Baseline.Length);
        Assert.Equal(5, run.Candidate.Length);
        Assert.True(run.HasData);
    }

    [Fact]
    public async Task Comparing_a_real_run_puts_the_percentile_lows_before_the_average()
    {
        // Ordering is a deliberate editorial choice: leading with average frame rate is the
        // habit this project exists to break.
        var runner = new AbRunner(new StubCapture(10.0));

        var run = await runner.RunAsync(
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            processId: null,
            perRun: TimeSpan.Zero,
            repetitions: 4);

        var results = run.Compare();

        Assert.Equal(Metric.OnePercentLowFps, results[0].Metric);
        Assert.Equal(Metric.AverageFps, results[^1].Metric);
    }

    private sealed class StubCapture(double frameTimeMs) : IFrameCapture
    {
        private int _call;

        public CaptureSource Source { get; } = new() { Name = "stub", IsAvailable = true };

        public ValueTask<CapturedFrames> CaptureAsync(
            int? processId, TimeSpan duration, CancellationToken cancellationToken)
        {
            // A touch of variation per call so the bootstrap has something to work with.
            var random = new Random(_call++);
            return ValueTask.FromResult(CapturedFrames.FromFrames(
                Enumerable.Range(0, 300).Select(i => new FrameRecord
                {
                    Index = i,
                    MsBetweenPresents = frameTimeMs + ((random.NextDouble() - 0.5) * 0.4),
                })));
        }
    }
}
