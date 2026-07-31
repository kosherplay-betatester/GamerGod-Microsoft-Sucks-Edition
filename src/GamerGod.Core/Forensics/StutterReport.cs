using System.Collections.Immutable;
using System.Globalization;

namespace GamerGod.Core.Forensics;

/// <summary>One late frame and what the capture said was occupying it.</summary>
public sealed record StallAttribution
{
    /// <summary>Position of the frame in the capture.</summary>
    public required int FrameIndex { get; init; }

    /// <summary>Timestamp within the capture, when PresentMon provided one.</summary>
    public double? TimeInMs { get; init; }

    public required double FrameTimeMs { get; init; }

    /// <summary>How much longer this frame took than the capture's median frame.</summary>
    public required double ExcessMs { get; init; }

    public required StallCause Cause { get; init; }

    /// <summary>
    /// The measurements this verdict was read from, in a sentence. A verdict without its
    /// figures attached is an opinion, and it cannot be argued with.
    /// </summary>
    public required string Evidence { get; init; }

    /// <summary>
    /// The datum that was missing, when the frame is <see cref="StallCause.Unattributable"/>
    /// because a column was <c>NA</c>. Null otherwise — including when every column was present
    /// and no rule matched, which is a different and equally honest kind of "we do not know".
    /// </summary>
    public string? MissingColumn { get; init; }
}

/// <summary>How many hitches one cause accounts for, and how much time they cost.</summary>
public sealed record CauseTally
{
    public required StallCause Cause { get; init; }

    public required int Count { get; init; }

    /// <summary>Total time these frames took beyond the capture's median frame.</summary>
    public required double TotalExcessMs { get; init; }
}

/// <summary>
/// Every hitch in a capture, classified.
///
/// <para>
/// The ranking is computed here rather than by callers, so that no two places in the product
/// can order the same causes differently, and so that <see cref="StallCause.Unattributable"/>
/// cannot be filtered out of the top of the list by whoever happens to be printing it.
/// </para>
/// </summary>
public sealed record StutterReport
{
    /// <summary>Rows parsed, including any PresentMon could not put a frame time on.</summary>
    public required int FrameCount { get; init; }

    /// <summary>Rows that had a usable frame time. The rest are visible as the difference.</summary>
    public required int MeasuredFrameCount { get; init; }

    public required double MedianFrameTimeMs { get; init; }

    /// <summary>Frames longer than this were examined. Median times the multiple.</summary>
    public required double HitchThresholdMs { get; init; }

    /// <summary>Every hitch, in capture order, each with a cause.</summary>
    public required ImmutableArray<StallAttribution> Hitches { get; init; }

    public int HitchCount => Hitches.Length;

    public int UnattributableCount =>
        Hitches.Count(h => h.Cause == StallCause.Unattributable);

    /// <summary>
    /// Causes ordered by how many hitches they account for, then by the time those frames cost.
    ///
    /// <para>
    /// The time is the tie-break rather than the enum's declaration order, so that two causes
    /// with one hitch each are separated by something a reader would care about instead of by
    /// an accident of how the enum was typed.
    /// </para>
    /// </summary>
    public ImmutableArray<CauseTally> Ranked =>
    [
        .. Hitches
            .GroupBy(h => h.Cause)
            .Select(g => new CauseTally
            {
                Cause = g.Key,
                Count = g.Count(),
                TotalExcessMs = g.Sum(h => h.ExcessMs),
            })
            .OrderByDescending(t => t.Count)
            .ThenByDescending(t => t.TotalExcessMs)
            .ThenBy(t => t.Cause),
    ];

    /// <summary>
    /// The leading causes, one short line each, ready to print. Empty when there were no
    /// hitches — there is no line to write about frames that all arrived on time.
    /// </summary>
    public ImmutableArray<string> TopCauses(int max = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);

        return
        [
            .. Ranked.Take(max).Select(t => string.Create(
                CultureInfo.InvariantCulture,
                $"{t.Cause.Label()} — {t.Count} of {HitchCount} hitches")),
        ];
    }

    /// <summary>One sentence about the capture as a whole.</summary>
    public string Explain()
    {
        if (MeasuredFrameCount == 0)
        {
            return "No frame times were captured, so there is nothing to attribute.";
        }

        if (HitchCount == 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"No frame took longer than {HitchThresholdMs:0.0} ms "
                + $"(median {MedianFrameTimeMs:0.0} ms) across {MeasuredFrameCount} frames.");
        }

        // The unattributable count is part of the headline, not a footnote. It is the number
        // that tells a reader how much of the rest to trust.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{HitchCount} of {MeasuredFrameCount} frames took longer than "
            + $"{HitchThresholdMs:0.0} ms (median {MedianFrameTimeMs:0.0} ms); "
            + $"{UnattributableCount} could not be attributed from the columns captured.");
    }
}
