using GamerGod.Core.Forensics;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Forensics;

/// <summary>
/// Parsing a whole frame, not just its duration.
///
/// <para>
/// The header pinned here is the same PresentMon 2.5.1 output as
/// <see cref="Measurement.PresentMonParsingTests"/>, for the same reason: every column is
/// located by name, and an inserted column must not silently shift a metric. The extra rule
/// this file adds is that a column PresentMon wrote as <c>NA</c> must arrive as null and never
/// as zero — zero is a number, and a number gets used.
/// </para>
/// </summary>
public sealed class FrameRecordParsingTests
{
    /// <summary>PresentMon 2.5.1, v2 metrics — the current default.</summary>
    internal const string V2Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,"
        + "AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,"
        + "MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,"
        + "CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,"
        + "MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,"
        + "MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    private static FrameRecord Single(string csv) =>
        Assert.Single(PresentMonCsv.ParseFrames(csv));

    [Fact]
    public void Every_column_the_attributor_needs_is_read_by_name()
    {
        // One row, every named value distinct, so a transposed pair of columns cannot pass.
        var row =
            "game.exe,1234,0x25EA8554010,DXGI,0,0,0,Hardware: Independent Flip,"
            + "45.4893,NA,33.3214,NA,0.0474,0.6023,16.2910,28.8952,16.6415,"
            + "30.1000,3.2000,1.1000,31.5000,31.0000,0.5000,NA,28.8952,0.4381,NA,NA";

        var frame = Single(string.Join("\n", [V2Header, row]));

        Assert.Equal(45.4893, frame.TimeInMs);
        Assert.Equal(33.3214, frame.MsBetweenPresents);
        Assert.Equal(0.0474, frame.MsInPresentAPI);
        Assert.Equal(0.6023, frame.MsRenderPresentLatency);
        Assert.Equal(30.1000, frame.MsCPUBusy);
        Assert.Equal(3.2000, frame.MsCPUWait);
        Assert.Equal(1.1000, frame.MsGPULatency);
        Assert.Equal(31.0000, frame.MsGPUBusy);
        Assert.Equal(0.5000, frame.MsGPUWait);
        Assert.Equal(0.4381, frame.MsFlipDelay);
        Assert.Equal("Hardware: Independent Flip", frame.PresentMode);
    }

    [Fact]
    public void An_NA_column_becomes_null_and_never_zero()
    {
        // The single most dangerous shortcut available here. Zero GPU-busy is a claim that the
        // GPU did nothing; NA is the absence of an answer. Collapsing one into the other turns
        // "we do not know" into "the GPU was idle", which is a confident lie.
        var csv = string.Join("\n",
        [
            "PresentMode,MsBetweenPresents,MsCPUBusy,MsCPUWait,MsGPUBusy",
            "Hardware: Independent Flip,33.3,NA,NA,NA",
        ]);

        var frame = Single(csv);

        Assert.Null(frame.MsCPUBusy);
        Assert.Null(frame.MsCPUWait);
        Assert.Null(frame.MsGPUBusy);
        Assert.Equal(33.3, frame.MsBetweenPresents);
    }

    [Fact]
    public void A_blank_column_becomes_null_too()
    {
        var csv = string.Join("\n",
        [
            "PresentMode,MsBetweenPresents,MsGPUBusy",
            ",33.3,",
        ]);

        var frame = Single(csv);

        Assert.Null(frame.MsGPUBusy);
        Assert.Null(frame.PresentMode);
    }

    [Fact]
    public void A_new_column_ahead_of_the_others_cannot_shift_a_metric()
    {
        var csv = string.Join("\n",
        [
            "SomethingNew," + V2Header,
            "0,game.exe,1234,0x25EA,DXGI,0,0,0,Composed: Flip,"
            + "45.4893,NA,33.3214,NA,0.0474,0.6023,16.2910,28.8952,16.6415,"
            + "30.1000,3.2000,1.1000,31.5000,31.0000,0.5000,NA,28.8952,0.4381,NA,NA",
        ]);

        var frame = Single(csv);

        Assert.Equal(33.3214, frame.MsBetweenPresents);
        Assert.Equal(31.0000, frame.MsGPUBusy);
        Assert.Equal("Composed: Flip", frame.PresentMode);
    }

    [Fact]
    public void A_warning_line_before_the_header_is_not_treated_as_data()
    {
        var csv = string.Join("\n",
        [
            "warning: a trace session named \"PresentMon\" is already running",
            "PresentMode,MsBetweenPresents",
            "Hardware: Independent Flip,16.66",
        ]);

        var frame = Single(csv);

        Assert.Equal(16.66, frame.MsBetweenPresents);
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var csv = "PresentMode,MsBetweenPresents\r\nComposed: Flip,16.66\r\nComposed: Flip,8.33\r\n";

        Assert.Equal([16.66, 8.33], PresentMonCsv.ParseFrames(csv).Select(f => f.MsBetweenPresents));
    }

