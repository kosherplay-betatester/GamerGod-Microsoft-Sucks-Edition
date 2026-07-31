using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// Native surface for the ambient levers.
///
/// <para>
/// Every entry point here changes something, so it is worth being explicit about what is
/// absent. There is no <c>WriteProcessMemory</c>, no <c>CreateRemoteThread</c>, no
/// <c>VirtualAllocEx</c>, no hook, no driver load, and no handle opened with
/// <c>PROCESS_ALL_ACCESS</c>. Every process handle below requests the narrowest access that
/// will do the job, and none of these are ever pointed at a game.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AmbientNativeMethods
{
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";
    private const string PowrProf = "powrprof.dll";

    // ---- Process access rights. Least privilege, every time. ----------

    /// <summary>Read a process's identity and limited state.</summary>
    internal const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>Required by SetProcessInformation for power throttling.</summary>
    internal const uint ProcessSetInformation = 0x0200;

    /// <summary>Required, with terminate rights, to place a process in a job object.</summary>
    internal const uint ProcessSetQuota = 0x0100;

    internal const uint ProcessTerminate = 0x0001;

    // ---- Efficiency mode (EcoQoS) -------------------------------------

    /// <summary>PROCESS_INFORMATION_CLASS.ProcessPowerThrottling.</summary>
    internal const int ProcessPowerThrottling = 4;

    internal const uint PowerThrottlingCurrentVersion = 1;

    /// <summary>PROCESS_POWER_THROTTLING_EXECUTION_SPEED.</summary>
    internal const uint PowerThrottlingExecutionSpeed = 0x1;

    /// <summary>
    /// <c>PROCESS_POWER_THROTTLING_STATE</c>. Setting <see cref="ControlMask"/> and
    /// <see cref="StateMask"/> to the execution-speed bit is exactly what Task Manager's
    /// "Efficiency mode" does: the process is classified EcoQoS, so the scheduler prefers
    /// efficient cores and lower clocks for it.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(
        IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessInformation(
        IntPtr process, int informationClass, ref PowerThrottlingState information, uint size);

    [DllImport(Kernel32, SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    // ---- Processor group affinity --------------------------------------
    //
    // Above 64 logical processors Windows splits the machine into groups, and a bare 64-bit
    // mask stops being an address: bit 3 of group 0 and bit 3 of group 1 are different
    // processors. Every mask GamerGod journals therefore carries the group it was read from,
    // and every write checks that the group still matches before it lands.

    /// <summary>ERROR_INSUFFICIENT_BUFFER — the process belongs to more than one group.</summary>
    internal const int ErrorInsufficientBuffer = 122;

    /// <summary>ERROR_INVALID_PARAMETER — no process with that id exists.</summary>
    internal const int ErrorInvalidParameter = 87;

    /// <summary>STILL_ACTIVE, the exit code Windows reports for a running process.</summary>
    internal const uint StillActive = 259;

    /// <summary>
    /// Needs PROCESS_QUERY_LIMITED_INFORMATION and nothing more. Returns false with
    /// <see cref="ErrorInsufficientBuffer"/> when the process spans several groups, which is
    /// the case GamerGod refuses rather than guesses at.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessGroupAffinity(
        IntPtr process, ref ushort groupCount, [In, Out] ushort[] groupArray);

    /// <summary>
    /// The mask is relative to the group the process belongs to, which is why it is never
    /// read without <see cref="GetProcessGroupAffinity"/> alongside it.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessAffinityMask(
        IntPtr process, out UIntPtr processAffinityMask, out UIntPtr systemAffinityMask);

    /// <summary>
    /// Sets affinity within the process's existing group. There is deliberately no call here
    /// that moves a process between groups: doing so means writing every thread's group
    /// affinity individually, and a partial failure would leave a process we could not put
    /// back.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessAffinityMask(IntPtr process, UIntPtr processAffinityMask);

    // ---- Process liveness, for the watchdog ---------------------------
    //
    // Read-only, PROCESS_QUERY_LIMITED_INFORMATION only. Nothing here can start, stop,
    // change or read anything inside another process.

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    /// <summary>
    /// The creation time is what distinguishes a process from whatever later inherits its
    /// id. FILETIME values are passed as longs, which is what they are.
    /// </summary>
    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(
        IntPtr process, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

    // ---- Job objects --------------------------------------------------

    /// <summary>JOBOBJECT_BASIC_LIMIT_INFORMATION.LimitFlags: affinity limit.</summary>
    internal const uint JobObjectLimitAffinity = 0x00000010;

    /// <summary>JOBOBJECTINFOCLASS.JobObjectBasicLimitInformation.</summary>
    internal const int JobObjectBasicLimitInformation = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimits
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetInformationJobObject(
        IntPtr job, int informationClass, ref JobObjectBasicLimits information, uint length);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    // ---- Service control manager --------------------------------------

    internal const uint ScManagerConnect = 0x0001;
    internal const uint ServiceQueryStatus = 0x0004;
    internal const uint ServiceStart = 0x0010;
    internal const uint ServiceStop = 0x0020;
    internal const uint ServiceQueryConfig = 0x0001;

    internal const uint ServiceControlStop = 0x00000001;

    internal const uint ServiceStopped = 0x00000001;
    internal const uint ServiceRunning = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint access);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint access);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceStatus(IntPtr service, ref ServiceStatus status);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport(Advapi32, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceW(IntPtr service, uint argc, IntPtr argv);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    // ---- Power schemes -------------------------------------------------
    //
    // Duplicate and activate, never edit. The user's own plan is left untouched, so even a
    // failed revert leaves their settings intact - the worst case is an unused scheme in
    // their list rather than a modified one.

    [DllImport(PowrProf)]
    internal static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport(PowrProf)]
    internal static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport(PowrProf)]
    internal static extern uint PowerDuplicateScheme(
        IntPtr rootPowerKey, ref Guid sourceScheme, ref IntPtr destinationScheme);

    [DllImport(PowrProf)]
    internal static extern uint PowerDeleteScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport(PowrProf, CharSet = CharSet.Unicode)]
    internal static extern uint PowerWriteFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingsGuid,
        IntPtr powerSettingGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string buffer,
        uint bufferSize);

    [DllImport(Kernel32)]
    internal static extern IntPtr LocalFree(IntPtr memory);
}
