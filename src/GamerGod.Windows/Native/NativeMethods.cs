using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// GamerGod's entire native surface.
///
/// <para>
/// This file is the audit surface for Charter Articles II and III. Every Win32 entry point
/// GamerGod can reach is declared here and nowhere else, so a reviewer can verify in one
/// place that there is no injection primitive, no driver load, and no full-access process
/// handle anywhere in the project.
/// </para>
///
/// <para>
/// Permanently banned, and asserted by <c>BannedApiTests</c>: <c>WriteProcessMemory</c>,
/// <c>ReadProcessMemory</c>, <c>CreateRemoteThread</c>, <c>VirtualAllocEx</c>,
/// <c>SetWindowsHookEx</c> targeting another process, <c>NtLoadDriver</c>, and any handle
/// opened with <c>PROCESS_ALL_ACCESS</c>.
/// </para>
/// </summary>
/// <para>
/// Declared with classic <c>DllImport</c> rather than source-generated <c>LibraryImport</c>
/// so that the whole solution compiles with <c>AllowUnsafeBlocks=false</c>. "GamerGod
/// contains no unsafe code" is a property a reviewer can verify from the build settings in
/// seconds, and it is worth more here than the marginal marshalling efficiency.
/// </para>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    /// <summary>
    /// Retrieves the relationships between logical processors, caches, dies and packages.
    /// Read-only; the sole source of truth for <see cref="GamerGod.Core.Hardware.ProcessorSnapshot"/>.
    /// </summary>
    [DllImport(Kernel32, EntryPoint = "GetLogicalProcessorInformationEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLogicalProcessorInformationEx(
        LogicalProcessorRelationship relationshipType,
        IntPtr buffer,
        ref uint returnedLength);
}