    [Fact]
    public void Frames_keep_their_capture_order_and_index()
    {
        var csv = string.Join("\n",
        [
            "MsBetweenPresents",
            "16.66",
            "8.33",
            "33.32",
        ]);

        var frames = PresentMonCsv.ParseFrames(csv).ToArray();

        Assert.Equal([0, 1, 2], frames.Select(f => f.Index));
        Assert.Equal([16.66, 8.33, 33.32], frames.Select(f => f.MsBetweenPresents));
    }

    [Fact]
    public void A_v1_capture_parses_with_the_v2_only_columns_null()
    {
        // PresentMon 1.x, and 2.x under --v1_metrics, has no CPU or GPU breakdown at all. The
        // record must still parse, with the missing stages null, so the attributor can report
        // that it cannot attribute rather than producing a shape error.
        var csv = string.Join("\n",
        [
            "Application,ProcessID,PresentMode,msBetweenPresents,msInPresentAPI",
            "game.exe,1234,Hardware: Independent Flip,16.66,0.04",
        ]);

        var frame = Single(csv);

        Assert.Equal(16.66, frame.MsBetweenPresents);
        Assert.Equal(0.04, frame.MsInPresentAPI);
        Assert.Null(frame.MsCPUBusy);
        Assert.Null(frame.MsGPUBusy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no header here at all")]
    [InlineData("Application,ProcessID\ngame.exe,1")]
    public void Output_with_no_recognised_column_yields_no_frames(string csv)
    {
        Assert.Empty(PresentMonCsv.ParseFrames(csv));
    }

    [Fact]
    public void Hardware_composed_independent_flip_is_an_independent_flip_not_a_composed_one()
    {
        // The trap in the present-mode strings. "Hardware Composed: Independent Flip" is the
        // multi-plane-overlay fast path — the desktop compositor is not in the frame's way. A
        // substring test for "Composed" reports the opposite of the truth here.
        var frame = Single(string.Join("\n",
        [
            "PresentMode,MsBetweenPresents",
            "Hardware Composed: Independent Flip,16.66",
        ]));

        Assert.True(frame.IsIndependentFlip);
        Assert.False(frame.IsComposed);
    }

    [Theory]
    [InlineData("Hardware: Independent Flip", true, false)]
    [InlineData("Hardware Composed: Independent Flip", true, false)]
    [InlineData("Composed: Flip", false, true)]
    [InlineData("Composed: Copy with GPU GDI", false, true)]
    [InlineData("Composed: Copy with CPU GDI", false, true)]
    [InlineData("Hardware: Legacy Flip", false, false)]
    [InlineData("Hardware: Legacy Copy to front buffer", false, false)]
    public void Present_modes_are_classified_from_the_documented_strings(
        string mode, bool independentFlip, bool composed)
    {
        var frame = Single(string.Join("\n", ["PresentMode,MsBetweenPresents", $"{mode},16.66"]));

        Assert.Equal(independentFlip, frame.IsIndependentFlip);
        Assert.Equal(composed, frame.IsComposed);
    }

    [Fact]
    public void An_absent_present_mode_is_neither_composed_nor_independent_flip()
    {
        // Null, not false-by-default: "we were not told" has to stay distinguishable from
        // "we were told it was not", or the attributor cannot know it is guessing.
        var frame = Single(string.Join("\n", ["MsBetweenPresents", "16.66"]));

        Assert.Null(frame.PresentMode);
        Assert.Null(frame.IsIndependentFlip);
        Assert.Null(frame.IsComposed);
    }

    [Fact]
    public void The_existing_frame_time_parser_still_behaves_exactly_as_before()
    {
        // ParseFrameTimes is the path every measurement in the product already runs through.
        // It is now implemented on top of the record parser, so this pins the two together.
        var csv = string.Join("\n",
        [
            "Application,ProcessID,MsBetweenPresents",
            "game.exe,1,16.66",
            "game.exe,1,NA",
            "game.exe,1,",
            "game.exe,1,8.33",
        ]);

        Assert.Equal([16.66, 8.33], PresentMonCsv.ParseFrameTimes(csv));
        Assert.Equal(
            PresentMonCsv.ParseFrameTimes(csv),
            PresentMonCsv.ParseFrames(csv)
                .Where(f => f.MsBetweenPresents.HasValue)
                .Select(f => f.MsBetweenPresents!.Value));
    }
}
