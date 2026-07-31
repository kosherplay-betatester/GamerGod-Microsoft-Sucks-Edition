using System.Collections.Immutable;
using System.Globalization;
using GamerGod.Core.Measurement;

namespace GamerGod.Core.Forensics;

/// <summary>
/// Works out which stage of the present pipeline was occupied while a frame was late.
///
/// <para>
/// It needs no ETW session of its own, no kernel driver, no handle to the game and no
/// administrator: PresentMon already writes the per-stage timings, and the whole question is
/// answerable from columns that are sitting in the CSV. That matters beyond convenience —
/// it means stutter attribution works on titles with kernel anti-cheat, where nothing may be
/// injected and no handle may be opened.
/// </para>
///
/// <para>
/// <b>The rule that governs the rest.</b> The rules below are a priority order, and a rule
/// whose inputs are <c>NA</c> stops the chain rather than being skipped. If GpuBound cannot be
/// evaluated, a lower rule matching tells us nothing about whether GpuBound would have — so the
/// answer is <see cref="StallCause.Unattributable"/>. This is deliberately conservative and it
/// costs real attributions: a frame whose MsCPUBusy fills it is still unattributable when
/// MsGPUBusy is <c>NA</c>, because the GPU may have been busier still. A confident wrong
/// attribution sends somebody off to fight a problem they do not have, and unlike a missing
/// answer, nothing downstream can detect it.
/// </para>
/// </summary>
public static class StutterAttributor
{
    /// <summary>
    /// Frames beyond this multiple of the median are examined — the same definition of "hitch"
    /// as <see cref="FrameSeries.Hitches"/>. Two definitions under one word would put two
    /// different numbers in front of a user with nothing to tell them apart.
    /// </summary>
    public const double DefaultHitchMultiple = 2.0;

    /// <summary>
    /// Classifies every hitch in a capture.
    /// </summary>
    public static StutterReport Attribute(
        IEnumerable<FrameRecord> frames, double hitchMultiple = DefaultHitchMultiple)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(hitchMultiple, 0);

        var all = frames.ToImmutableArray();
        var frameTimes = all
            .Select(f => f.MsBetweenPresents)
            .Where(t => t is > 0 && double.IsFinite(t.Value))
            .Select(t => t!.Value)
            .ToArray();

        if (frameTimes.Length == 0)
        {
            return new StutterReport
            {
                FrameCount = all.Length,
                MeasuredFrameCount = 0,
                MedianFrameTimeMs = 0,
                HitchThresholdMs = 0,
                Hitches = [],
            };
        }

        // Via FrameSeries rather than a second median implementation, so the threshold here and
        // the threshold everywhere else in the product cannot drift apart.
        var median = FrameSeries.FromFrameTimes(frameTimes).MedianFrameTimeMs;
        var threshold = median * hitchMultiple;

        var hitches = ImmutableArray.CreateBuilder<StallAttribution>();

        for (var i = 0; i < all.Length; i++)
        {
            var frame = all[i];

            if (frame.MsBetweenPresents is not { } frameTime || frameTime <= threshold)
            {
                continue;
            }

            // The immediately preceding present, whether or not it had a usable frame time —
            // present-mode transitions are about the sequence of presents, not of timings.
            var previous = i > 0 ? all[i - 1] : null;
            var (cause, missing, evidence) = Classify(frame, previous, frameTime);

            hitches.Add(new StallAttribution
            {
                FrameIndex = frame.Index,
                TimeInMs = frame.TimeInMs,
                FrameTimeMs = frameTime,
                ExcessMs = frameTime - median,
                Cause = cause,
                Evidence = evidence,
                MissingColumn = missing,
            });
        }

