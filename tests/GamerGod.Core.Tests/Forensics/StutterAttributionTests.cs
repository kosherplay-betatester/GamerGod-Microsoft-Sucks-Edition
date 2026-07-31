using GamerGod.Core.Forensics;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Forensics;

/// <summary>
/// Attribution of hitches to a pipeline stage, from PresentMon columns alone.
///
/// <para>
/// Every test here is a hand-written capture and a string. No PresentMon, no game, no ETW
/// session, no administrator. That is possible because the whole question — which stage of the
/// present pipeline was occupied while the frame was late — is answerable from columns
/// PresentMon already writes.
/// </para>
///
/// <para>
/// Each rule is tested twice: once with a capture that satisfies it, and once with a capture
/// that flips exactly one term of the predicate. A rule with only the first kind of test passes
/// just as happily when it is inverted, which is the failure this file exists to prevent.
/// </para>
/// </summary>
public sealed class StutterAttributionTests
{
    private const string IndependentFlip = "Hardware: Independent Flip";
    private const string HardwareComposed = "Hardware Composed: Independent Flip";
    private const string ComposedFlip = "Composed: Flip";
    private const string LegacyFlip = "Hardware: Legacy Flip";

    /// <summary>
    /// One row of PresentMon 2.5.1 output. Every parameter is a string so that any column can
    /// be set to <c>NA</c>, which is the case that matters most.
    /// </summary>
    private static string Row(
        string frameTime,
        string cpuBusy = "4.0",
        string cpuWait = "6.0",
        string gpuBusy = "5.0",
        string mode = IndependentFlip) =>
        $"game.exe,1234,0x25EA8554010,DXGI,0,0,0,{mode},0.0,NA,{frameTime},NA,0.05,"
        + $"0.6,16.3,28.9,16.6,{cpuBusy},{cpuWait},1.1,31.5,{gpuBusy},0.5,NA,28.9,0.4,NA,NA";

    /// <summary>Twenty unremarkable 10 ms frames: median 10 ms, so the hitch threshold is 20 ms.</summary>
    private static IEnumerable<string> Steady(string mode = IndependentFlip) =>
        Enumerable.Repeat(Row("10.0", mode: mode), 20);

    private static IEnumerable<FrameRecord> Parse(IEnumerable<string> rows) =>
        PresentMonCsv.ParseFrames(
            string.Join("\n", rows.Prepend(FrameRecordParsingTests.V2Header)));

    private static StutterReport Attribute(IEnumerable<string> rows) =>
        StutterAttributor.Attribute(Parse(rows));

    /// <summary>A steady run with exactly one late frame, and that frame's verdict.</summary>
    private static StallAttribution OneHitch(string hitchRow, string steadyMode = IndependentFlip)
    {
        var report = Attribute([.. Steady(steadyMode), hitchRow]);

        Assert.Equal(21, report.MeasuredFrameCount);
        return Assert.Single(report.Hitches);
    }

    // ------------------------------------------------------------------
    // GpuBound — MsGPUBusy > MsCPUBusy and MsGPUBusy covers at least half the frame.
    // ------------------------------------------------------------------

    [Fact]
    public void A_frame_the_gpu_was_busy_for_is_attributed_to_the_gpu()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"));

