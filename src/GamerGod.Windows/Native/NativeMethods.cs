using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GamerGod.Windows.Native;

/// <summary>
/// Processor topology, and only that.
///
/// <para>
/// <b>This is not the whole native surface, and this comment used to say it was.</b> It claimed
/// every Win32 entry point GamerGod can reach was declared here and nowhere else, and told a
/// reviewer auditing Charter Articles II and III to read this one file. This file declares two
/// imports. There are roughly thirty-five more — process handles and CPU Sets in
/// <c>AmbientNativeMethods.cs</c>, device enumeration in <c>DeviceEnumeration.cs</c>, hotkeys
/// and window styles in <c>OverlayNativeMethods.cs</c>, icon extraction in
/// <c>IconExtractor.cs</c>. An auditor who followed the instruction saw one read-only call and
/// signed off on a surface that includes <c>OpenProcess</c> and service control.
/// </para>
///
/// <para>
/// The real audit surface is the <b>project</b>: <c>GamerGod.Windows</c> is the only one
/// permitted to P/Invoke at all, and
/// <c>CharterComplianceTests.The_native_surface_is_confined_to_one_project</c> enforces that by
/// scanning every file under <c>src/</c>. Read the folder, not this file.
/// </para>
///
/// <para>
/// The banned list is asserted by <c>CharterComplianceTests.No_production_source_uses_a_banned_api</c>
/// — this comment previously cited <c>BannedApiTests</c>, which does not exist and never did,
/// so the pointer a reviewer was told to follow led nowhere.
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
