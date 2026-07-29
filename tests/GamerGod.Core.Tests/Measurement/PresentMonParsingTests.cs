using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Measurement;

/// <summary>
/// CSV parsing verified against real PresentMon output rather than an assumed shape.
///
/// <para>
/// The header below was captured from PresentMon 2.5.1 on the reference machine. Writing it
/// down matters because an earlier draft located the frame-time column by index, which appears
/// to work and silently reads a different metric the moment a version adds a column.
/// </para>
/// </summary>
public sealed class PresentMonParsingTests
{
    /// <summary>PresentMon 2.5.1, v2 metrics — the current default.</summary>
    private const string V2Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,"
        + "AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,"
        + "MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,"
        + "CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,"
        + "MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,"
        + "MsAllInputToPhotonLatency,MsClickToPhotonLatency";

    private static string V2Row(double frameTime) =>
        "game.exe,1234,0x25EA8554010,DXGI,0,0,0,Hardware Composed: Independent Flip,45.4893,NA,"
        + frameTime.ToString("0.00000000000000", System.Globalization.CultureInfo.InvariantCulture)
        + ",NA,0.04740000000000,0.60230000000000,16.2910,28.8952,16.6415,16.5941,0.0474,"
        + "16.7583,0.4381,0.4318,0.0063,NA,28.8952,NA,NA,NA";

    private static IEnumerable<double> Parse(string csv) =>
        PresentMonCsv.ParseFrameTimes(csv);

    [Fact]
    public void Frame_times_are_read_from_real_presentmon_output()
    {
        var csv = string.Join("\n", [V2Header, V2Row(16.6607), V2Row(16.5941), V2Row(33.3214)]);

        Assert.Equal([16.6607, 16.5941, 33.3214], Parse(csv).Select(v => Math.Round(v, 4)));
    }

    [Fact]
    public void The_column_is_located_by_name_so_a_new_column_cannot_shift_it()
    {
        // A future version inserting a column ahead of MsBetweenPresents must not cause the
        // wrong metric to be read. An index-based parser would report GPU time as frame time
        // and every downstream number would be confidently wrong.
        var shifted = "SomethingNew," + V2Header;
        var row = "0," + V2Row(16.6607);

        Assert.Equal([16.6607], Parse(string.Join("\n", [shifted, row])).Select(v => Math.Round(v, 4)));
    }

    [Fact]
    public void The_1x_column_name_is_still_accepted()
    {
        // PresentMon 1.x and --v1_metrics both emit msBetweenPresents in lower camel case.
        var csv = string.Join("\n",
        [
            "Application,ProcessID,msBetweenPresents,msInPresentAPI",
            "game.exe,1234,16.66,0.04",
            "game.exe,1234,8.33,0.04",
        ]);

        Assert.Equal([16.66, 8.33], Parse(csv));
    }

    [Fact]
    public void Rows_with_unparseable_values_are_skipped_not_guessed()
    {
        var csv = string.Join("\n",
        [
            "Application,ProcessID,MsBetweenPresents",
            "game.exe,1,16.66",
            "game.exe,1,NA",
            "game.exe,1,",
            "game.exe,1,8.33",
        ]);

        Assert.Equal([16.66, 8.33], Parse(csv));
    }

    [Fact]
    public void A_warning_line_before_the_header_does_not_break_parsing()
    {
        // PresentMon writes warnings to the same stream, e.g. about an existing trace session.
        var csv = string.Join("\n",
        [
            "warning: a trace session named \"PresentMon\" is already running",
            "Application,ProcessID,MsBetweenPresents",
            "game.exe,1,16.66",
        ]);

        Assert.Equal([16.66], Parse(csv));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no header here at all")]
    [InlineData("Application,ProcessID\ngame.exe,1")]
    public void Output_without_a_frame_time_column_yields_nothing_rather_than_nonsense(string csv)
    {
        Assert.Empty(Parse(csv));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        var csv = "Application,MsBetweenPresents\r\ngame.exe,16.66\r\ngame.exe,8.33\r\n";

        Assert.Equal([16.66, 8.33], Parse(csv));
    }

    [Fact]
    public void A_capture_is_only_useful_once_it_becomes_a_series()
    {
        // End to end: real header shape through to statistics, which is the path that matters.
        var rows = Enumerable.Repeat(V2Row(16.6607), 200).Prepend(V2Header);
        var series = FrameSeries.FromFrameTimes(Parse(string.Join("\n", rows)));

        Assert.Equal(200, series.Count);
        Assert.Equal(60.0, series.AverageFps, 1);
        Assert.Equal(100.0, series.ConsistencyPercent, 1);
    }
}