        Assert.Equal(StallCause.GpuBound, hitch.Cause);
        Assert.Contains("36.0", hitch.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Inverting_the_gpu_against_cpu_term_changes_the_answer()
    {
        // Same frame, same wait, the two busy figures exchanged. If the comparison were the
        // other way round this would still come back GpuBound.
        var hitch = OneHitch(Row("40.0", cpuBusy: "36.0", cpuWait: "3.0", gpuBusy: "3.0"));

        Assert.NotEqual(StallCause.GpuBound, hitch.Cause);
        Assert.Equal(StallCause.GameCpuBound, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_gpu_half_frame_term_changes_the_answer()
    {
        // The GPU was the busiest single stage but was busy for well under half the frame, so
        // it does not account for the frame being late. Nothing else does either.
        var hitch = OneHitch(Row("40.0", cpuBusy: "3.0", cpuWait: "12.0", gpuBusy: "15.0"));

        Assert.NotEqual(StallCause.GpuBound, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    [Fact]
    public void A_gpu_bound_capture_is_never_reported_as_cpu_contention()
    {
        // The rule the whole feature would be worthless without. A GPU-bound frame has a large
        // MsCPUWait by construction — the CPU is waiting on the GPU — and any predicate that
        // reads MsCPUWait without also reading MsGPUBusy calls that CPU contention and sends
        // the user off to fight the wrong problem.
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.5", cpuWait: "36.0", gpuBusy: "38.0"),
            Row("52.0", cpuBusy: "4.0", cpuWait: "47.0", gpuBusy: "49.0"),
            Row("44.0", cpuBusy: "3.0", cpuWait: "40.0", gpuBusy: "42.0"),
        ]);

        Assert.Equal(3, report.HitchCount);
        Assert.All(report.Hitches, h => Assert.Equal(StallCause.GpuBound, h.Cause));
        Assert.DoesNotContain(report.Hitches, h => h.Cause == StallCause.CpuBlocked);
        Assert.DoesNotContain(report.Hitches, h => h.Cause == StallCause.GameCpuBound);
    }

    // ------------------------------------------------------------------
    // GameCpuBound — MsCPUBusy >= MsGPUBusy and MsCPUBusy covers at least half the frame.
    // ------------------------------------------------------------------

    [Fact]
    public void A_frame_the_games_own_cpu_work_filled_is_attributed_to_it()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "34.0", cpuWait: "5.0", gpuBusy: "6.0"));

        Assert.Equal(StallCause.GameCpuBound, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_cpu_half_frame_term_changes_the_answer()
    {
        // The game's CPU work was the largest stage and still only a third of the frame. The
        // remaining two thirds are unaccounted for, so no stage gets the blame.
        var hitch = OneHitch(Row("40.0", cpuBusy: "15.0", cpuWait: "8.0", gpuBusy: "6.0"));

        Assert.NotEqual(StallCause.GameCpuBound, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_cpu_against_gpu_term_changes_the_answer()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "20.0", cpuWait: "18.0", gpuBusy: "34.0"));

        Assert.NotEqual(StallCause.GameCpuBound, hitch.Cause);
        Assert.Equal(StallCause.GpuBound, hitch.Cause);
    }

    // ------------------------------------------------------------------
    // CpuBlocked — MsCPUWait covers at least half the frame while MsGPUBusy covers under a
    // quarter of it.
    // ------------------------------------------------------------------

    [Fact]
    public void A_frame_spent_waiting_while_the_gpu_sat_idle_is_attributed_to_the_wait()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "5.0", cpuWait: "34.0", gpuBusy: "2.0"));

        Assert.Equal(StallCause.CpuBlocked, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_wait_half_frame_term_changes_the_answer()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "5.0", cpuWait: "8.0", gpuBusy: "2.0"));

        Assert.NotEqual(StallCause.CpuBlocked, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_idle_gpu_term_changes_the_answer()
    {
        // Identical wait, a busy GPU. Without this term the answer would be CPU contention on
        // a frame where the CPU was waiting for the GPU, which is the opposite of the truth.
        var hitch = OneHitch(Row("40.0", cpuBusy: "5.0", cpuWait: "34.0", gpuBusy: "36.0"));

        Assert.NotEqual(StallCause.CpuBlocked, hitch.Cause);
        Assert.Equal(StallCause.GpuBound, hitch.Cause);
    }

    [Fact]
    public void A_partly_busy_gpu_is_not_enough_to_call_it_cpu_contention()
    {
        // 12 ms of a 40 ms frame: over the quarter that CpuBlocked requires, under the half
        // that GpuBound requires. The honest answer is that the data does not separate them.
        var hitch = OneHitch(Row("40.0", cpuBusy: "5.0", cpuWait: "34.0", gpuBusy: "12.0"));

        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    // ------------------------------------------------------------------
    // LeftIndependentFlip — this frame is not an independent flip and the one before it
    // was. The transition itself is the event.
    // ------------------------------------------------------------------

    [Fact]
    public void Leaving_independent_flip_is_attributed_to_the_transition()
    {
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip));

        Assert.Equal(StallCause.LeftIndependentFlip, hitch.Cause);
        Assert.Contains(IndependentFlip, hitch.Evidence, StringComparison.Ordinal);
        Assert.Contains(ComposedFlip, hitch.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_independent_flip_for_a_legacy_flip_counts_too()
    {
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: LegacyFlip));

        Assert.Equal(StallCause.LeftIndependentFlip, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_previous_frame_term_changes_the_answer()
    {
        // The identical late frame, reached from a run that was already composed. Nothing
        // transitioned, so there is no transition to report.
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
            steadyMode: ComposedFlip);

        Assert.NotEqual(StallCause.LeftIndependentFlip, hitch.Cause);
        Assert.Equal(StallCause.Compositor, hitch.Cause);
    }

    // ------------------------------------------------------------------
    // Run boundaries. Several runs of one arm share a median and nothing else.
    // ------------------------------------------------------------------

    [Fact]
    public void A_run_boundary_is_not_a_present_mode_transition()
    {
        // Two runs of the same arm. Run 1 ran in independent flip throughout; run 2 was composed
        // from its very first frame — a second PresentMon session, with the game restarted in
        // between. Concatenating them made run 1's last frame the predecessor of run 2's first,
        // and rule 4 duly reported presentation "leaving independent flip" at a seam where no
        // present followed another at all.
        var report = StutterAttributor.AttributeRuns(
        [
            Parse(Steady()),
            Parse([Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
                .. Steady(ComposedFlip)]),
        ]);

        var hitch = Assert.Single(report.Hitches);

        Assert.NotEqual(StallCause.LeftIndependentFlip, hitch.Cause);

        // Not Compositor either, and that is the governing rule of this file doing its job. The
        // frame is composed, so rule 5 would match — but rule 4 sits above it and its input is
        // genuinely unknown here, so the chain stops rather than skipping down to an answer that
        // would look just as confident. The first frame of a run is the first frame of a
        // capture, because that is exactly what it is.
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
        Assert.Equal("PresentMode", hitch.MissingColumn);
    }

    [Fact]
    public void A_transition_inside_a_run_is_still_attributed_when_runs_are_separate()
    {
        // The guard above must not be a blanket refusal to attribute the first frames of a run.
        // The identical hitch, one frame later — a real transition, inside run 2 — still reads.
        var report = StutterAttributor.AttributeRuns(
        [
            Parse(Steady()),
            Parse([Row("10.0"),
                Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
                .. Steady(ComposedFlip)]),
        ]);

        Assert.Equal(StallCause.LeftIndependentFlip, Assert.Single(report.Hitches).Cause);
    }

    [Fact]
    public void Pooled_runs_share_one_median_and_count_every_frame()
    {
        // The pooling that is correct, and that the fix above must not have undone: the arm's
        // runs are one machine state measured repeatedly, so they share a threshold.
        var report = StutterAttributor.AttributeRuns([Parse(Steady()), Parse(Steady())]);

        Assert.Equal(40, report.FrameCount);
        Assert.Equal(40, report.MeasuredFrameCount);
        Assert.Equal(10.0, report.MedianFrameTimeMs, 3);
        Assert.Equal(20.0, report.HitchThresholdMs, 3);
    }

    [Fact]
    public void Staying_in_independent_flip_is_not_a_transition()
    {
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: IndependentFlip));

        Assert.NotEqual(StallCause.LeftIndependentFlip, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    [Fact]
    public void A_first_frame_hitch_has_no_predecessor_so_the_transition_cannot_be_judged()
    {
        // Nothing precedes frame zero, so "the mode changed" has no answer — not "no". Saying
        // no would be inventing a fact about a frame that was never captured.
        //
        // Compositor would be a true statement about this frame and is still not made, because
        // the higher-priority transition rule could not be evaluated. That is the conservative
        // half of the priority rule, and it is deliberate: it costs one attribution per capture
        // at worst, and it keeps a single rule — an unevaluable rule stops the chain — instead
        // of a table of exceptions that a later contributor would have to get right.
        var report = Attribute(
        [
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
            .. Enumerable.Repeat(Row("10.0"), 20),
        ]);

        var hitch = Assert.Single(report.Hitches);

        Assert.Equal(0, hitch.FrameIndex);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
        Assert.NotEqual(StallCause.Compositor, hitch.Cause);
        Assert.Equal("PresentMode", hitch.MissingColumn);
    }

    // ------------------------------------------------------------------
    // Compositor — the present was composed by the desktop compositor.
    // ------------------------------------------------------------------

    [Fact]
    public void A_composed_present_is_attributed_to_the_compositor()
    {
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
            steadyMode: ComposedFlip);

        Assert.Equal(StallCause.Compositor, hitch.Cause);
    }

    [Fact]
    public void Inverting_the_composed_term_changes_the_answer()
    {
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: IndependentFlip));

        Assert.NotEqual(StallCause.Compositor, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    [Fact]
    public void Hardware_composed_independent_flip_is_not_the_compositor_path()
    {
        // Multi-plane overlay. The word "Composed" is in the string and the compositor is not
        // in the way; a substring test here would attribute every hitch on a modern full-screen
        // game to DWM, which would be wrong on the majority of machines this runs on.
        var hitch = OneHitch(
            Row("40.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: HardwareComposed),
            steadyMode: HardwareComposed);

        Assert.NotEqual(StallCause.Compositor, hitch.Cause);
        Assert.Equal(StallCause.Unattributable, hitch.Cause);
    }

    // ------------------------------------------------------------------
    // Unattributable — the answer when the data does not contain one.
    // ------------------------------------------------------------------

    [Fact]
    public void A_missing_gpu_column_makes_a_cpu_heavy_frame_unattributable()
    {
        // MsCPUBusy alone looks conclusive and is not: without MsGPUBusy there is no way to
        // know the GPU was not busier still, and GpuBound outranks GameCpuBound. Reporting the
        // CPU here would be a guess wearing the clothes of a measurement.
        var hitch = OneHitch(Row("40.0", cpuBusy: "34.0", cpuWait: "5.0", gpuBusy: "NA"));

        Assert.Equal(StallCause.Unattributable, hitch.Cause);
        Assert.NotEqual(StallCause.GameCpuBound, hitch.Cause);
        Assert.Equal("MsGPUBusy", hitch.MissingColumn);
    }

    [Fact]
    public void A_missing_cpu_column_is_unattributable_too()
    {
        var hitch = OneHitch(Row("40.0", cpuBusy: "NA", cpuWait: "5.0", gpuBusy: "36.0"));

        Assert.Equal(StallCause.Unattributable, hitch.Cause);
        Assert.Equal("MsCPUBusy", hitch.MissingColumn);
    }

    [Fact]
    public void A_v1_metrics_capture_attributes_nothing_and_says_so()
    {
        // PresentMon 1.x has no CPU or GPU breakdown at all. Every hitch is real and none of
        // them can be explained. The count of what could not be explained is the report.
        var csv = string.Join("\n",
        [
            "Application,ProcessID,PresentMode,msBetweenPresents,msInPresentAPI",
            .. Enumerable.Repeat("game.exe,1,Hardware: Independent Flip,10.0,0.04", 20),
            "game.exe,1,Hardware: Independent Flip,40.0,0.04",
            "game.exe,1,Hardware: Independent Flip,55.0,0.04",
        ]);

        var report = StutterAttributor.Attribute(PresentMonCsv.ParseFrames(csv));

        Assert.Equal(2, report.HitchCount);
        Assert.Equal(2, report.UnattributableCount);
        Assert.All(report.Hitches, h => Assert.Equal(StallCause.Unattributable, h.Cause));
        Assert.Equal("MsCPUBusy", report.Hitches[0].MissingColumn);
    }

    [Fact]
    public void Unattributable_frames_are_counted_not_dropped()
    {
        // Silently discarding what cannot be explained leaves a report that is 100% confident
        // about the 20% of hitches it happened to understand.
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", gpuBusy: "NA"),
            Row("42.0", gpuBusy: "NA"),
            Row("43.0", gpuBusy: "NA"),
        ]);

        Assert.Equal(4, report.HitchCount);
        Assert.Equal(3, report.UnattributableCount);
        Assert.Equal(report.HitchCount, report.Ranked.Sum(r => r.Count));
    }

    [Fact]
    public void Unattributable_can_be_the_top_cause_and_is_not_hidden_below_the_others()
    {
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", gpuBusy: "NA"),
            Row("41.0", gpuBusy: "NA"),
            Row("42.0", gpuBusy: "NA"),
            Row("43.0", cpuBusy: "3.0", cpuWait: "40.0", gpuBusy: "40.0"),
        ]);

        Assert.Equal(StallCause.Unattributable, report.Ranked[0].Cause);
        Assert.Contains(
            report.TopCauses(),
            line => line.Contains("attribut", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_default_classification_is_unattributable_rather_than_a_cause()
    {
        // A zero-initialised StallCause must not claim anything. Enum member order is load
        // bearing here, so it gets a test rather than a comment.
        Assert.Equal(StallCause.Unattributable, default);
    }

    // ------------------------------------------------------------------
    // The report.
    // ------------------------------------------------------------------

    [Fact]
    public void The_hitches_examined_are_exactly_the_ones_the_existing_threshold_finds()
    {
        // Two definitions of "hitch" in one product means two different numbers under one word.
        double[] frameTimes = [10, 10, 10, 10, 10, 10, 10, 10, 45, 10, 10, 90];

        var series = FrameSeries.FromFrameTimes(frameTimes);
        var report = Attribute(frameTimes.Select(t =>
            Row(t.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture))));

        Assert.Equal(series.MedianFrameTimeMs, report.MedianFrameTimeMs, 6);
        Assert.Equal(series.MedianFrameTimeMs * 2.0, report.HitchThresholdMs, 6);
        Assert.Equal(series.Hitches(2.0), report.Hitches.Select(h => h.FrameTimeMs));
    }

    [Fact]
    public void A_stricter_multiple_finds_fewer_hitches()
    {
        var rows = new[] { Row("25.0"), Row("90.0") }.Concat(Steady()).ToArray();
        var frames = PresentMonCsv.ParseFrames(
            string.Join("\n", rows.Prepend(FrameRecordParsingTests.V2Header))).ToArray();

        Assert.Equal(2, StutterAttributor.Attribute(frames, hitchMultiple: 2.0).HitchCount);
        Assert.Equal(1, StutterAttributor.Attribute(frames, hitchMultiple: 4.0).HitchCount);
    }

    [Fact]
    public void Every_hitch_appears_in_the_report_exactly_once()
    {
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "36.0", cpuWait: "3.0", gpuBusy: "3.0"),
            Row("42.0", cpuBusy: "5.0", cpuWait: "38.0", gpuBusy: "2.0"),
            Row("43.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
        ]);

        Assert.Equal(4, report.HitchCount);
        Assert.Equal([40.0, 41.0, 42.0, 43.0], report.Hitches.Select(h => h.FrameTimeMs));
        Assert.Equal(report.Hitches.Length, report.Hitches.Select(h => h.FrameIndex).Distinct().Count());
        Assert.Equal(
            [
                StallCause.GpuBound,
                StallCause.GameCpuBound,
                StallCause.CpuBlocked,
                StallCause.LeftIndependentFlip,
            ],
            report.Hitches.Select(h => h.Cause));
    }

    [Fact]
    public void Causes_are_ranked_by_how_many_hitches_they_account_for()
    {
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "3.0", cpuWait: "37.0", gpuBusy: "37.0"),
            Row("42.0", cpuBusy: "3.0", cpuWait: "38.0", gpuBusy: "38.0"),
            Row("43.0", cpuBusy: "38.0", cpuWait: "3.0", gpuBusy: "3.0"),
        ]);

        Assert.Equal(StallCause.GpuBound, report.Ranked[0].Cause);
        Assert.Equal(3, report.Ranked[0].Count);
        Assert.Equal(StallCause.GameCpuBound, report.Ranked[1].Cause);
        Assert.Equal(1, report.Ranked[1].Count);
    }

    [Fact]
    public void Equal_counts_are_broken_by_the_time_lost_not_by_enum_order()
    {
        // One hitch each. The one that cost more time ranks higher, and the answer flips when
        // the two frame times are exchanged — which an enum-order tie-break would not do.
        var gpuWorse = Attribute(
        [
            .. Steady(),
            Row("80.0", cpuBusy: "3.0", cpuWait: "76.0", gpuBusy: "76.0"),
            Row("30.0", cpuBusy: "28.0", cpuWait: "2.0", gpuBusy: "2.0"),
        ]);

        var cpuWorse = Attribute(
        [
            .. Steady(),
            Row("30.0", cpuBusy: "3.0", cpuWait: "27.0", gpuBusy: "27.0"),
            Row("80.0", cpuBusy: "76.0", cpuWait: "3.0", gpuBusy: "3.0"),
        ]);

        Assert.Equal(StallCause.GpuBound, gpuWorse.Ranked[0].Cause);
        Assert.Equal(StallCause.GameCpuBound, cpuWorse.Ranked[0].Cause);
    }

    [Fact]
    public void The_top_causes_are_at_most_three_lines_and_name_their_share()
    {
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "38.0", cpuWait: "3.0", gpuBusy: "3.0"),
            Row("42.0", cpuBusy: "5.0", cpuWait: "38.0", gpuBusy: "2.0"),
            Row("43.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
        ]);

        var lines = report.TopCauses();

        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Contains("of 4 hitches", line, StringComparison.Ordinal));
        Assert.All(lines, line => Assert.DoesNotContain("\n", line, StringComparison.Ordinal));
    }

    [Fact]
    public void A_capture_with_no_hitches_produces_no_lines_and_says_so()
    {
        var report = Attribute(Steady());

        Assert.Equal(0, report.HitchCount);
        Assert.Empty(report.TopCauses());
        Assert.Contains("No frame", report.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_capture_reports_nothing_rather_than_throwing()
    {
        var report = StutterAttributor.Attribute([]);

        Assert.Equal(0, report.FrameCount);
        Assert.Equal(0, report.HitchCount);
        Assert.Equal(0, report.MedianFrameTimeMs);
        Assert.Empty(report.Hitches);
        Assert.Empty(report.Ranked);
        Assert.Empty(report.TopCauses());
        Assert.False(string.IsNullOrWhiteSpace(report.Explain()));
    }

    [Fact]
    public void Frames_without_a_frame_time_are_visible_in_the_counts()
    {
        // A row PresentMon could not time is not a hitch and is not nothing. The difference
        // between the two counts is how a caller sees how much of the capture was usable.
        var report = Attribute([.. Steady(), Row("NA"), Row("NA")]);

        Assert.Equal(22, report.FrameCount);
        Assert.Equal(20, report.MeasuredFrameCount);
        Assert.Equal(0, report.HitchCount);
    }

    [Fact]
    public void The_same_capture_always_produces_the_same_report()
    {
        // An attribution that reorders itself between runs is not evidence of anything.
        var rows = new[]
        {
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "38.0", cpuWait: "3.0", gpuBusy: "3.0"),
            Row("42.0", gpuBusy: "NA"),
            Row("43.0", cpuBusy: "5.0", cpuWait: "38.0", gpuBusy: "2.0"),
        }.Concat(Steady()).ToArray();

        var first = Attribute(rows);
        var second = Attribute(rows);

        // Non-trivial first, or "the same nothing twice" would satisfy this.
        Assert.Equal(4, first.HitchCount);
        Assert.Equal(4, first.Ranked.Length);
        Assert.Equal(3, first.TopCauses().Length);

        // Compared as sequences on purpose. ImmutableArray<T> implements IEquatable<T> as
        // reference equality over its backing store, so Assert.Equal on two of them asks
        // whether they are the same array rather than whether they say the same thing.
        Assert.Equal(first.Ranked.Select(r => r.Cause), second.Ranked.Select(r => r.Cause));
        Assert.Equal<IEnumerable<string>>(first.TopCauses(), second.TopCauses());
        Assert.Equal(first.Explain(), second.Explain());
    }

    // ------------------------------------------------------------------
    // Article VII — the report states what the data showed, never what to do about it.
    // ------------------------------------------------------------------

    public static TheoryData<string> ClaimWords() =>
        ["bottleneck", "caused by", "is the cause", "you should", "will improve",
         "boost", "optimi", "recommend", "fix your", "upgrade"];

    [Theory]
    [MemberData(nameof(ClaimWords))]
    public void No_part_of_the_report_makes_a_claim_about_what_to_do(string word)
    {
        // "The GPU was busy for 36 ms while the CPU waited" is a fact about a capture. "Your
        // GPU is the bottleneck" is a claim about a machine, a game and a scene this tool did
        // not measure. Article VII is the difference between the two.
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "38.0", cpuWait: "3.0", gpuBusy: "3.0"),
            Row("42.0", cpuBusy: "5.0", cpuWait: "38.0", gpuBusy: "2.0"),
            Row("43.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
            Row("44.0", gpuBusy: "NA"),
        ]);

        var text = string.Join(
            "\n",
            [
                report.Explain(),
                .. report.TopCauses(6),
                .. report.Hitches.Select(h => h.Evidence),
                .. Enum.GetValues<StallCause>().Select(c => c.Label()),
            ]);

        // There has to be prose here for its absence of claims to mean anything.
        Assert.Equal(5, report.HitchCount);
        Assert.True(text.Length > 300, $"only {text.Length} characters of report to inspect");

        Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_quotes_the_numbers_it_was_read_from()
    {
        // A sentence with no figures in it is an opinion. Each verdict has to carry the
        // measurements that produced it so a reader can disagree with the rule.
        var report = Attribute(
        [
            .. Steady(),
            Row("40.0", cpuBusy: "3.0", cpuWait: "36.0", gpuBusy: "36.0"),
            Row("41.0", cpuBusy: "38.0", cpuWait: "3.0", gpuBusy: "3.0"),
            Row("42.0", cpuBusy: "5.0", cpuWait: "38.0", gpuBusy: "2.0"),
            Row("43.0", cpuBusy: "3.0", cpuWait: "5.0", gpuBusy: "2.0", mode: ComposedFlip),
            Row("44.0", gpuBusy: "NA"),
        ]);

        Assert.Equal(5, report.HitchCount);
        Assert.All(report.Hitches, h => Assert.False(string.IsNullOrWhiteSpace(h.Evidence)));
        Assert.All(
            report.Hitches.Where(h => h.Cause != StallCause.LeftIndependentFlip),
            h => Assert.Contains("ms", h.Evidence, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_cause_has_wording_of_its_own()
    {
        // A missing case in the label switch would otherwise surface as several causes sharing
        // one sentence, which reads as agreement between rules that did not agree.
        var labels = Enum.GetValues<StallCause>().Select(c => c.Label()).ToArray();

        Assert.All(labels, l => Assert.False(string.IsNullOrWhiteSpace(l)));
        Assert.Equal(labels.Length, labels.Distinct(StringComparer.Ordinal).Count());
    }
}
