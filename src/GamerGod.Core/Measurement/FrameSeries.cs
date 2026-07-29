using System.Collections.Immutable;

namespace GamerGod.Core.Measurement;

/// <summary>
/// A run of frame times, in milliseconds, with the statistics that actually describe how a
/// game felt.
///
/// <para>
/// Average frame rate is the least interesting number here and it is the only one most tools
/// report. What players notice is the slow frames: a run that averages 144 fps while dropping
/// to 40 twice a second feels far worse than a steady 100. So the percentile lows are computed
/// from the slowest <em>frames</em> and never from an average of averages, which is a common
/// mistake that quietly flatters every result.
/// </para>
/// </summary>
public sealed class FrameSeries
{
    private readonly double[] _sorted;

    private FrameSeries(ImmutableArray<double> frameTimesMs)
    {
        FrameTimesMs = frameTimesMs;
        _sorted = [.. frameTimesMs.Order()];
    }

    public ImmutableArray<double> FrameTimesMs { get; }

    public int Count => FrameTimesMs.Length;

    /// <summary>
    /// Builds a series, discarding a warm-up period.
    ///
    /// <para>
    /// The first seconds of any run are unrepresentative: shaders are still compiling, files
    /// are still being read for the first time, and clocks are still ramping. Including them
    /// makes every measurement noisier and makes the second arm of an A/B look better than the
    /// first purely because the machine had warmed up.
    /// </para>
    /// </summary>
    public static FrameSeries FromFrameTimes(
        IEnumerable<double> frameTimesMs, double discardFirstSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);

        var all = frameTimesMs.Where(t => t > 0 && double.IsFinite(t)).ToArray();

        if (discardFirstSeconds > 0)
        {
            var elapsed = 0.0;
            var skip = 0;

            while (skip < all.Length && elapsed < discardFirstSeconds * 1000.0)
            {
                elapsed += all[skip];
                skip++;
            }

            // Never discard everything. A short run is better reported as short than as empty.
            all = skip < all.Length ? all[skip..] : all;
        }

        return new FrameSeries([.. all]);
    }

    public bool IsEmpty => Count == 0;

    public double AverageFrameTimeMs => IsEmpty ? 0 : FrameTimesMs.Average();

    public double AverageFps => AverageFrameTimeMs <= 0 ? 0 : 1000.0 / AverageFrameTimeMs;

    public double MedianFrameTimeMs => Percentile(50);

    /// <summary>
    /// The 1% low, as frames per second: the mean of the slowest one percent of frames.
    ///
    /// <para>
    /// This is what reviewers and frame-time tools mean by "1% low", so a number from GamerGod
    /// is comparable with a number from anywhere else.
    /// </para>
    ///
    /// <para>
    /// Note what it deliberately is <em>not</em>. An interpolated 99th percentile of frame
    /// times is a different statistic and it is badly behaved in exactly the case that matters:
    /// with 100 frames, 99 at 10 ms and one at 100 ms, interpolation reports about 92 fps
    /// because the outlier is diluted by its neighbour. The worst frame is the one the player
    /// felt, so averaging the worst one percent reports 10 fps and is the honest answer. The
    /// interpolated form remains available as <see cref="Percentile"/> for callers who want it.
    /// </para>
    /// </summary>
    public double OnePercentLowFps => WorstFractionFps(0.01);

    /// <summary>
    /// The 0.1% low, as frames per second: the mean of the slowest one tenth of one percent of
    /// frames. The number that exposes rare, severe hitches.
    /// </summary>
    public double PointOnePercentLowFps => WorstFractionFps(0.001);

    /// <summary>
    /// The interpolated 99th percentile frame time, in milliseconds — the other convention
    /// sometimes labelled "1% low". Exposed separately and named for what it is, because
    /// calling two different statistics by the same name is how comparisons become nonsense.
    /// </summary>
    public double NinetyNinthPercentileFrameTimeMs => Percentile(99);

    private double WorstFractionFps(double fraction)
    {
        if (IsEmpty)
        {
            return 0;
        }

        // At least one frame, always. A 200-frame run still has a worst 0.1%: it is one frame.
        var take = Math.Max(1, (int)Math.Ceiling(Count * fraction));
        var worst = _sorted.AsSpan(_sorted.Length - take);

        var total = 0.0;
        foreach (var frame in worst)
        {
            total += frame;
        }

        return FpsFromFrameTime(total / take);
    }

    /// <summary>
    /// The share of frames within 10% of the median, as a percentage.
    ///
    /// <para>
    /// A deliberately plain definition of "smooth". Standard deviation is the obvious choice
    /// and a poor one here: a single 400 ms hitch moves it enough to swamp the difference
    /// between a steady run and a slightly-less-steady one, which is the comparison that
    /// matters. Counting how many frames arrived when expected is closer to what a player
    /// actually perceives, and it is a number they can reason about.
    /// </para>
    /// </summary>
    public double ConsistencyPercent
    {
        get
        {
            if (IsEmpty)
            {
                return 0;
            }

            var median = MedianFrameTimeMs;
            if (median <= 0)
            {
                return 0;
            }

            var lower = median * 0.9;
            var upper = median * 1.1;
            var within = 0;

            foreach (var frame in FrameTimesMs)
            {
                if (frame >= lower && frame <= upper)
                {
                    within++;
                }
            }

            return within * 100.0 / Count;
        }
    }

    /// <summary>Frames slower than <paramref name="multiple"/> times the median — the hitches.</summary>
    public ImmutableArray<double> Hitches(double multiple = 2.0)
    {
        if (IsEmpty)
        {
            return [];
        }

        var threshold = MedianFrameTimeMs * multiple;
        return [.. FrameTimesMs.Where(t => t > threshold)];
    }

    /// <summary>
    /// Linear-interpolated percentile of frame times, in milliseconds.
    /// </summary>
    public double Percentile(double percentile)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percentile);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percentile, 100);

        if (IsEmpty)
        {
            return 0;
        }

        if (_sorted.Length == 1)
        {
            return _sorted[0];
        }

        var rank = percentile / 100.0 * (_sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, _sorted.Length - 1);
        var fraction = rank - lower;

        return _sorted[lower] + ((_sorted[upper] - _sorted[lower]) * fraction);
    }

    private static double FpsFromFrameTime(double milliseconds) =>
        milliseconds <= 0 ? 0 : 1000.0 / milliseconds;
}
