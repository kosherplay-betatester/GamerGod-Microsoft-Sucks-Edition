using System.Collections.Immutable;

namespace GodMode.Core.Diagnostics;

public enum HazardSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public enum HazardCategory
{
    Driver,
    Display,
    Virtualization,
    Security,
    Network,
    Software,
    Recovery,
    Hardware,
}

/// <param name="Id">Stable identifier, so a user can suppress one finding without silencing the rest.</param>
/// <param name="Remedy">What to actually do. A finding without an action is just anxiety.</param>
public sealed record Hazard(
    string Id,
    HazardSeverity Severity,
    HazardCategory Category,
    string Title,
    string Detail,
    string? Remedy = null);

/// <summary>
/// Read-only diagnostic that runs on first launch and on demand.
///
/// <para>
/// This is the highest value-per-line feature in GodMode and it changes nothing. On the
/// reference machine it found a display driver in an error state, a kernel driver from
/// Microsoft's vulnerable-driver blocklist, and two coexisting hypervisors — inside ten
/// seconds, before a single optimisation had been applied.
/// </para>
///
/// <para>
/// Telling somebody their display driver is broken is worth more than two percent more
/// frames, so this leads the product rather than hiding in a diagnostics tab.
/// </para>
/// </summary>
public static class HazardScanner
{
    /// <summary>
    /// Kernel drivers that are widely abused by attackers because they expose privileged
    /// primitives to user mode. Several appear on Microsoft's vulnerable-driver blocklist.
    ///
    /// <para>
    /// These are not malware, and their parent applications are legitimate and popular.
    /// They matter to GodMode for two practical reasons: Memory Integrity refuses to load
    /// them, and some anti-cheat products decline to run or raise a flag while one is
    /// resident. A user seeing a launch failure deserves to know this before blaming us.
    /// </para>
    /// </summary>
    private static readonly ImmutableDictionary<string, string> KnownAbusableDrivers =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rtcore64.sys"] = "MSI Afterburner / RivaTuner",
            ["winring0x64.sys"] = "OpenHardwareMonitor and similar",
            ["winring0.sys"] = "OpenHardwareMonitor and similar",
            ["gdrv.sys"] = "Gigabyte utilities",
            ["gdrv2.sys"] = "Gigabyte utilities",
            ["asrdrv101.sys"] = "ASRock utilities",
            ["asrdrv102.sys"] = "ASRock utilities",
            ["iqvw64e.sys"] = "Intel network diagnostics",
            ["dbutil_2_3.sys"] = "Dell firmware utility",
            ["speedfan.sys"] = "SpeedFan",
            ["xhunter1.sys"] = "XIGNCODE3 anti-cheat",
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly ImmutableArray<string> HypervisorServices =
        ["vmms", "vmcompute", "VMwareHostd", "VMAuthdService", "VMwareAutostartService",
         "vboxdrv", "VBoxSDS", "prl_tools"];

    private static readonly ImmutableArray<string> VirtualDisplayMarkers =
        ["virtual desktop", "parsec", "usbmmidd", "spacedesk", "idd", "virtual display",
         "amyuni", "sunshine"];

