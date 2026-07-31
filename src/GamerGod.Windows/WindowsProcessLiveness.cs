using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GamerGod.Core.Recovery;
using GamerGod.Windows.Native;

namespace GamerGod.Windows;

/// <summary>
/// Answers whether the process that armed a session is still running, using the narrowest
/// access right that can answer the question.
///
/// <para>
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> and nothing else. No synchronise right, no
/// terminate right, no read of anything inside the process. The watchdog runs as LocalSystem
/// and could ask for far more; it must not, because a service with a handle it does not need
/// is a service somebody has to justify.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessLiveness : IProcessLiveness
{
    public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            return ValueTask.FromResult<ProcessIdentity?>(null);
        }

        var handle = AmbientNativeMethods.OpenProcess(
            AmbientNativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
        {
            var openError = Marshal.GetLastWin32Error();

            // No such process id. Every other failure - a refused open, a transient error -
            // is reported as an exception rather than as an exit, because the watchdog acts
            // on "gone" and must never act on "could not tell".
            if (openError == AmbientNativeMethods.ErrorInvalidParameter)
            {
                return ValueTask.FromResult<ProcessIdentity?>(null);
            }

            throw new Win32Exception(openError, $"Cannot query process {processId}.");
        }

        try
        {
            if (!AmbientNativeMethods.GetExitCodeProcess(handle, out var exitCode))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), $"Cannot read the state of process {processId}.");
            }

            // A process that exits with code 259 is indistinguishable from a running one by
            // this route, and the alternative needs a SYNCHRONIZE handle we have chosen not
            // to take. The consequence is bounded: the watchdog keeps waiting, and the id
            // being reused changes the identity below, which fires it anyway.
            if (exitCode != AmbientNativeMethods.StillActive)
            {
                return ValueTask.FromResult<ProcessIdentity?>(null);
            }

            if (!AmbientNativeMethods.GetProcessTimes(handle, out var created, out _, out _, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), $"Cannot read the start time of process {processId}.");
            }

            return ValueTask.FromResult<ProcessIdentity?>(
                new ProcessIdentity(processId, DateTime.FromFileTimeUtc(created).Ticks));
        }
        finally
        {
            AmbientNativeMethods.CloseHandle(handle);
        }
    }
}
