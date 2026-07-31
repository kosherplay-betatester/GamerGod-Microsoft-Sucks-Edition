using System;
using System.Windows;
using System.Windows.Interop;
using GamerGod.Windows.Native;

namespace GamerGod.Ui.Overlay;

/// <summary>
/// Owns the readout window and the key that toggles it.
///
/// <para>
/// Kept apart from the main window because the readout's lifetime is not the application's: it
/// appears and disappears many times in a session, and the rules about when it may be on screen
/// are worth reading in one place rather than scattered through a settings handler.
/// </para>
///
/// <para>
/// It knows nothing about any game. No window enumeration, no foreground-to-process resolution,
/// no handle to anything GamerGod did not create. Whether a game is running, and whether it is
/// covering the readout, are questions only frame data can answer — and until that data exists
/// the honest interface says it does not know rather than guessing from window rectangles.
/// </para>
/// </summary>
public sealed class ReadoutController : IDisposable
{
    /// <summary>Arbitrary, and only has to be unique within this process.</summary>
    private const int ToggleHotKeyId = 0xB001;

    /// <summary>Virtual-key code for O, as in the Ctrl+Shift+O the readout prints on itself.</summary>
    private const uint VkO = 0x4F;

    private readonly Window _owner;
    private readonly Func<ReadoutSnapshot> _source;

    private ReadoutWindow? _readout;
    private HwndSource? _messages;
    private bool _hotKeyRegistered;

    public ReadoutController(Window owner, Func<ReadoutSnapshot> source)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>True when the readout is currently on screen.</summary>
    public bool IsShowing => _readout is not null;

    /// <summary>
    /// Why the toggle key is not available, or null when it is.
    ///
    /// <para>
    /// Another application can already own Ctrl+Shift+O. The interface has to say so — a key
    /// that is printed on screen and does nothing is worse than one that was never offered.
    /// </para>
    /// </summary>
    public string? HotKeyProblem { get; private set; }

    /// <summary>
    /// Claims the toggle key. Call once the owner window has a handle.
    /// </summary>
    public void RegisterHotKey()
    {
        if (_hotKeyRegistered)
        {
            return;
        }

        var handle = new WindowInteropHelper(_owner).Handle;

        if (handle == IntPtr.Zero)
        {
            HotKeyProblem = "the window has no handle yet";
            return;
        }

        _messages = HwndSource.FromHwnd(handle);
        _messages?.AddHook(OnWindowMessage);

        if (OverlayNativeMethods.TryRegisterHotKey(handle, ToggleHotKeyId, VkO))
        {
            _hotKeyRegistered = true;
            HotKeyProblem = null;
            return;
        }

        HotKeyProblem = "Ctrl+Shift+O is already in use by another application";
    }

    public void Show()
    {
        if (_readout is not null)
        {
            return;
        }

        _readout = new ReadoutWindow { Owner = _owner };
        _readout.ReadFrom(_source);
        _readout.Show();

        // Placed after Show so SizeToContent has produced a real width to align against.
        _readout.PlaceIn(
            new Rect(
                SystemParameters.WorkArea.Left,
                SystemParameters.WorkArea.Top,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height),
            ReadoutCorner.TopLeft);

        _readout.ShowEscapeHint();
    }

    public void Hide()
    {
        _readout?.Close();
        _readout = null;
    }

    public void Toggle()
    {
        if (IsShowing)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private IntPtr OnWindowMessage(
        IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == OverlayNativeMethods.WmHotkey && wParam.ToInt32() == ToggleHotKeyId)
        {
            Toggle();
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Hide();

        if (_hotKeyRegistered)
        {
            OverlayNativeMethods.UnregisterHotKey(
                new WindowInteropHelper(_owner).Handle, ToggleHotKeyId);

            _hotKeyRegistered = false;
        }

        _messages?.RemoveHook(OnWindowMessage);
        _messages = null;
    }
}
