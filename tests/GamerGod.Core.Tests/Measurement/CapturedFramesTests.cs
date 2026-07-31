using System.Collections;
using System.Globalization;
using GamerGod.Core.Forensics;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Measurement;

/// <summary>
/// The path from a live capture to a stutter attribution.
///
/// <para>
/// The rule these tests exist to hold is that the frames handed to the attributor are exactly
/// the frames the A/B verdict was computed from. A capture discards its opening seconds —
/// shader compilation, first-time file reads and clock ramp make them unrepresentative — and if
/// attribution ran over the whole capture instead, bench would print causes for hitches its own
/// verdict refuses to count.
/// </para>
/// </summary>
public sealed class CapturedFramesTests
{
    /// <summary>PresentMon 2.5.1, v2 metrics — pinned here so these tests stand on their own.</summary>
    private const string V2Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,"
        + "AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,"
        + "MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,"
        + "CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,"
        + "MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,"
        + "MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    private const string IndependentFlip = "Hardware: Independent Flip";

    /// <summary>One frame of a fixture: how long it took, and what was busy while it did.</summary>
    private readonly record struct Frame(double FrameTimeMs, string CpuBusy = "2.0", string GpuBusy = "4.0");

    private static string Row(string frameTime, double timeInMs, string cpuBusy, string gpuBusy)
    {
        var time = timeInMs.ToString("0.0000", CultureInfo.InvariantCulture);

        return $"game.exe,1234,0x25EA8554010,DXGI,0,0,0,{IndependentFlip},{time},NA,{frameTime},"
               + $"NA,0.05,0.6,16.3,28.9,16.6,{cpuBusy},3.0,1.1,31.5,{gpuBusy},0.5,NA,28.9,0.4,NA,NA";
    }

    /// <summary>Builds a capture, timestamping each row the way PresentMon does.</summary>
    private static string Csv(IEnumerable<Frame> frames)
    {
        var rows = new List<string> { V2Header };
        var clock = 0.0;

        foreach (var frame in frames)
        {
            clock += frame.FrameTimeMs;
            rows.Add(Row(
                frame.FrameTimeMs.ToString("0.0000", CultureInfo.InvariantCulture),
                clock,
                frame.CpuBusy,
                frame.GpuBusy));
        }

        return string.Join("\n", rows);
    }

    private static IEnumerable<Frame> Steady(int count) =>
        Enumerable.Repeat(new Frame(10.0), count);

    /// <summary>A 200 ms frame the GPU was busy for nearly all of.</summary>
    private static Frame GpuHitch => new(200.0, GpuBusy: "190.0");

    /// <summary>A 200 ms frame the game's own CPU work filled instead.</summary>
    private static Frame CpuHitch => new(200.0, CpuBusy: "190.0", GpuBusy: "2.0");

    // ------------------------------------------------------------------
    // The warm-up window.
    // ------------------------------------------------------------------

    [Fact]
    public void A_hitch_inside_the_warm_up_is_not_attributed_because_the_verdict_ignores_it()
    {
        // 100 steady frames (1.0 s), one 200 ms hitch, then a long steady run. The hitch lands
        // 1.0 s into a capture whose first 2.0 s are discarded, so the measurement never counts
        // it — and neither may the attribution, or bench prints a cause for a frame its own
        // verdict threw away.
        var csv = Csv([.. Steady(100), GpuHitch, .. Steady(290)]);

        var capture = CapturedFrames.FromCsv(csv, discardFirstSeconds: 2.0);

        Assert.Equal(0, capture.Attribute().HitchCount);
        Assert.DoesNotContain(capture.Frames, f => f.MsBetweenPresents > 100);
        Assert.Empty(capture.Series.Hitches());
    }

    [Fact]
    public void That_same_capture_does_contain_a_hitch_once_the_warm_up_is_kept()
    {
        // Without this, the test above would pass just as happily against a fixture with no
        // hitch in it at all.
        var csv = Csv([.. Steady(100), GpuHitch, .. Steady(290)]);

        var whole = StutterAttributor.Attribute(PresentMonCsv.ParseFrames(csv));

        Assert.Equal(1, whole.HitchCount);
        Assert.Equal(StallCause.GpuBound, whole.Hitches[0].Cause);
    }

