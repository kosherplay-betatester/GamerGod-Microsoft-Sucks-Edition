using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// The window styles that make a readout float over a game without being part of it.
///
/// <para>
/// Every call here acts on <b>a window this process owns</b>. Nothing enumerates windows,
/// resolves a foreground handle to a process, or reads anything belonging to a game. That is
/// not caution, it is the Charter: Article III forbids injecting into, hooking or modifying a
/// game, and the first step down that road is always identifying its window.
/// </para>
///
/// <para>
/// It is also why this overlay cannot draw over an exclusive-fullscreen game and every other
/// overlay can. Steam, Discord and RivaTuner load their code inside the game and draw from
/// within its own present chain. The compliant technique is a separate top-most window, and the
/// compositor will not place any window above an application that owns the display outright.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class OverlayNativeMethods
{
    private const int GwlExStyle = -20;

    /// <summary>Composited by the window manager, which is what allows per-window alpha.</summary>
    private const int WsExLayered = 0x00080000;

    /// <summary>
    /// Clicks pass straight through to whatever is underneath.
    ///
    /// <para>
    /// The consequence is easy to miss and shapes the whole design: <b>the readout can never
    /// receive a click</b>. No notice it displays may carry a button, so every action is a
    /// keypress named in the notice itself. Making it hit-testable even briefly would steal
    /// input from the game, which is worse than anything it would solve.
    /// </para>
    /// </summary>
    private const int WsExTransparent = 0x00000020;

    /// <summary>Never takes focus, so alt-tab order and the game's input are untouched.</summary>
    private const int WsExNoActivate = 0x08000000;

    /// <summary>Kept out of the taskbar and the alt-tab list; it is a readout, not a program.</summary>
    private const int WsExToolWindow = 0x00000080;

    /// <summary>
    /// Makes a window of ours click-through, non-activating and invisible to alt-tab.
    /// </summary>
    /// <param name="ownWindowHandle">
    /// A handle to a window <em>this process created</em>. Passing anything else would be a
    /// Charter violation, and there is no code path here that could produce one.
    /// </param>
    public static void MakeClickThrough(IntPtr ownWindowHandle)
    {
        if (ownWindowHandle == IntPtr.Zero)
        {
            return;
        }

        var current = GetWindowLongPtrW(ownWindowHandle, GwlExStyle).ToInt64();

        var updated = current
            | WsExLayered
            | WsExTransparent
            | WsExNoActivate
            | WsExToolWindow;

        SetWindowLongPtrW(ownWindowHandle, GwlExStyle, new IntPtr(updated));
    }

    /// <summary>Ctrl + Shift, the modifiers the readout's own hint names.</summary>
    private const uint ModControl = 0x0002;

    private const uint ModShift = 0x0004;

    /// <summary>Suppresses the auto-repeat a held key would otherwise produce.</summary>
    private const uint ModNoRepeat = 0x4000;

    /// <summary>The message a registered hotkey delivers to the owning window.</summary>
    public const int WmHotkey = 0x0312;

    /// <summary>
    /// Registers Ctrl+Shift+&lt;key&gt; against a window this process owns.
    ///
    /// <para>
    /// A system-wide hotkey rather than a hook, and the distinction is the whole Charter
    /// argument. <c>RegisterHotKey</c> asks Windows to deliver a message to <em>our</em> window
    /// when a combination is pressed; it loads no code anywhere, reads no other process's
    /// input, and is the mechanism Windows publishes for exactly this. A keyboard hook would
    /// see every keystroke in every application, which is indistinguishable from a keylogger at
    /// the telemetry level and forbidden outright.
    /// </para>
    ///
    /// <para>
    /// Returns false when another application already owns the combination. That is an ordinary
    /// outcome — plenty of software claims Ctrl+Shift chords — and the interface has to say so
    /// rather than leaving a key that silently does nothing.
    /// </para>
    /// </summary>
    public static bool TryRegisterHotKey(IntPtr ownWindowHandle, int id, uint virtualKey) =>
        ownWindowHandle != IntPtr.Zero
        && RegisterHotKey(ownWindowHandle, id, ModControl | ModShift | ModNoRepeat, virtualKey);

    public static void UnregisterHotKey(IntPtr ownWindowHandle, int id)
    {
        if (ownWindowHandle != IntPtr.Zero)
        {
            _ = UnregisterHotKeyNative(ownWindowHandle, id);
        }
    }

    // SetWindowLongPtrW is the 64-bit form. GetWindowLongPtrW does not exist as an export on
    // 32-bit Windows, but GamerGod is x64-only - the installer refuses anything else, because
    // every scheduling API it depends on is 64-bit in practice.

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "UnregisterHotKey")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKeyNative(IntPtr hWnd, int id);
}
