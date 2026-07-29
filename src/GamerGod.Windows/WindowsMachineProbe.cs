using System.Collections.Immutable;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using GamerGod.Core.Diagnostics;
using GamerGod.Core.Hardware;
using GamerGod.Windows.Native;
using Microsoft.Win32;

namespace GamerGod.Windows;

/// <summary>
/// Collects the read-only facts the hazard scan reasons over.
///
/// <para>
/// Every query here observes and nothing writes, which is what lets the scan run on first
/// launch before the user has agreed to anything. Sources are SetupAPI, the registry and
/// the BCL — deliberately not WMI, which is neither trim- nor AOT-compatible and would
/// silently return nothing in a NativeAOT build.
/// </para>
///
/// <para>
/// Individual probes fail soft. A machine where one source is unavailable still produces a
/// useful report rather than an error.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMachineProbe
{
    public MachineSnapshot Capture() => new()
    {
        Devices = Probe(ReadDevices),
        KernelDrivers = Probe(ReadKernelDrivers),
        NetworkAdapters = Probe(ReadNetworkAdapters),
        InstalledServices = Probe(ReadServiceNames),
        GpuNames = Probe(ReadGpuNames),
        SecureBootEnabled = Try(ReadSecureBoot),
        TpmReady = Try(SystemFacts.TpmReady),
        VirtualizationBasedSecurityRunning = Try(ReadVbsEnabled),
        MemoryIntegrityRunning = Try(ReadMemoryIntegrityEnabled),
        SystemRestoreEnabled = Try(ReadSystemRestoreEnabled),
        PhysicalMemoryGigabytes = SystemFacts.PhysicalMemoryGigabytes(),
        PerformanceDomainCount = SafeDomainCount(),
    };

    private static ImmutableArray<T> Probe<T>(Func<ImmutableArray<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool Try(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int SafeDomainCount()
    {
        try
        {
            return new WindowsTopologyProvider().Classify().Domains.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static ImmutableArray<DeviceEntry> ReadDevices() =>
        DeviceEnumeration.Enumerate()
            .Select(d => new DeviceEntry(d.Description, d.Class, d.Status, d.DriverVersion))
            .ToImmutableArray();

    private static ImmutableArray<KernelDriverEntry> ReadKernelDrivers()
    {
        // The registry is authoritative and, unlike a running-drivers query, includes
        // drivers registered but not currently loaded — exactly the case that matters for a
        // driver an anti-cheat will object to the moment it starts.
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null)
        {
            return [];
        }

        var drivers = ImmutableArray.CreateBuilder<KernelDriverEntry>();

        foreach (var name in services.GetSubKeyNames())
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("Type") is not int type || type is not (1 or 2))
            {
                continue;
            }

            if (key.GetValue("ImagePath") is not string image || string.IsNullOrWhiteSpace(image))
            {
                continue;
            }

            // Start = 4 means disabled; anything else can load. Treating "not disabled and
            // demand-started" as running would overstate severity, so only boot, system and
            // auto start are reported as resident.
            var start = key.GetValue("Start") as int? ?? 3;
            drivers.Add(new KernelDriverEntry(name, image, start is 0 or 1 or 2));
        }

        return drivers.ToImmutable();
    }

    private static ImmutableArray<NetworkAdapterEntry> ReadNetworkAdapters()
    {
        var adapters = ImmutableArray.CreateBuilder<NetworkAdapterEntry>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var description = nic.Description;

            var isWireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                             || description.Contains("wi-fi", StringComparison.OrdinalIgnoreCase)
                             || description.Contains("wireless", StringComparison.OrdinalIgnoreCase)
                             || description.Contains("802.11", StringComparison.OrdinalIgnoreCase);

            var isVirtual = nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
                            || description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("tap-", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("virtualbox", StringComparison.OrdinalIgnoreCase);

            adapters.Add(new NetworkAdapterEntry(
                nic.Name,
                description,
                nic.OperationalStatus == OperationalStatus.Up,
                isWireless,
                isVirtual,
                nic.Speed));
        }

        return adapters.ToImmutable();
    }

    private static ImmutableArray<string> ReadServiceNames()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        return key is null ? [] : [.. key.GetSubKeyNames()];
    }

    private static ImmutableArray<string> ReadGpuNames()
    {
        // The display adapter class key. Each numbered subkey is one adapter.
        const string DisplayClass =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

        using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null)
        {
            return [];
        }

        var names = ImmutableArray.CreateBuilder<string>();

        foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var key = root.OpenSubKey(sub);
            if (key?.GetValue("DriverDesc") is string desc && !string.IsNullOrWhiteSpace(desc))
            {
                names.Add(desc);
            }
        }

        return names.ToImmutable();
    }

    private static bool ReadSecureBoot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return key?.GetValue("UEFISecureBootEnabled") is int v && v == 1;
    }

    private static bool ReadVbsEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard");

        // Either explicitly enabled by policy, or launched by the platform default.
        return key?.GetValue("EnableVirtualizationBasedSecurity") is int enabled && enabled == 1
               || key?.GetValue("RequirePlatformSecurityFeatures") is int req && req > 0;
    }

    private static bool ReadMemoryIntegrityEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
        return key?.GetValue("Enabled") is int v && v == 1;
    }

    private static bool ReadSystemRestoreEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");

        if (key is null)
        {
            return false;
        }

        if (key.GetValue("DisableSR") is int disabled && disabled == 1)
        {
            return false;
        }

        // Protection is only real if the system volume has shadow storage configured.
        using var config = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients");

        return config is not null || key.GetValue("RPSessionInterval") is not null;
    }
}
