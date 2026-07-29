using System.Collections.Immutable;

namespace GamerGod.Core.Diagnostics;

/// <param name="Status">Device manager status. "OK" is healthy; anything else is not.</param>
public sealed record DeviceEntry(string Name, string Class, string Status, string? DriverVersion = null);

/// <param name="ImagePath">Where the driver binary lives. Used to spot unusual locations.</param>
public sealed record KernelDriverEntry(string Name, string ImagePath, bool Running);

public sealed record NetworkAdapterEntry(
    string Name,
    string Description,
    bool IsUp,
    bool IsWireless,
    bool IsVirtual,
    long LinkSpeedBitsPerSecond);

/// <summary>
/// Everything the hazard scan is allowed to look at. Read-only by construction: the scan
/// changes nothing, which is why it can run on first launch before the user has agreed to
/// anything at all.
/// </summary>
public sealed record MachineSnapshot
{
    public ImmutableArray<DeviceEntry> Devices { get; init; } = [];

    public ImmutableArray<KernelDriverEntry> KernelDrivers { get; init; } = [];

    public ImmutableArray<NetworkAdapterEntry> NetworkAdapters { get; init; } = [];

    public ImmutableArray<string> InstalledServices { get; init; } = [];

    public ImmutableArray<string> GpuNames { get; init; } = [];

    public bool SecureBootEnabled { get; init; }

    public bool TpmReady { get; init; }

    public bool VirtualizationBasedSecurityRunning { get; init; }

    public bool MemoryIntegrityRunning { get; init; }

    public bool SystemRestoreEnabled { get; init; }

    public int PhysicalMemoryGigabytes { get; init; }

    /// <summary>Performance domains found by the classifier. One means no partitioning.</summary>
    public int PerformanceDomainCount { get; init; }
}
