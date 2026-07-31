using System.Collections.Immutable;
using GamerGod.Core.Forensics;

namespace GamerGod.Core.Measurement;

/// <summary>
/// One capture, kept whole: the per-frame rows and the statistics computed from them.
///
/// <para>
/// Frame times alone are enough to say a frame was late and not enough to say what was
/// occupying it. Keeping the rows means stutter attribution can run on a capture a user
/// actually took, instead of only on a CSV somebody saved by hand.
/// </para>
///
/// <para>
/// <b>The window is the point of this type.</b> A capture discards its opening seconds, because
/// shader compilation, first-time file reads and clock ramp make them unrepresentative, and the
/// A/B verdict deliberately excludes them. The rows and the series are windowed together, once,
/// so attribution cannot name a cause for a hitch the measurement beside it refuses to count.
/// There is no constructor that lets the two disagree.
/// </para>
/// </summary>
public sealed class CapturedFrames
{
    private CapturedFrames(ImmutableArray<FrameRecord> frames, FrameSeries series)
    {
        Frames = frames;
        Series = series;
    }

    /// <summary>A capture that collected nothing. Never a fabricated one.</summary>
    public static CapturedFrames Empty { get; } = new([], FrameSeries.FromFrameTimes([]));

    /// <summary>The rows inside the window, in capture order.</summary>
    public ImmutableArray<FrameRecord> Frames { get; }

    /// <summary>The frame times of those same rows, and the statistics over them.</summary>
    public FrameSeries Series { get; }

    /// <summary>
    /// Parses PresentMon output. The CSV is walked once: the series is derived from the rows
    /// rather than from a second pass over the text.
    /// </summary>
    public static CapturedFrames FromCsv(string csv, double discardFirstSeconds = 0) =>
        FromFrames(PresentMonCsv.ParseFrames(csv), discardFirstSeconds);

    /// <summary>
    /// Builds a capture from already-parsed rows, discarding a warm-up period from both the
    /// rows and the series.
    /// </summary>
    public static CapturedFrames FromFrames(
        IEnumerable<FrameRecord> frames, double discardFirstSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(frames);

        // Materialised once. ParseFrames is a lazy iterator, so a second enumeration would
        // re-parse the entire capture.
        var kept = Window([.. frames], discardFirstSeconds);

        return new CapturedFrames(kept, FrameSeries.FromFrameTimes(FrameTimes(kept)));
    }

    /// <summary>
    /// Classifies every hitch in the window — the same frames, and therefore the same hitches,
    /// the series reports.
    /// </summary>
    public StutterReport Attribute(double hitchMultiple = StutterAttributor.DefaultHitchMultiple) =>
        StutterAttributor.Attribute(Frames, hitchMultiple);

    /// <summary>
    /// Drops leading rows until the warm-up period has elapsed, matching
    /// <see cref="FrameSeries.FromFrameTimes"/> exactly — including its refusal to discard
    /// everything, because a short run is better reported as short than as empty.
    /// </summary>
    private static ImmutableArray<FrameRecord> Window(
        ImmutableArray<FrameRecord> all, double discardFirstSeconds)
    {
        if (discardFirstSeconds <= 0)
        {
            return all;
        }

        var budget = discardFirstSeconds * 1000.0;
        var elapsed = 0.0;
        var discarded = 0;
        var cut = 0;

        for (var i = 0; i < all.Length && elapsed < budget; i++)
        {
            if (Usable(all[i]) is { } frameTime)
            {
                elapsed += frameTime;
                discarded++;
            }

            // Rows the warm-up period covered but PresentMon could not time go with it. They
            // describe the same discarded seconds.
            cut = i + 1;
        }

        var measured = all.Count(f => Usable(f) is not null);

        return discarded < measured ? all[cut..] : all;
    }

    private static IEnumerable<double> FrameTimes(ImmutableArray<FrameRecord> frames)
    {
        foreach (var frame in frames)
        {
            if (Usable(frame) is { } frameTime)
            {
                yield return frameTime;
            }
        }
    }

    /// <summary>
    /// The frame time, when there is one a series would accept. The same predicate
    /// <see cref="FrameSeries"/> applies, so the two cannot count different frames.
    /// </summary>
    private static double? Usable(FrameRecord frame) =>
        frame.MsBetweenPresents is { } frameTime && frameTime > 0 && double.IsFinite(frameTime)
            ? frameTime
            : null;
}
