using System.Runtime.InteropServices;

namespace GamerGod.Windows.Native;

/// <summary>
/// <c>LOGICAL_PROCESSOR_RELATIONSHIP</c>.
/// </summary>
internal enum LogicalProcessorRelationship : uint
{
    ProcessorCore = 0,
    NumaNode = 1,
    Cache = 2,
    ProcessorPackage = 3,
    Group = 4,
    ProcessorDie = 5,
    NumaNodeEx = 6,
    ProcessorModule = 7,
    All = 0xffff,
}

/// <summary>
/// <c>GROUP_AFFINITY</c>. 16 bytes on x64: an 8-byte KAFFINITY, a 2-byte group index,
/// and three reserved words.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GroupAffinity
{
    public nuint Mask;
    public ushort Group;
    public ushort Reserved0;
    public ushort Reserved1;
    public ushort Reserved2;
}

/// <summary>
/// Field offsets for walking a <c>SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX</c> buffer.
///
/// <para>
/// The structure is a discriminated union whose entries are variable-length, so it cannot
/// be marshalled as a fixed struct — it must be walked using each entry's own
/// <c>Size</c> field. Offsets are taken directly from <c>winnt.h</c>.
/// </para>
///
/// <para>
/// <c>CACHE_RELATIONSHIP</c> is the awkward one. Its trailing 20 reserved bytes were
/// redefined as a union in Windows 10 21H1 to carry a <c>GroupCount</c> plus an array of
/// group masks, which moved the single-group mask from offset 32 to offset 16. Both
/// layouts remain in the wild, so <see cref="LogicalProcessorInformation"/> distinguishes
/// them by the entry's declared size rather than assuming either.
/// </para>
/// </summary>
internal static class Offsets
{
    // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX header
    public const int Relationship = 0;
    public const int Size = 4;
    public const int Payload = 8;

    // PROCESSOR_RELATIONSHIP, relative to Payload
    public const int ProcessorFlags = 0;
    public const int ProcessorEfficiencyClass = 1;
    public const int ProcessorGroupCount = 22;
    public const int ProcessorGroupMask = 24;

    // CACHE_RELATIONSHIP, relative to Payload
    public const int CacheLevel = 0;
    public const int CacheAssociativity = 1;
    public const int CacheLineSize = 2;
    public const int CacheSize = 4;
    public const int CacheType = 8;
    public const int CacheGroupCount = 12;

    /// <summary>Group mask offset when the modern union layout is in use.</summary>
    public const int CacheGroupMaskModern = 16;

    /// <summary>Group mask offset under the original <c>Reserved[20]</c> layout.</summary>
    public const int CacheGroupMaskLegacy = 32;

    public const int GroupAffinitySize = 16;

    /// <summary><c>LTP_PC_SMT</c> — this core has more than one logical processor.</summary>
    public const byte FlagSmt = 0x1;
}
