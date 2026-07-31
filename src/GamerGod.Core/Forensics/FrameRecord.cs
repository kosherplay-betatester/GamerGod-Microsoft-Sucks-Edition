namespace GamerGod.Core.Forensics;

/// <summary>
/// One frame, as PresentMon described it.
///
/// <para>
/// Every metric is nullable, and that is the whole design. PresentMon writes <c>NA</c> for a
/// column it could not fill — which happens routinely, per title and per driver, and for whole
/// captures under <c>--v1_metrics</c>. A parser that folded <c>NA</c> into <c>0</c> would turn
/// "we were not told what the GPU did" into "the GPU did nothing", and every rule downstream
/// would then reach a confident conclusion from a value nobody measured.
/// </para>
///
/// <para>
/// Only the columns the attributor actually reads are here. PresentMon emits around thirty and
/// carrying the rest would invite a rule to be written against a column no test covers.
/// </para>
/// </summary>
public sealed record FrameRecord
{
    /// <summary>Position in the capture, from zero. Present even when every metric is null.</summary>
    public required int Index { get; init; }

    /// <summary>Timestamp within the capture, in milliseconds.</summary>
    public double? TimeInMs { get; init; }

    /// <summary>Frame time: the interval between this present and the previous one.</summary>
    public double? MsBetweenPresents { get; init; }

    /// <summary>Time the application's CPU work occupied within the frame.</summary>
    public double? MsCPUBusy { get; init; }

    /// <summary>Time the application's CPU thread spent blocked within the frame.</summary>
    public double? MsCPUWait { get; init; }

    /// <summary>Time the GPU spent executing work for the frame.</summary>
    public double? MsGPUBusy { get; init; }

    /// <summary>Time the GPU spent idle while the frame was in flight.</summary>
    public double? MsGPUWait { get; init; }

    /// <summary>Time between the present starting and the GPU beginning work on it.</summary>
    public double? MsGPULatency { get; init; }

    /// <summary>Time between the frame being ready and its flip being issued.</summary>
    public double? MsFlipDelay { get; init; }

    /// <summary>Time the CPU spent inside the Present call.</summary>
    public double? MsInPresentAPI { get; init; }

    /// <summary>Time between the present starting and the frame being ready to display.</summary>
    public double? MsRenderPresentLatency { get; init; }

    /// <summary>
    /// How Windows presented the frame — <c>Hardware: Independent Flip</c>,
    /// <c>Composed: Flip</c>, and so on. Null when the column was absent or <c>NA</c>.
    /// </summary>
    public string? PresentMode { get; init; }

    /// <summary>
    /// Whether the frame went to the screen by independent flip. Null when the mode is unknown.
    ///
    /// <para>
    /// Both <c>Hardware: Independent Flip</c> and <c>Hardware Composed: Independent Flip</c>
    /// count. The second is the multi-plane-overlay path: the display hardware composes, the
    /// desktop compositor is not in the frame's way, and treating it as composed would
    /// misattribute nearly every hitch on a modern full-screen game.
    /// </para>
    /// </summary>
    public bool? IsIndependentFlip =>
        Mode is { } mode ? mode.Contains("Independent Flip", StringComparison.OrdinalIgnoreCase) : null;

    /// <summary>
    /// Whether the desktop compositor was in the presentation path. Null when the mode is
    /// unknown.
    ///
    /// <para>
    /// Matched on the <c>Composed:</c> prefix rather than the word "Composed" anywhere, because
    /// <c>Hardware Composed: Independent Flip</c> contains the word and is the opposite case. A
    /// mode string this does not recognise reads as <c>false</c>, which costs an attribution
    /// rather than producing a wrong one.
    /// </para>
    /// </summary>
    public bool? IsComposed =>
        Mode is { } mode ? mode.StartsWith("Composed:", StringComparison.OrdinalIgnoreCase) : null;

    private string? Mode => string.IsNullOrWhiteSpace(PresentMode) ? null : PresentMode;
}
