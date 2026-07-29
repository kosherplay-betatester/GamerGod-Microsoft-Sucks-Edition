using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// Device tree enumeration via SetupAPI — the same source Device Manager reads.
///
/// <para>
/// This replaces what would conventionally be a WMI query. WMI is convenient but it is not
/// trim- or AOT-compatible, and GamerGod's service and watchdog publish as NativeAOT so they
/// start instantly and carry no runtime dependency. A diagnostic that worked in development
/// and silently returned nothing in the shipped build would be worse than no diagnostic.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DeviceEnumeration
{
    private const string SetupApi = "setupapi.dll";
    private const string CfgMgr = "cfgmgr32.dll";

    private const int DigcfPresent = 0x00000002;
    private const int DigcfAllClasses = 0x00000004;

    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpClass = 0x00000007;
    private const uint SpdrpFriendlyName = 0x0000000C;
    private const uint SpdrpDriverVersion = 0x00000009;

    /// <summary>DN_HAS_PROBLEM — the device node reported a problem code.</summary>
    private const uint DnHasProblem = 0x00000400;

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(
        IntPtr classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport(SetupApi, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport(SetupApi, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport(CfgMgr)]
    private static extern int CM_Get_DevNode_Status(
        out uint status, out uint problemNumber, uint devInst, uint flags);

    internal readonly record struct RawDevice(
        string Description, string Class, string? DriverVersion, uint ProblemCode)
    {
        public bool IsHealthy => ProblemCode == 0;

        /// <summary>
        /// Device Manager's problem code, rendered the way a user would recognise it.
        /// Code 43 is the familiar "Windows has stopped this device".
        /// </summary>
        public string Status => ProblemCode switch
        {
            0 => "OK",
            10 => "Cannot start",
            12 => "Insufficient resources",
            18 => "Reinstall required",
            22 => "Disabled",
            28 => "Drivers not installed",
            31 => "Driver failed to load",
            43 => "Stopped by Windows (code 43)",
            _ => $"Problem code {ProblemCode}",
        };
    }

    public static List<RawDevice> Enumerate()
    {
        var results = new List<RawDevice>();

        var set = SetupDiGetClassDevsW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
            DigcfPresent | DigcfAllClasses);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            return results;
        }

        try
        {
            var info = new SpDevInfoData { CbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; ; index++)
            {
                if (!SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                    {
                        break;
                    }

                    continue;
                }

                var deviceClass = ReadProperty(set, ref info, SpdrpClass) ?? "Unknown";

                // Only the device families that can plausibly affect frame delivery.
                if (!IsRelevant(deviceClass))
                {
                    continue;
                }

                var description = ReadProperty(set, ref info, SpdrpFriendlyName)
                                  ?? ReadProperty(set, ref info, SpdrpDeviceDesc);

                if (string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                uint problem = 0;
                if (CM_Get_DevNode_Status(out var status, out var problemNumber, info.DevInst, 0) == 0
                    && (status & DnHasProblem) != 0)
                {
                    problem = problemNumber;
                }

                results.Add(new RawDevice(
                    description,
                    deviceClass,
                    ReadProperty(set, ref info, SpdrpDriverVersion),
                    problem));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return results;
    }

    private static bool IsRelevant(string deviceClass) => deviceClass switch
    {
        "Display" or "Monitor" or "Media" or "AudioEndpoint" or "MEDIA" => true,
        "HIDClass" or "XnaComposite" or "XboxComposite" => true,
        "Net" or "Bluetooth" or "USB" => true,
        "System" or "SCSIAdapter" => true,
        _ => false,
    };

    private static string? ReadProperty(IntPtr set, ref SpDevInfoData info, uint property)
    {
        if (SetupDiGetDeviceRegistryPropertyW(
                set, ref info, property, out _, null, 0, out var size))
        {
            return null;
        }

        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || size == 0)
        {
            return null;
        }

        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(
                set, ref info, property, out _, buffer, size, out _))
        {
            return null;
        }

        var text = System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

/// <summary>Machine facts that have no managed equivalent.</summary>
[SupportedOSPlatform("windows")]
internal static class SystemFacts
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, IntPtr info);

    public static int PhysicalMemoryGigabytes()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? (int)Math.Round(status.TotalPhys / (1024.0 * 1024.0 * 1024.0))
            : 0;
    }

    /// <summary>
    /// A TPM 2.0 is present and usable.
    ///
    /// <para>
    /// Asked through TPM Base Services rather than read from the registry, because what
    /// matters is whether the device actually answers — which is also what an anti-cheat's
    /// platform check will find.
    /// </para>
    ///
    /// <para>
    /// The reported version is checked, not just the call's success. A TPM 1.2 or a
    /// software-emulated TPM answers happily, and reporting it as "TPM 2.0 ready" would tell
    /// a user their machine can run titles that will in fact refuse to launch — precisely the
    /// wrong way for this check to be wrong.
    /// </para>
    /// </summary>
    public static bool TpmReady()
    {
        // TPM_DEVICE_INFO: structVersion, tpmVersion, tpmInterfaceType, tpmImpRevision.
        const int Size = 16;
        const int TpmVersionOffset = 4;
        const uint TpmVersion20 = 2;

        var buffer = Marshal.AllocHGlobal(Size);
        try
        {
            if (Tbsi_GetDeviceInfo(Size, buffer) != 0)
            {
                return false;
            }

            var version = (uint)Marshal.ReadInt32(buffer, TpmVersionOffset);
            return version >= TpmVersion20;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