    [Fact]
    public void A_hitch_after_the_warm_up_is_attributed()
    {
        // The other half of the same rule: the window must not swallow the capture.
        var csv = Csv([.. Steady(300), GpuHitch, .. Steady(100)]);

        var report = CapturedFrames.FromCsv(csv, discardFirstSeconds: 2.0).Attribute();

        Assert.Equal(1, report.HitchCount);
        Assert.Equal(StallCause.GpuBound, report.Hitches[0].Cause);
        Assert.Single(report.TopCauses(3));
    }

    [Fact]
    public void The_frames_and_the_series_cover_the_same_window()
    {
        // Two windows under one capture would put two different numbers in front of a reader
        // with nothing to tell them apart.
        var csv = Csv([.. Steady(100), GpuHitch, .. Steady(290)]);

        var capture = CapturedFrames.FromCsv(csv, discardFirstSeconds: 2.0);
        var expected = FrameSeries.FromFrameTimes(
            PresentMonCsv.ParseFrameTimes(csv), discardFirstSeconds: 2.0);

        // Compared as sequences on purpose: ImmutableArray<T> implements IEquatable<T> as
        // reference equality over its backing store.
        Assert.Equal<IEnumerable<double>>(expected.FrameTimesMs, capture.Series.FrameTimesMs);
        Assert.Equal<IEnumerable<double>>(
            capture.Series.FrameTimesMs,
            capture.Frames.Where(f => f.MsBetweenPresents.HasValue).Select(f => f.MsBetweenPresents!.Value));
    }

    [Fact]
    public void Rows_without_a_frame_time_travel_with_the_window_rather_than_being_dropped()
    {
        // PresentMon writes rows it could not time. They are not measurements and they are not
        // nothing: the attributor counts them, and the difference between the two counts is how
        // a reader sees how much of the capture was usable.
        var csv = string.Join(
            "\n",
            [
                V2Header,
                .. Enumerable.Repeat(Row("10.0000", 0, "2.0", "4.0"), 250),
                Row("NA", 0, "2.0", "4.0"),
                Row("NA", 0, "2.0", "4.0"),
                .. Enumerable.Repeat(Row("10.0000", 0, "2.0", "4.0"), 50),
            ]);

        var capture = CapturedFrames.FromCsv(csv, discardFirstSeconds: 2.0);
        var report = capture.Attribute();

        // 200 frames of 10 ms are the discarded 2.0 s. The 100 measured frames after them and
        // both untimed rows survive into the window.
        Assert.Equal(100, capture.Series.Count);
        Assert.Equal(102, capture.Frames.Length);
        Assert.Equal(102, report.FrameCount);
        Assert.Equal(100, report.MeasuredFrameCount);
    }

    [Fact]
    public void Discarding_never_empties_a_capture()
    {
        // The same rule FrameSeries follows: a short run is better reported as short than as
        // empty, and the frames must not vanish when the series does not.
        var capture = CapturedFrames.FromCsv(Csv(Steady(5)), discardFirstSeconds: 60);

        Assert.False(capture.Series.IsEmpty);
        Assert.Equal(5, capture.Series.Count);
        Assert.Equal(5, capture.Frames.Length);
    }

    [Fact]
    public void A_capture_with_nothing_in_it_reports_nothing_rather_than_throwing()
    {
        foreach (var capture in new[]
                 {
                     CapturedFrames.Empty,
                     CapturedFrames.FromCsv(string.Empty, discardFirstSeconds: 2.0),
                     CapturedFrames.FromCsv("no header here at all", discardFirstSeconds: 2.0),
                 })
        {
            Assert.True(capture.Series.IsEmpty);
            Assert.Empty(capture.Frames);
            Assert.Equal(0, capture.Attribute().HitchCount);
            Assert.False(string.IsNullOrWhiteSpace(capture.Attribute().Explain()));
        }
    }

    // ------------------------------------------------------------------
    // Parsed once.
    // ------------------------------------------------------------------

    [Fact]
    public void The_parsed_frames_are_walked_once()
    {
        // PresentMonCsv.ParseFrames is a lazy iterator, so enumerating it twice re-parses the
        // whole capture — which is what asking for frame times and frame records separately
        // does. This source refuses to be walked twice, so a second pass fails loudly.
        var source = new WalkOnce(
            PresentMonCsv.ParseFrames(Csv([.. Steady(300), GpuHitch, .. Steady(100)])));

        var capture = CapturedFrames.FromFrames(source, discardFirstSeconds: 2.0);

        Assert.Equal(1, source.Walks);

        // Non-trivial, or "walked once" would be satisfied by not walking it at all.
        Assert.Equal(201, capture.Series.Count);
        Assert.Equal(1, capture.Attribute().HitchCount);
    }

