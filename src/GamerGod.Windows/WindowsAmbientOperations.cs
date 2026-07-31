using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Windows.Native;

namespace GamerGod.Windows;

/// <summary>
/// The real implementation of the ambient levers.
///
/// <para>
/// Everything here operates on processes and services other than the game. No method in this
/// class is ever pointed at a title, which is what makes the whole set ambient: a game's
/// process, handles, modules and registry view are identical whether GamerGod is running or
/// not. Only the machine around it is quieter.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsAmbientOperations : IAmbientOperations
{
    private const int ErrorServiceNotActive = 1062;
    private const int ErrorServiceAlreadyRunning = 1056;

    public ValueTask<ImmutableArray<ProcessInfo>> ListProcessesAsync(CancellationToken cancellationToken)
    {
        var processes = ImmutableArray.CreateBuilder<ProcessInfo>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    processes.Add(new ProcessInfo(process.Id, process.ProcessName, process.WorkingSet64));
                }
                catch (Exception)
                {
                    // Exited between enumeration and inspection. Ordinary desktop churn.
                }
            }
        }

        return ValueTask.FromResult(processes.ToImmutable());
    }

    public ValueTask<bool> IsEfficiencyModeAsync(int processId, CancellationToken cancellationToken)
    {
        var handle = AmbientNativeMethods.OpenProcess(
            AmbientNativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
        {
            return ValueTask.FromResult(false);
        }

        try
        {
            var state = new AmbientNativeMethods.PowerThrottlingState
            {
                Version = AmbientNativeMethods.PowerThrottlingCurrentVersion,
            };

            if (!AmbientNativeMethods.GetProcessInformation(
                    handle,
                    AmbientNativeMethods.ProcessPowerThrottling,
                    ref state,
                    (uint)Marshal.SizeOf<AmbientNativeMethods.PowerThrottlingState>()))
            {
                return ValueTask.FromResult(false);
            }

            var throttled =
                (state.ControlMask & AmbientNativeMethods.PowerThrottlingExecutionSpeed) != 0
                && (state.StateMask & AmbientNativeMethods.PowerThrottlingExecutionSpeed) != 0;

            return ValueTask.FromResult(throttled);
        }
        finally
        {
            AmbientNativeMethods.CloseHandle(handle);
        }
    }

    public ValueTask SetEfficiencyModeAsync(
        int processId, bool enabled, CancellationToken cancellationToken)
    {
        // PROCESS_SET_INFORMATION and nothing more. No memory access, no thread creation,
        // no termination rights.
        var handle = AmbientNativeMethods.OpenProcess(
            AmbientNativeMethods.ProcessSetInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open process {processId}.");
        }

        try
        {
            var state = new AmbientNativeMethods.PowerThrottlingState
            {
                Version = AmbientNativeMethods.PowerThrottlingCurrentVersion,
                ControlMask = AmbientNativeMethods.PowerThrottlingExecutionSpeed,

                // Clearing StateMask while keeping ControlMask set means "explicitly not
                // throttled", which is not the same as the default. On revert the whole
                // struct is zeroed instead, handing the decision back to Windows exactly as
                // it was before GamerGod ran.
                StateMask = enabled ? AmbientNativeMethods.PowerThrottlingExecutionSpeed : 0,
            };

            if (!enabled)
            {
                state.ControlMask = 0;
            }

            if (!AmbientNativeMethods.SetProcessInformation(
                    handle,
                    AmbientNativeMethods.ProcessPowerThrottling,
                    ref state,
                    (uint)Marshal.SizeOf<AmbientNativeMethods.PowerThrottlingState>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), $"Cannot set efficiency mode on process {processId}.");
            }
        }
        finally
        {
            AmbientNativeMethods.CloseHandle(handle);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IConfinement> ConfineAsync(
        string name,
        ProcessorMask mask,
        IEnumerable<int> processIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (mask.IsEmpty)
        {
            throw new ArgumentException(
                "Refusing to confine processes to an empty processor mask - Windows would have "
                + "nowhere to schedule them.",
                nameof(mask));
        }

        var job = AmbientNativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the job object.");
        }

        try
        {
            var limits = new AmbientNativeMethods.JobObjectBasicLimits
            {
                LimitFlags = AmbientNativeMethods.JobObjectLimitAffinity,
                Affinity = (UIntPtr)mask.Mask,
            };

            if (!AmbientNativeMethods.SetInformationJobObject(
                    job,
                    AmbientNativeMethods.JobObjectBasicLimitInformation,
                    ref limits,
                    (uint)Marshal.SizeOf<AmbientNativeMethods.JobObjectBasicLimits>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), "Cannot set the job object's affinity limit.");
            }

            var assigned = 0;

            foreach (var id in processIds)
            {
                var handle = AmbientNativeMethods.OpenProcess(
                    AmbientNativeMethods.ProcessSetQuota | AmbientNativeMethods.ProcessTerminate,
                    false,
                    (uint)id);

                if (handle == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (AmbientNativeMethods.AssignProcessToJobObject(job, handle))
                    {
                        assigned++;
                    }

                    // A process already in an incompatible job, or protected by the system,
                    // simply stays where it is. One refusal is not worth failing the session.
                }
                finally
                {
                    AmbientNativeMethods.CloseHandle(handle);
                }
            }

            return ValueTask.FromResult<IConfinement>(new JobConfinement(name, job, assigned));
        }
        catch
        {
            AmbientNativeMethods.CloseHandle(job);
            throw;
        }
    }

    /// <summary>
    /// Reads a process's affinity <em>and the group it is relative to</em>.
    ///
    /// <para>
    /// This used to report group zero for everything, because <c>Process.ProcessorAffinity</c>
    /// has no way to say otherwise. On a machine with more than 64 logical processors that
    /// journals a restore point naming the wrong processors, and revert-exactly is the whole
    /// guarantee. Invisible on any consumer part; wrong on every Threadripper and Xeon.
    /// </para>
    /// </summary>
    public ValueTask<ProcessorMask> GetAffinityAsync(int processId, CancellationToken cancellationToken)
    {
        var handle = AmbientNativeMethods.OpenProcess(
            AmbientNativeMethods.ProcessQueryLimitedInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), $"Cannot read the affinity of process {processId}.");
        }

        try
        {
            var group = GroupOf(handle, processId);

            if (!AmbientNativeMethods.GetProcessAffinityMask(handle, out var mask, out _))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), $"Cannot read the affinity mask of process {processId}.");
            }

            return ValueTask.FromResult(new ProcessorMask(group, (ulong)mask));
        }
        finally
        {
            AmbientNativeMethods.CloseHandle(handle);
        }
    }

    public ValueTask SetAffinityAsync(
        int processId, ProcessorMask mask, CancellationToken cancellationToken)
    {
        if (mask.IsEmpty)
        {
            throw new ArgumentException(
                "Refusing to set an empty processor affinity - the process would become "
                + "unschedulable.",
                nameof(mask));
        }

        // PROCESS_QUERY_LIMITED_INFORMATION to read the group, PROCESS_SET_INFORMATION to
        // write the mask. Nothing wider.
        var handle = AmbientNativeMethods.OpenProcess(
            AmbientNativeMethods.ProcessQueryLimitedInformation | AmbientNativeMethods.ProcessSetInformation,
            false,
            (uint)processId);

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), $"Cannot open process {processId} to set its affinity.");
        }

        try
        {
            var group = GroupOf(handle, processId);

            if (group != mask.Group)
            {
                // Windows would not fail this - it would apply the mask relative to the group
                // the process is already in, quietly selecting different processors. That is
                // exactly the silent wrong answer this refusal exists to prevent, and it is
                // also a mask we could never claim to have restored exactly.
                throw new InvalidOperationException(
                    $"Refusing to write a group {mask.Group} affinity mask onto process "
                    + $"{processId}, which runs in group {group}. The same bits name different "
                    + "processors in each group.");
            }

            if (!AmbientNativeMethods.SetProcessAffinityMask(handle, (UIntPtr)mask.Mask))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(), $"Cannot set the affinity of process {processId}.");
            }
        }
        finally
        {
            AmbientNativeMethods.CloseHandle(handle);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The single processor group a process belongs to.
    ///
    /// <para>
    /// A process whose threads span groups has no single affinity mask, so there is nothing
    /// GamerGod could capture that it could put back exactly. Refusing is the only honest
    /// answer available: the alternative is a journal entry claiming a restore point that
    /// process never had.
    /// </para>
    /// </summary>
    private static ushort GroupOf(IntPtr handle, int processId)
    {
        ushort count = 1;
        var groups = new ushort[1];

        if (AmbientNativeMethods.GetProcessGroupAffinity(handle, ref count, groups))
        {
            return groups[0];
        }

        var error = Marshal.GetLastWin32Error();

        if (error == AmbientNativeMethods.ErrorInsufficientBuffer)
        {
            throw new InvalidOperationException(
                $"Process {processId} has threads in {count} processor groups. GamerGod does not "
                + "move a process whose affinity it could not restore exactly.");
        }

        throw new Win32Exception(
            error, $"Cannot read the processor group of process {processId}.");
    }

    public ValueTask<ServiceInfo?> QueryServiceAsync(string name, CancellationToken cancellationToken)
    {
        var manager = AmbientNativeMethods.OpenSCManagerW(null, null, AmbientNativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return ValueTask.FromResult<ServiceInfo?>(null);
        }

        try
        {
            var service = AmbientNativeMethods.OpenServiceW(
                manager, name, AmbientNativeMethods.ServiceQueryStatus);

            if (service == IntPtr.Zero)
            {
                return ValueTask.FromResult<ServiceInfo?>(null);
            }

            try
            {
                var status = default(AmbientNativeMethods.ServiceStatus);
                if (!AmbientNativeMethods.QueryServiceStatus(service, ref status))
                {
                    return ValueTask.FromResult<ServiceInfo?>(null);
                }

                // Start type is read but never written. Stopping a service is undone by a
                // reboot; changing its start type would outlive one.
                return ValueTask.FromResult<ServiceInfo?>(new ServiceInfo(
                    name,
                    status.CurrentState == AmbientNativeMethods.ServiceRunning,
                    "unchanged"));
            }
            finally
            {
                AmbientNativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            AmbientNativeMethods.CloseServiceHandle(manager);
        }
    }

    public ValueTask StopServiceAsync(string name, CancellationToken cancellationToken) =>
        ControlServiceAsync(name, AmbientNativeMethods.ServiceStop, stop: true);

    public ValueTask StartServiceAsync(string name, CancellationToken cancellationToken) =>
        ControlServiceAsync(name, AmbientNativeMethods.ServiceStart, stop: false);

    private static ValueTask ControlServiceAsync(string name, uint access, bool stop)
    {
        var manager = AmbientNativeMethods.OpenSCManagerW(null, null, AmbientNativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot reach the service control manager.");
        }

        try
        {
            var service = AmbientNativeMethods.OpenServiceW(manager, name, access);
            if (service == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open the {name} service.");
            }

            try
            {
                var status = default(AmbientNativeMethods.ServiceStatus);
                var ok = stop
                    ? AmbientNativeMethods.ControlService(service, AmbientNativeMethods.ServiceControlStop, ref status)
                    : AmbientNativeMethods.StartServiceW(service, 0, IntPtr.Zero);

                if (ok)
                {
                    return ValueTask.CompletedTask;
                }

                var error = Marshal.GetLastWin32Error();

                // Already in the requested state. That is the outcome the caller wanted, so
                // treating it as a failure would make revert non-idempotent.
                if ((stop && error == ErrorServiceNotActive)
                    || (!stop && error == ErrorServiceAlreadyRunning))
                {
                    return ValueTask.CompletedTask;
                }

                throw new Win32Exception(
                    error, $"Cannot {(stop ? "stop" : "start")} the {name} service.");
            }
            finally
            {
                AmbientNativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            AmbientNativeMethods.CloseServiceHandle(manager);
        }
    }

    public ValueTask<Guid> GetActivePowerSchemeAsync(CancellationToken cancellationToken)
    {
        if (AmbientNativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0
            || pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot read the active power scheme.");
        }

        try
        {
            return ValueTask.FromResult(Marshal.PtrToStructure<Guid>(pointer));
        }
        finally
        {
            AmbientNativeMethods.LocalFree(pointer);
        }
    }

    public ValueTask SetActivePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken)
    {
        if (AmbientNativeMethods.PowerSetActiveScheme(IntPtr.Zero, ref scheme) != 0)
        {
            throw new InvalidOperationException($"Cannot activate power scheme {scheme}.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> DuplicatePowerSchemeAsync(
        Guid source, string name, CancellationToken cancellationToken)
    {
        var destination = IntPtr.Zero;

        if (AmbientNativeMethods.PowerDuplicateScheme(IntPtr.Zero, ref source, ref destination) != 0
            || destination == IntPtr.Zero)
        {
            throw new InvalidOperationException("Cannot duplicate the active power scheme.");
        }

        try
        {
            var created = Marshal.PtrToStructure<Guid>(destination);

            // Named so a user who finds it in their power settings knows where it came from
            // and that deleting it is safe.
            AmbientNativeMethods.PowerWriteFriendlyName(
                IntPtr.Zero, ref created, IntPtr.Zero, IntPtr.Zero, name, (uint)((name.Length + 1) * 2));

            return ValueTask.FromResult(created);
        }
        finally
        {
            AmbientNativeMethods.LocalFree(destination);
        }
    }

    public ValueTask DeletePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken)
    {
        AmbientNativeMethods.PowerDeleteScheme(IntPtr.Zero, ref scheme);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A live job object. Closing the handle lifts the affinity limit.
    ///
    /// <para>
    /// This is the strongest reversal guarantee in the project, and it belongs to Windows
    /// rather than to us: a job object's limits exist only while a handle to it is open, so
    /// if GamerGod is killed, crashes, or the machine loses power, the confinement ends
    /// without any of our code running.
    /// </para>
    /// </summary>
    private sealed class JobConfinement(string name, IntPtr job, int assigned) : IConfinement
    {
        private IntPtr _job = job;

        public string Name => name;

        public int ProcessCount => assigned;

        public ValueTask DisposeAsync()
        {
            var handle = Interlocked.Exchange(ref _job, IntPtr.Zero);
            if (handle != IntPtr.Zero)
            {
                AmbientNativeMethods.CloseHandle(handle);
            }

            return ValueTask.CompletedTask;
        }
    }
}