        return new StutterReport
        {
            FrameCount = all.Length,
            MeasuredFrameCount = frameTimes.Length,
            MedianFrameTimeMs = median,
            HitchThresholdMs = threshold,
            Hitches = hitches.ToImmutable(),
        };
    }

    private static (StallCause Cause, string? Missing, string Evidence) Classify(
        FrameRecord frame, FrameRecord? previous, double frameTime)
    {
        // --- Rules 1 and 2 both compare the two busy figures, so both need both. ---

        if (frame.MsCPUBusy is not { } cpuBusy)
        {
            return Unknown("MsCPUBusy", frameTime);
        }

        if (frame.MsGPUBusy is not { } gpuBusy)
        {
            return Unknown("MsGPUBusy", frameTime);
        }

        // 1. GpuBound: the GPU was the busier of the two and covered at least half the frame.
        if (gpuBusy > cpuBusy && gpuBusy >= frameTime * 0.5)
        {
            return (StallCause.GpuBound, null, string.Create(CultureInfo.InvariantCulture,
                $"The GPU was busy for {gpuBusy:0.0} ms of a {frameTime:0.0} ms frame; "
                + $"the game's CPU work took {cpuBusy:0.0} ms."));
        }

        // 2. GameCpuBound: the game's own CPU work was the busier and covered at least half.
        if (cpuBusy >= gpuBusy && cpuBusy >= frameTime * 0.5)
        {
            return (StallCause.GameCpuBound, null, string.Create(CultureInfo.InvariantCulture,
                $"The game's CPU work took {cpuBusy:0.0} ms of a {frameTime:0.0} ms frame; "
                + $"the GPU was busy for {gpuBusy:0.0} ms."));
        }

        if (frame.MsCPUWait is not { } cpuWait)
        {
            return Unknown("MsCPUWait", frameTime);
        }

        // 3. CpuBlocked: the CPU spent at least half the frame blocked while the GPU was busy
        //    for under a quarter of it. The GPU term is not decoration. A GPU-bound frame has a
        //    large MsCPUWait by construction - the CPU is waiting on the GPU - so a rule that
        //    read MsCPUWait alone would report CPU contention on precisely the frames where the
        //    CPU had nothing to do. The quarter is a gap, not a boundary: a frame whose GPU was
        //    busy between a quarter and a half of it satisfies neither rule and is reported as
        //    unattributable, which is what the data supports.
        if (cpuWait >= frameTime * 0.5 && gpuBusy < frameTime * 0.25)
        {
            return (StallCause.CpuBlocked, null, string.Create(CultureInfo.InvariantCulture,
                $"The CPU waited {cpuWait:0.0} ms of a {frameTime:0.0} ms frame "
                + $"while the GPU was busy for {gpuBusy:0.0} ms."));
        }

        // --- Rules 4 and 5 read the present mode instead of the timings. ---

        if (frame.IsIndependentFlip is not { } independentFlip)
        {
            return Unknown("PresentMode", frameTime);
        }

        // 4. LeftIndependentFlip: this frame is not an independent flip and the one
        //    before it was. Losing independent flip - to an overlay, a notification, a window
        //    losing focus - costs a frame, and the transition is the event worth naming.
        if (!independentFlip)
        {
            if (previous?.IsIndependentFlip is not { } wasIndependentFlip)
            {
                return (StallCause.Unattributable, "PresentMode", string.Create(CultureInfo.InvariantCulture,
                    $"The present mode of the frame before this one is not in the capture, "
                    + $"so a transition cannot be ruled out. The frame took {frameTime:0.0} ms."));
            }

            if (wasIndependentFlip)
            {
                return (StallCause.LeftIndependentFlip, null,
                    $"Presentation left independent flip on this frame: "
                    + $"'{previous.PresentMode}' became '{frame.PresentMode}'.");
            }
        }

        // 5. Compositor: the desktop compositor was in the presentation path.
        if (frame.IsComposed is true)
        {
            return (StallCause.Compositor, null, string.Create(CultureInfo.InvariantCulture,
                $"The frame was composed by the desktop compositor ('{frame.PresentMode}') "
                + $"and took {frameTime:0.0} ms."));
        }

        // Every column was readable and no rule matched. Not the same as missing data, and
        // reported as its own sentence so the two are distinguishable in a log.
        return (StallCause.Unattributable, null, string.Create(CultureInfo.InvariantCulture,
            $"No single stage filled the {frameTime:0.0} ms frame: the game's CPU work took "
            + $"{cpuBusy:0.0} ms, the CPU waited {cpuWait:0.0} ms, and the GPU was busy for "
            + $"{gpuBusy:0.0} ms."));
    }

    private static (StallCause, string?, string) Unknown(string column, double frameTime) =>
        (StallCause.Unattributable, column, string.Create(CultureInfo.InvariantCulture,
            $"{column} was NA on this frame, so no stage can be ranked against another. "
            + $"The frame took {frameTime:0.0} ms."));
}