    [Fact]
    public void The_live_capture_path_parses_the_csv_exactly_once()
    {
        // The defect this replaced: PresentMonCapture parsed the CSV for frame times and threw
        // the text away, leaving the attributor with no path to a real capture. Parsing it a
        // second time to get the records back would double the cost of every measurement and —
        // worse — leave two passes free to window the capture differently.
        var path = new[]
        {
            Path.Combine("src", "GamerGod.Windows", "PresentMonCapture.cs"),
            Path.Combine("src", "GamerGod.Core", "Measurement", "CapturedFrames.cs"),
        };

        var calls = new List<string>();

        foreach (var (file, lines) in Charter.Repo.ProductionSources())
        {
            if (!path.Contains(file, StringComparer.Ordinal))
            {
                continue;
            }

            foreach (var (number, text) in Charter.Repo.CodeLines(lines))
            {
                if (text.Contains("PresentMonCsv.Parse", StringComparison.Ordinal))
                {
                    calls.Add($"{file}:{number}  {text.Trim()}");
                }
            }
        }

        Assert.True(
            calls.Count == 1,
            $"the live capture path must parse the CSV exactly once, found {calls.Count}:\n  "
            + string.Join("\n  ", calls));
    }

    // ------------------------------------------------------------------
    // The capture seam.
    // ------------------------------------------------------------------

    [Fact]
    public async Task An_unavailable_source_returns_no_frames_as_well_as_no_series()
    {
        var capture = await new UnavailableFrameCapture("Nothing can measure this machine.")
            .CaptureAsync(null, TimeSpan.FromSeconds(1), default);

        Assert.True(capture.Series.IsEmpty);
        Assert.Empty(capture.Frames);
    }

    [Fact]
    public async Task A_run_attributes_the_candidate_arm_and_only_the_candidate_arm()
    {
        // Bench prints these causes underneath a verdict about the candidate arm. Pooling both
        // arms would describe a machine that was in two different states.
        var capture = new ArmedCapture();
        var runner = new AbRunner(capture);

        var run = await runner.RunAsync(
            _ => { capture.Arm = CpuHitch; return ValueTask.CompletedTask; },
            _ => { capture.Arm = GpuHitch; return ValueTask.CompletedTask; },
            processId: null,
            perRun: TimeSpan.Zero,
            repetitions: 5);

        var report = run.AttributeCandidate();

        // One hitch per candidate run, and every one of them from the candidate arm.
        Assert.Equal(5, report.HitchCount);
        Assert.All(report.Hitches, h => Assert.Equal(StallCause.GpuBound, h.Cause));
        Assert.DoesNotContain(report.Hitches, h => h.Cause == StallCause.GameCpuBound);
        Assert.Single(report.TopCauses(3));
    }

    [Fact]
    public async Task A_run_with_no_frames_still_produces_a_report()
    {
        var runner = new AbRunner(new UnavailableFrameCapture("No capture source is installed."));

        var run = await runner.RunAsync(
            _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask,
            processId: null,
            perRun: TimeSpan.Zero,
            repetitions: 3);

        var report = run.AttributeCandidate();

        Assert.Equal(0, report.HitchCount);
        Assert.False(string.IsNullOrWhiteSpace(report.Explain()));
    }

    /// <summary>
    /// Captures whatever hitch the arm that was just applied put there — the same order the real
    /// runner uses, apply then measure. Which arm a report came from is then visible in its
    /// causes, which the interleaved call order alone could not tell us.
    /// </summary>
    private sealed class ArmedCapture : IFrameCapture
    {
        public Frame Arm { get; set; } = new(10.0);

        public CaptureSource Source { get; } = new() { Name = "stub", IsAvailable = true };

        public ValueTask<CapturedFrames> CaptureAsync(
            int? processId, TimeSpan duration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CapturedFrames.FromCsv(Csv([.. Steady(100), Arm, .. Steady(100)])));
    }

    /// <summary>A sequence that fails if anything walks it twice.</summary>
    private sealed class WalkOnce(IEnumerable<FrameRecord> source) : IEnumerable<FrameRecord>
    {
        public int Walks { get; private set; }

        public IEnumerator<FrameRecord> GetEnumerator()
        {
            if (++Walks > 1)
            {
                throw new InvalidOperationException(
                    "the parsed capture was walked a second time, which re-parses the whole CSV");
            }

            return source.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
