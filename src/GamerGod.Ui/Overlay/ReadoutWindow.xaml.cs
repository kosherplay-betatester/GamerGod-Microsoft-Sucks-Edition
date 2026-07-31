using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GamerGod.Windows.Native;

namespace GamerGod.Ui.Overlay;

/// <summary>
/// The frame readout, drawn in a window of GamerGod's own.
///
/// <para>
/// It never touches the game. It does not enumerate windows, resolve a foreground handle to a
/// process, or open anything belonging to a running title — Article III, and the first step
/// down that road is always identifying the game's window. What appears here comes from frame
/// data GamerGod already captures and from GamerGod's own state.
/// </para>
///
/// <para>
/// That restraint is exactly why it cannot draw over an exclusive-fullscreen game while Steam's
/// and RivaTuner's can. They run their code inside the game. This is a separate top-most window,
/// and Windows will not put any window above an application that owns the display outright. The
/// interface says so before anyone enables it rather than after they wonder.
/// </para>
/// </summary>
public partial class ReadoutWindow : Window
{
    /// <summary>
    /// Twice a second. A number that changes 140 times a second is not readable, and every
    /// change is a repaint the compositor has to carry — which is the cost this whole feature
    /// has to justify.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long the dismissal hint stays before the readout goes quiet.</summary>
    private static readonly TimeSpan EscapeHintDuration = TimeSpan.FromSeconds(6);

    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _hideHint;

    private Func<ReadoutSnapshot>? _source;

    public ReadoutWindow()
    {
        InitializeComponent();

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = RefreshInterval,
        };
        _refresh.Tick += (_, _) => Draw();

        _hideHint = new DispatcherTimer { Interval = EscapeHintDuration };
        _hideHint.Tick += (_, _) =>
        {
            _hideHint.Stop();
            Escape.Visibility = Visibility.Collapsed;
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ShowEscapeHint();
    }

    /// <summary>
    /// Where the numbers come from. Set before showing; called on the UI thread twice a second.
    /// </summary>
    public void ReadFrom(Func<ReadoutSnapshot> source)
    {
        _source = source;
        Draw();
    }

    /// <summary>
    /// Puts the readout in a corner of the given screen area, inset far enough to clear the
    /// rounded corners some games draw at the edges of a borderless window.
    /// </summary>
    public void PlaceIn(Rect workArea, ReadoutCorner corner)
    {
        const double inset = 16;

        // SizeToContent means Width and Height are only real after a layout pass.
        UpdateLayout();

        Left = corner is ReadoutCorner.TopLeft or ReadoutCorner.BottomLeft
            ? workArea.Left + inset
            : workArea.Right - ActualWidth - inset;

        Top = corner is ReadoutCorner.TopLeft or ReadoutCorner.TopRight
            ? workArea.Top + inset
            : workArea.Bottom - ActualHeight - inset;
    }

    /// <summary>Re-shows the dismissal hint. Called on every change of detail level.</summary>
    public void ShowEscapeHint()
    {
        Escape.Visibility = Visibility.Visible;
        _hideHint.Stop();
        _hideHint.Start();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // WS_EX_NOACTIVATE should prevent this outright. If it ever happens anyway, handing
        // focus straight back is far better than a readout stealing input mid-match.
        if (Owner is not null)
        {
            Owner.Activate();
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Applied here rather than in the constructor because the handle does not exist until
        // now, and applied to our own window only.
        OverlayNativeMethods.MakeClickThrough(new WindowInteropHelper(this).Handle);

        _refresh.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refresh.Stop();
        _hideHint.Stop();
        base.OnClosed(e);
    }

    /// <summary>
    /// Paints the current values.
    ///
    /// <para>
    /// A zero never stands in for an unknown. Before enough frames have arrived the field shows
    /// an em dash, because "0 FPS" is a measurement and "we do not know yet" is not.
    /// </para>
    /// </summary>
    private void Draw()
    {
        var snapshot = _source?.Invoke() ?? ReadoutSnapshot.Unknown;

        ArmedMark.Foreground = new SolidColorBrush(
            snapshot.Armed
                ? Color.FromRgb(0xFF, 0xB4, 0x54)
                : Color.FromRgb(0x5A, 0x63, 0x77));

        Fps.Text = Number(snapshot.Fps);
        OnePercentLow.Text = Number(snapshot.OnePercentLowFps);
        Hitches.Text = snapshot.Hitches?.ToString() ?? "—";
    }

    private static string Number(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v).ToString("0") : "—";
}

public enum ReadoutCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// One reading. Every field is nullable because "not measured yet" is a real state that must
/// survive all the way to the screen rather than being flattened into a zero on the way.
/// </summary>
public readonly record struct ReadoutSnapshot(
    bool Armed,
    double? Fps,
    double? OnePercentLowFps,
    int? Hitches)
{
    public static ReadoutSnapshot Unknown => new(false, null, null, null);
}
