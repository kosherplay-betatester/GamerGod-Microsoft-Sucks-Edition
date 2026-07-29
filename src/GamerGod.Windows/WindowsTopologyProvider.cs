using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GamerGod.Core.Hardware;
using GamerGod.Windows.Native;
using Microsoft.Win32;

namespace GamerGod.Windows;

/// <summary>
/// Reads the real processor layout from <c>GetLogicalProcessorInformationEx</c>.
///
/// <para>
/// The returned buffer is a sequence of variable-length, union-shaped records, so it is
/// walked byte by byte using each record's own <c>Size</c> field rather than marshalled as
/// an array of fixed structs. Every offset used here is in <see cref="Offsets"/> with a
/// note on where it comes from.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsTopologyProvider : ITopologyProvider
{
    private const int ErrorInsufficientBuffer = 122;

    public ProcessorSnapshot Capture()
    {
        var buffer = QueryRelationships(LogicalProcessorRelationship.All);

        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        var caches = ImmutableArray.CreateBuilder<CacheInfo>();

        var offset = 0;
        while (offset < buffer.Length)
        {
            var relationship = (LogicalProcessorRelationship)BitConverter.ToUInt32(
                buffer, offset + Offsets.Relationship);
            var size = (int)BitConverter.ToUInt32(buffer, offset + Offsets.Size);

            if (size <= 0 || offset + size > buffer.Length)
            {
                // A malformed record length would otherwise spin or read out of bounds.
                // Stopping here yields a partial but self-consistent snapshot.
                break;
            }

            var payload = offset + Offsets.Payload;
            var payloadSize = size - Offsets.Payload;

            switch (relationship)
            {
                case LogicalProcessorRelationship.ProcessorCore:
                    ReadProcessorCore(buffer, payload, cores);
                    break;

                case LogicalProcessorRelationship.Cache:
                    ReadCache(buffer, payload, payloadSize, caches);
                    break;
            }

            offset += size;
        }

        if (cores.Count == 0)
        {
            throw new InvalidOperationException(
                "GetLogicalProcessorInformationEx returned no processor cores. " +
                "GamerGod cannot classify this machine's topology.");
        }

        return new ProcessorSnapshot
        {
            Cores = cores.ToImmutable(),
            Caches = caches.ToImmutable(),
            ProcessorName = ReadProcessorName(),
            MaxFrequencyMhz = ReadMaxFrequencyMhz(),
            PreferredCoreOrder = [],
        };
    }

    private static void ReadProcessorCore(
        byte[] buffer,
        int payload,
        ImmutableArray<PhysicalCore>.Builder cores)
    {
        var efficiencyClass = buffer[payload + Offsets.ProcessorEfficiencyClass];
        var groupCount = BitConverter.ToUInt16(buffer, payload + Offsets.ProcessorGroupCount);

        // A core is reported with one group mask per processor group it spans. Consumer
        // hardware is always a single group; the loop is here so a 96-core workstation
        // does not silently lose half its cores.
        var groups = groupCount < 1 ? 1 : (int)groupCount;
        for (var g = 0; g < groups; g++)
        {
            var maskOffset = payload + Offsets.ProcessorGroupMask + (g * Offsets.GroupAffinitySize);
            if (maskOffset + Offsets.GroupAffinitySize > buffer.Length)
            {
                break;
            }

            var mask = ReadGroupAffinity(buffer, maskOffset);
            if (!mask.IsEmpty)
            {
                cores.Add(new PhysicalCore(mask, efficiencyClass));
            }
        }
    }

    private static void ReadCache(
        byte[] buffer,
        int payload,
        int payloadSize,
        ImmutableArray<CacheInfo>.Builder caches)
    {
        var level = buffer[payload + Offsets.CacheLevel];
        var cacheSize = BitConverter.ToUInt32(buffer, payload + Offsets.CacheSize);

        // CACHE_RELATIONSHIP grew a GroupCount/GroupMasks union in Windows 10 21H1, which
        // moved the single-group mask from offset 32 to offset 16. Both layouts are still
        // produced in the field, so pick by the record's own declared size rather than
        // assuming a Windows version.
        var legacyLayout = payloadSize >= Offsets.CacheGroupMaskLegacy + Offsets.GroupAffinitySize;
        var maskOffset = payload + (legacyLayout
            ? Offsets.CacheGroupMaskLegacy
            : Offsets.CacheGroupMaskModern);

        if (maskOffset + Offsets.GroupAffinitySize > buffer.Length)
        {
            return;
        }

        var mask = ReadGroupAffinity(buffer, maskOffset);
        if (mask.IsEmpty && !legacyLayout)
        {
            return;
        }

        // A zero mask under the legacy guess means the guess was wrong; retry the other one
        // before discarding a cache record, because a missing L3 changes classification.
        if (mask.IsEmpty)
        {
            var alternate = payload + Offsets.CacheGroupMaskModern;
            if (alternate + Offsets.GroupAffinitySize <= buffer.Length)
            {
                mask = ReadGroupAffinity(buffer, alternate);
            }
        }

        if (!mask.IsEmpty)
        {
            caches.Add(new CacheInfo(level, cacheSize, mask));
        }
    }

    private static ProcessorMask ReadGroupAffinity(byte[] buffer, int offset)
    {
        var mask = BitConverter.ToUInt64(buffer, offset);
        var group = BitConverter.ToUInt16(buffer, offset + sizeof(ulong));
        return new ProcessorMask(group, mask);
    }

    private static byte[] QueryRelationships(LogicalProcessorRelationship relationship)
    {
        uint length = 0;
        if (NativeMethods.GetLogicalProcessorInformationEx(relationship, IntPtr.Zero, ref length))
        {
            return [];
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorInsufficientBuffer)
        {
            throw new Win32Exception(error, "GetLogicalProcessorInformationEx failed to size its buffer.");
        }

        var native = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!NativeMethods.GetLogicalProcessorInformationEx(relationship, native, ref length))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "GetLogicalProcessorInformationEx failed.");
            }

            var managed = new byte[length];
            Marshal.Copy(native, managed, 0, (int)length);
            return managed;
        }
        finally
        {
            Marshal.FreeHGlobal(native);
        }
    }

    private static string ReadProcessorName()
    {
        const string key = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        using var cpu = Registry.LocalMachine.OpenSubKey(key);
        var name = cpu?.GetValue("ProcessorNameString") as string;
        return string.IsNullOrWhiteSpace(name) ? "Unknown Processor" : name.Trim();
    }

    private static int ReadMaxFrequencyMhz()
    {
        const string key = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        using var cpu = Registry.LocalMachine.OpenSubKey(key);
        return cpu?.GetValue("~MHz") is int mhz ? mhz : 0;
    }
}
