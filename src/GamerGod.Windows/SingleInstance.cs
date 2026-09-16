using System.Runtime.Versioning;
using GamerGod.Windows.Native;

namespace GamerGod.Windows;

/// <summary>
/// Keeps GamerGod to one window per signed-in user, and sends a second launch to the first one.
///
/// <para>
/// Two copies is not a cosmetic problem here. Each one puts its own icon in the notification
/// area — which is how it was noticed — and each one holds its own elevated helper, so a user
/// who had approved a permission prompt once would be asked again by a window that looked
/// identical. Worse, both read and write the same journal: one could arm while the other still
/// believed the machine was untouched, and the switch in each would disagree about a machine
/// only one of them had changed.
/// </para>
///
/// <para>
/// <b>Per user session, deliberately.</b> The names are <c>Local\</c>, not <c>Global\</c>, so two
/// people signed in at once — fast user switching, or a shared machine — each get their own
/// window. A global lock would mean the second person's GamerGod silently refused to open and
/// handed activation to somebody else's desktop.
/// </para>
///
/// <para>
/// The mutex decides who is first; a named event carries the "someone tried to start me"
/// message. No pipe, no port, nothing to secure: an event can only be set, so the most a second
/// process can say is that it exists.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\GamerGod.SingleInstance";
    private const string EventName = @"Local\GamerGod.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private RegisteredWaitHandle? _registration;
    private bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Raised when another copy of GamerGod was started and asked this one to show itself.
    ///
    /// <para>
    /// Arrives on a thread-pool thread. A WPF handler has to marshal to the dispatcher before
    /// touching a window.
    /// </para>
    /// </summary>
    public event Action? ActivationRequested;

    /// <summary>
    /// Claims the single-instance slot.
    ///
    /// <para>
    /// Returns null when another copy already holds it — and before returning, asks that copy to
    /// come to the front. The caller's only job then is to exit quietly: the user pressed
    /// GamerGod and a GamerGod window is about to be in front of them, which is what they meant.
    /// </para>
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        // Created before the mutex is examined, so the running instance's wait is already armed
        // no matter which process reaches this line first.
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var isFirst);

        if (isFirst)
        {
            var instance = new SingleInstance(mutex, activate);
            instance.Listen();
            return instance;
        }

        // Second copy. Hand over the foreground rights this process holds by virtue of having
        // just been started, then knock.
        //
        // Both calls are best-effort. If the grant fails the window still restores and merely
        // flashes in the taskbar; if the event cannot be set the user is no worse off than
        // before this existed.
        try
        {
            ForegroundNativeMethods.AllowSetForegroundWindow(ForegroundNativeMethods.AllowAnyProcess);
            activate.Set();
        }
        catch (Exception)
        {
            // Nothing useful to do: this process is about to exit either way.
        }

        mutex.Dispose();
        activate.Dispose();

        return null;
    }

    private void Listen() =>
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    ActivationRequested?.Invoke();
                }
            },
            state: null,
            Timeout.Infinite,

            // Not once: a user may press the shortcut repeatedly, and every press should bring
            // the window forward rather than only the first.
            executeOnlyOnce: false);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _registration?.Unregister(null);

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owner — the process is exiting anyway and Windows releases it.
        }

        _mutex.Dispose();
        _activate.Dispose();
    }
}
