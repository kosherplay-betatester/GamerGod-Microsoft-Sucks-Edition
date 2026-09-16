using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// The one call needed to hand foreground rights to the copy of GamerGod that is already
/// running.
///
/// <para>
/// Windows will not let an arbitrary process pull a window in front of whatever the user is
/// looking at — a rule worth having, and the reason a naive "bring it to the front" only ever
/// flashes the taskbar button. The process being <em>started</em> by the user does briefly hold
/// the right to give it away, which is exactly the situation here: someone double-clicked
/// GamerGod, so the shell hands this new process the foreground, and this new process passes
/// that on to the instance that will actually serve them before exiting.
/// </para>
///
/// <para>
/// Nothing here enumerates windows, resolves a foreground handle to a process, or touches a
/// window this product did not create. It grants a permission and returns.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class ForegroundNativeMethods
{
    /// <summary>
    /// Any process may take the foreground.
    ///
    /// <para>
    /// Used rather than a process id because the id is not known: the running instance is found
    /// through a named mutex, which identifies an instance without identifying a process. The
    /// grant lasts until the next foreground change, which is the very next thing that happens.
    /// </para>
    /// </summary>
    public const int AllowAnyProcess = -1;

    /// <summary>
    /// Permits another process to call <c>SetForegroundWindow</c>. Failure is not worth
    /// reporting: the window still restores, it simply may not come to the front.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);
}