    public static ImmutableArray<Hazard> Scan(MachineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var hazards = ImmutableArray.CreateBuilder<Hazard>();

        ScanFailedDevices(snapshot, hazards);
        ScanVirtualDisplays(snapshot, hazards);
        ScanAbusableDrivers(snapshot, hazards);
        ScanHypervisors(snapshot, hazards);
        ScanSecurityPosture(snapshot, hazards);
        ScanNetwork(snapshot, hazards);
        ScanRecovery(snapshot, hazards);
        ScanCapacity(snapshot, hazards);

        return hazards
            .OrderByDescending(h => h.Severity)
            .ThenBy(h => h.Category)
            .ThenBy(h => h.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    /// <summary>
    /// A device the user switched off is not a fault.
    ///
    /// <para>
    /// Device Manager problem code 22 means somebody chose to disable this device, and
    /// vendors ship several components disabled by default. Reporting that as a failure
    /// would fill a healthy machine's first-run report with red text, and a diagnostic
    /// nobody believes is worse than none.
    /// </para>
    /// </summary>
    private static bool IsIntentionallyOff(DeviceEntry device) =>
        device.Status.Contains("Disabled", StringComparison.OrdinalIgnoreCase);

    private static void ScanFailedDevices(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        // Device names are not unique — a machine typically has several devices called
        // "USB Input Device". Reporting each separately would produce colliding ids, so a
        // user suppressing one finding would silently suppress the others too. Group them
        // instead, which also reads better.
        var failures = s.Devices
            .Where(d => !string.Equals(d.Status, "OK", StringComparison.OrdinalIgnoreCase)
                        && !IsIntentionallyOff(d))
            .GroupBy(d => (d.Name, d.Class, d.Status));

        foreach (var group in failures)
        {
            var (name, deviceClass, status) = group.Key;
            var count = group.Count();

            // A display or audio device that failed to start is a first-order stutter
            // source, well ahead of anything GodMode could tune.
            var severe = deviceClass.Contains("Display", StringComparison.OrdinalIgnoreCase)
                         || deviceClass.Contains("Media", StringComparison.OrdinalIgnoreCase)
                         || deviceClass.Contains("Video", StringComparison.OrdinalIgnoreCase);

            var subject = count > 1 ? $"{count} × {name} are not working" : $"{name} is not working";

            h.Add(new Hazard(
                $"device.error.{Slug(deviceClass)}.{Slug(name)}.{StableHash(name, deviceClass, status)}",
                severe ? HazardSeverity.High : HazardSeverity.Medium,
                severe ? HazardCategory.Display : HazardCategory.Driver,
                subject,
                $"Windows reports this {deviceClass} device in a '{status}' state. A "
                + "display or audio device that fails to initialise can stall the desktop "
                + "compositor and cause hitches no amount of tuning will fix.",
                "Reinstall or remove the device in Device Manager. If you no longer use the "
                + "software that installed it, uninstalling that software is the cleanest fix."));
        }
    }

    private static void ScanVirtualDisplays(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        // Only adapters that are actually live can affect composition. A disabled one is
        // already out of the way and does not belong in the count.
        var virtualDisplays = s.Devices
            .Where(d => d.Class.Contains("Display", StringComparison.OrdinalIgnoreCase)
                        || d.Class.Contains("Monitor", StringComparison.OrdinalIgnoreCase))
            .Where(d => !IsIntentionallyOff(d))
            .Where(d => VirtualDisplayMarkers.Any(m =>
                d.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        if (virtualDisplays.Length >= 2)
        {
            h.Add(new Hazard(
                "display.virtual.multiple",
                HazardSeverity.Medium,
                HazardCategory.Display,
                $"{virtualDisplays.Length} virtual display adapters are installed",
                "Each virtual display takes part in display enumeration and desktop "
                + "composition. Several at once is a documented cause of variable-refresh "
                + "misbehaviour and slow mode changes: "
                + string.Join(", ", virtualDisplays.Select(d => d.Name)) + ".",
                "Keep the one you actually use for remote play or VR and remove the rest."));
        }
    }

    private static void ScanAbusableDrivers(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        foreach (var driver in s.KernelDrivers)
        {
            var file = SafeFileName(driver.ImagePath);
            if (file is null || !KnownAbusableDrivers.TryGetValue(file, out var owner))
            {
                continue;
            }

            h.Add(new Hazard(
                $"driver.abusable.{Slug(file)}",
                driver.Running ? HazardSeverity.High : HazardSeverity.Medium,
                HazardCategory.Driver,
                $"{file} is present ({owner})",
                "This kernel driver is widely abused because it exposes privileged access to "
                + "ordinary programs, and several drivers of this kind appear on Microsoft's "
                + "vulnerable driver blocklist. The software itself is legitimate. What "
                + "matters here is that Memory Integrity will refuse to load it, and some "
                + "anti-cheat products decline to run or raise a flag while it is resident. "
                + "If a protected game fails to launch, start here.",
                $"Close {owner} before playing a title with kernel anti-cheat, or leave it "
                + "running and accept that Memory Integrity cannot be enabled."));
        }
    }

    private static void ScanHypervisors(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        var present = HypervisorServices
            .Where(name => s.InstalledServices.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var thirdParty = present.Where(p =>
            !p.StartsWith("vmms", StringComparison.OrdinalIgnoreCase)
            && !p.StartsWith("vmcompute", StringComparison.OrdinalIgnoreCase)).ToArray();

        if (thirdParty.Length > 0 && s.VirtualizationBasedSecurityRunning)
        {
            h.Add(new Hazard(
                "virt.coexisting",
                HazardSeverity.Medium,
                HazardCategory.Virtualization,
                "Two hypervisor stacks are active at once",
                "Windows virtualisation-based security is running, and third-party "
                + $"virtualisation is also installed ({string.Join(", ", thirdParty)}). Kernel "
                + "anti-cheat products are documented to conflict with third-party "
                + "hypervisors, and this is a common cause of a protected game refusing to "
                + "start — one GodMode did not cause and cannot fix.",
                "If a protected title will not launch, stop the third-party virtualisation "
                + "service and try again before changing anything else."));
        }
    }

    private static void ScanSecurityPosture(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        // The framing matters. Every tuning guide tells people to disable these for frames.
        // Doing so does not slow those games down, it stops them launching, so GodMode's job
        // is to report the state and say which titles depend on it.
        if (!s.SecureBootEnabled || !s.TpmReady)
        {
            var missing = !s.SecureBootEnabled && !s.TpmReady
                ? "Secure Boot and TPM 2.0 are"
                : !s.SecureBootEnabled ? "Secure Boot is" : "TPM 2.0 is";

            h.Add(new Hazard(
                "security.platform.missing",
                HazardSeverity.High,
                HazardCategory.Security,
                $"{missing} not available — some games will refuse to launch",
                "Battlefield 6, Call of Duty and other titles with kernel anti-cheat require "
                + "Secure Boot and a ready TPM 2.0 before they will start at all. This is a "
                + "launch requirement, not a performance setting.",
                "Enable Secure Boot and TPM 2.0 in firmware. GodMode will never turn these "
                + "off, whatever a tuning guide tells you."));
        }
        else
        {
            h.Add(new Hazard(
                "security.platform.ok",
                HazardSeverity.Info,
                HazardCategory.Security,
                "Secure Boot and TPM 2.0 are ready",
                "Titles with kernel anti-cheat, including Battlefield 6, will launch on this "
                + "machine. GodMode will keep it that way — it never disables platform "
                + "security for performance.",
                null));
        }

        if (s.VirtualizationBasedSecurityRunning && !s.MemoryIntegrityRunning)
        {
            h.Add(new Hazard(
                "security.hvci.off",
                HazardSeverity.Low,
                HazardCategory.Security,
                "Memory Integrity is off",
                "Virtualisation-based security is running but Memory Integrity is not. Games "
                + "only require the capability, so nothing will fail to launch. Turning it on "
                + "costs a small amount of performance and blocks the abusable drivers listed "
                + "above from loading.",
                "Your call. GodMode reports it and changes nothing either way."));
        }
    }

    private static void ScanNetwork(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        var live = s.NetworkAdapters.Where(a => a.IsUp && !a.IsVirtual).ToArray();

        if (live.Length > 0 && live.All(a => a.IsWireless))
        {
            h.Add(new Hazard(
                "network.wireless.only",
                HazardSeverity.Medium,
                HazardCategory.Network,
                "Wireless is the only active connection",
                "For competitive multiplayer, a wired connection is worth more than every "
                + "software lever in this project combined. Wireless adds latency variance "
                + "that no amount of scheduling can remove — GodMode can quiet the machine "
                + "around your game, but it cannot quiet the radio.",
                "If you play multiplayer seriously, run an Ethernet cable. It is the single "
                + "highest-value change available on this machine."));
        }

        var virtualNics = s.NetworkAdapters.Count(a => a.IsVirtual && a.IsUp);
        if (virtualNics >= 3)
        {
            h.Add(new Hazard(
                "network.virtual.many",
                HazardSeverity.Low,
                HazardCategory.Network,
                $"{virtualNics} virtual network adapters are active",
                "Virtual adapters from virtualisation software and VPN tunnels take part in "
                + "network interface selection and can confuse a game's choice of route.",
                "Disable the ones you are not currently using."));
        }
    }

    private static void ScanRecovery(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        if (!s.SystemRestoreEnabled)
        {
            h.Add(new Hazard(
                "recovery.restore.off",
                HazardSeverity.Medium,
                HazardCategory.Recovery,
                "System Protection is turned off",
                "No restore points can be created. GodMode's journal still guarantees that "
                + "everything it does is undone on exit or at next boot, so the core promise "
                + "holds — but the extra safety net for changes that outlive a reboot is "
                + "missing, and GodMode will decline to make those changes without it.",
                "Turn System Protection on for the system drive in System Properties."));
        }
    }

    private static void ScanCapacity(MachineSnapshot s, ImmutableArray<Hazard>.Builder h)
    {
        if (s.PhysicalMemoryGigabytes is > 0 and <= 16)
        {
            h.Add(new Hazard(
                "hardware.memory.tight",
                HazardSeverity.Info,
                HazardCategory.Hardware,
                $"{s.PhysicalMemoryGigabytes} GB of memory — GodMode helps more here",
                "On a memory-constrained machine, evicting background work and trimming "
                + "working sets before a game loads prevents the paging stalls that cause the "
                + "worst hitches. Expect a larger benefit than a high-end system would see.",
                null));
        }

        if (s.PerformanceDomainCount <= 1)
        {
            h.Add(new Hazard(
                "hardware.single.domain",
                HazardSeverity.Info,
                HazardCategory.Hardware,
                "This CPU has a single performance domain",
                "There is nowhere to evict background work to, so GodMode will not partition "
                + "this processor. Every other lever still applies: efficiency-mode demotion, "
                + "service suppression, interrupt steering and power. GodMode would rather say "
                + "this plainly than pretend to partition a CPU that cannot be partitioned.",
                null));
        }
    }

    private static string? SafeFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Driver image paths arrive in forms like \??\C:\Windows\x.sys and
        // \SystemRoot\System32\drivers\x.sys, so take the trailing segment directly rather
        // than asking Path to parse a value it may consider malformed.
        var trimmed = path.Trim().Trim('"');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        var name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;

        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// A short, deterministic discriminator so two devices that slug identically still get
    /// distinct finding ids.
    ///
    /// <para>
    /// FNV-1a rather than <see cref="string.GetHashCode()"/>, which .NET randomises per
    /// process. A per-run identifier would silently break suppressions: a user hides a
    /// finding, restarts, and it comes back under a new id.
    /// </para>
    /// </summary>
    private static string StableHash(params string[] parts)
    {
        const uint Offset = 2166136261;
        const uint Prime = 16777619;

        var hash = Offset;

        foreach (var part in parts)
        {
            foreach (var c in part)
            {
                hash ^= c;
                hash *= Prime;
            }

            hash ^= 0x1F; // separator, so ("ab","c") and ("a","bc") hash differently
            hash *= Prime;
        }

        return hash.ToString("x8");
    }

    private static string Slug(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        return new string(chars).Trim('-');
    }
}
