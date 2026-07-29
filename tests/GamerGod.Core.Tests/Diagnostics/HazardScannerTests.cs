using GamerGod.Core.Diagnostics;
using Xunit;

namespace GamerGod.Core.Tests.Diagnostics;

public sealed class HazardScannerTests
{
    /// <summary>
    /// The reference machine, captured verbatim on 2026-07-29. Every entry below was
    /// actually present: a display driver in an error state, four virtual display adapters,
    /// MSI Afterburner's RTCore64 and XIGNCODE3's xhunter1, VMware alongside running VBS,
    /// and no wired network at all.
    /// </summary>
    private static MachineSnapshot ReferenceMachine() => new()
    {
        Devices =
        [
            new DeviceEntry("Virtual Desktop Monitor", "Display", "Error", "13.50.53.699"),
            new DeviceEntry("Parsec Virtual Display Adapter", "Display", "OK", "0.45.0.0"),
            new DeviceEntry("NVIDIA GeForce RTX 5070 Ti", "Display", "OK", "32.0.16.1088"),
            new DeviceEntry("AMD Radeon(TM) Graphics", "Display", "OK", "32.0.21045.1000"),
            new DeviceEntry("Realtek High Definition Audio", "Media", "OK"),
            new DeviceEntry("Virtual Desktop Audio", "Media", "OK"),
        ],
        KernelDrivers =
        [
            new KernelDriverEntry("RTCore64", @"\??\C:\Program Files (x86)\MSI Afterburner\RTCore64.sys", true),
            new KernelDriverEntry("xhunter1", @"\??\C:\Windows\xhunter1.sys", false),
            new KernelDriverEntry("amd3dvcache", @"\SystemRoot\System32\drivers\amd3dvcache.sys", true),
        ],
        NetworkAdapters =
        [
            new NetworkAdapterEntry("Wi-Fi", "Realtek 8852BE WiFi 6 PCI-E NIC", true, true, false, 573_500_000),
            new NetworkAdapterEntry("VMnet8", "VMware Virtual Ethernet Adapter", true, false, true, 100_000_000),
            new NetworkAdapterEntry("VMnet1", "VMware Virtual Ethernet Adapter", true, false, true, 100_000_000),
            new NetworkAdapterEntry("Tailscale", "Tailscale Tunnel", true, false, true, 100_000_000_000),
        ],
        InstalledServices = ["VMwareAutostartService", "vmms", "EAAntiCheatService", "WSearch"],
        GpuNames = ["NVIDIA GeForce RTX 5070 Ti", "AMD Radeon(TM) Graphics"],
        SecureBootEnabled = true,
        TpmReady = true,
        VirtualizationBasedSecurityRunning = true,
        MemoryIntegrityRunning = false,
        SystemRestoreEnabled = true,
        PhysicalMemoryGigabytes = 64,
        PerformanceDomainCount = 2,
    };

    private static MachineSnapshot CleanMachine() => new()
    {
        Devices = [new DeviceEntry("NVIDIA GeForce RTX 4070", "Display", "OK")],
        NetworkAdapters =
        [
            new NetworkAdapterEntry("Ethernet", "Intel I225-V", true, false, false, 2_500_000_000),
        ],
        SecureBootEnabled = true,
        TpmReady = true,
        SystemRestoreEnabled = true,
        PhysicalMemoryGigabytes = 32,
        PerformanceDomainCount = 2,
    };

    // ------------------------------------------------------------------
    // Findings from the real machine.
    // ------------------------------------------------------------------

    [Fact]
    public void The_broken_display_driver_is_the_top_finding()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var top = hazards[0];
        Assert.Equal(HazardSeverity.High, top.Severity);

        var broken = Assert.Single(hazards, h => h.Id.StartsWith("device.error", StringComparison.Ordinal));
        Assert.Equal(HazardSeverity.High, broken.Severity);
        Assert.Equal(HazardCategory.Display, broken.Category);
        Assert.Contains("Virtual Desktop Monitor", broken.Title, StringComparison.Ordinal);
        Assert.NotNull(broken.Remedy);
    }

    [Fact]
    public void Abusable_kernel_drivers_are_reported_with_their_owning_application()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var rtcore = Assert.Single(hazards, h => h.Id.Contains("rtcore64", StringComparison.Ordinal));
        Assert.Equal(HazardSeverity.High, rtcore.Severity); // running
        Assert.Contains("MSI Afterburner", rtcore.Title, StringComparison.Ordinal);
        Assert.Contains("blocklist", rtcore.Detail, StringComparison.Ordinal);

        // Named without accusing legitimate software of wrongdoing.
        Assert.Contains("software itself is legitimate", rtcore.Detail, StringComparison.Ordinal);

        var xhunter = Assert.Single(hazards, h => h.Id.Contains("xhunter1", StringComparison.Ordinal));
        Assert.Equal(HazardSeverity.Medium, xhunter.Severity); // present but not running
    }

    [Fact]
    public void Ordinary_drivers_are_not_flagged()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        Assert.DoesNotContain(hazards, h => h.Id.Contains("amd3dvcache", StringComparison.Ordinal));
    }

    [Fact]
    public void Coexisting_hypervisors_are_flagged_as_a_launch_failure_cause()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var virt = Assert.Single(hazards, h => h.Id == "virt.coexisting");
        Assert.Contains("VMwareAutostartService", virt.Detail, StringComparison.Ordinal);

        // Says plainly that this is not our fault and not our fix.
        Assert.Contains("GamerGod did not cause", virt.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Wireless_only_networking_is_flagged_honestly_for_multiplayer()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var net = Assert.Single(hazards, h => h.Id == "network.wireless.only");
        Assert.Equal(HazardSeverity.Medium, net.Severity);

        // The honest admission: a cable beats everything this project does.
        Assert.Contains("worth more than every software lever", net.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Multiple_virtual_displays_are_flagged()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var displays = Assert.Single(hazards, h => h.Id == "display.virtual.multiple");
        Assert.Contains("Parsec", displays.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Security posture is reported, never changed.
    // ------------------------------------------------------------------

    [Fact]
    public void A_ready_platform_is_confirmed_rather_than_left_unsaid()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        var ok = Assert.Single(hazards, h => h.Id == "security.platform.ok");
        Assert.Equal(HazardSeverity.Info, ok.Severity);
        Assert.Contains("Battlefield 6", ok.Detail, StringComparison.Ordinal);
        Assert.Contains("never disables platform security", ok.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_secure_boot_is_reported_as_a_launch_blocker_not_a_tweak()
    {
        var snapshot = ReferenceMachine() with { SecureBootEnabled = false };

        var hazard = Assert.Single(HazardScanner.Scan(snapshot), h => h.Id == "security.platform.missing");

        Assert.Equal(HazardSeverity.High, hazard.Severity);
        Assert.Contains("refuse to launch", hazard.Title, StringComparison.Ordinal);
        Assert.Contains("launch requirement, not a performance setting", hazard.Detail, StringComparison.Ordinal);
        Assert.Contains("GamerGod will never turn these off", hazard.Remedy!, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_integrity_is_reported_without_a_recommendation_either_way()
    {
        var hazard = Assert.Single(
            HazardScanner.Scan(ReferenceMachine()), h => h.Id == "security.hvci.off");

        Assert.Equal(HazardSeverity.Low, hazard.Severity);
        Assert.Contains("Your call", hazard.Remedy!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Honest silence on a healthy machine.
    // ------------------------------------------------------------------

    [Fact]
    public void A_clean_machine_produces_no_warnings_at_all()
    {
        var hazards = HazardScanner.Scan(CleanMachine());

        Assert.DoesNotContain(hazards, h => h.Severity >= HazardSeverity.Low);
        Assert.All(hazards, h => Assert.Equal(HazardSeverity.Info, h.Severity));
    }

    [Fact]
    public void System_restore_being_off_is_reported_without_overstating_the_risk()
    {
        var snapshot = CleanMachine() with { SystemRestoreEnabled = false };

        var hazard = Assert.Single(HazardScanner.Scan(snapshot), h => h.Id == "recovery.restore.off");

        // The core guarantee still holds without restore points, and saying otherwise
        // would be scaremongering.
        Assert.Contains("core promise holds", hazard.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_domain_cpu_is_told_plainly_that_it_cannot_be_partitioned()
    {
        var snapshot = CleanMachine() with { PerformanceDomainCount = 1 };

        var hazard = Assert.Single(HazardScanner.Scan(snapshot), h => h.Id == "hardware.single.domain");

        Assert.Equal(HazardSeverity.Info, hazard.Severity);
        Assert.Contains("than pretend to partition", hazard.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_memory_constrained_machine_is_told_it_benefits_more()
    {
        var snapshot = CleanMachine() with { PhysicalMemoryGigabytes = 16 };

        var hazard = Assert.Single(HazardScanner.Scan(snapshot), h => h.Id == "hardware.memory.tight");

        Assert.Contains("larger benefit", hazard.Detail, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Invariants.
    // ------------------------------------------------------------------

    [Fact]
    public void Findings_are_ordered_by_severity()
    {
        var hazards = HazardScanner.Scan(ReferenceMachine());

        for (var i = 1; i < hazards.Length; i++)
        {
            Assert.True(hazards[i - 1].Severity >= hazards[i].Severity);
        }
    }

    [Fact]
    public void Every_actionable_finding_says_what_to_do_about_it()
    {
        // A warning with no remedy is just anxiety.
        foreach (var hazard in HazardScanner.Scan(ReferenceMachine())
                     .Where(h => h.Severity >= HazardSeverity.Low))
        {
            Assert.False(
                string.IsNullOrWhiteSpace(hazard.Remedy),
                $"{hazard.Id} is severity {hazard.Severity} but offers no remedy.");
        }
    }

    [Fact]
    public void Finding_ids_are_unique_so_they_can_be_suppressed_individually()
    {
        var ids = HazardScanner.Scan(ReferenceMachine()).Select(h => h.Id).ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void An_empty_snapshot_does_not_throw()
    {
        var hazards = HazardScanner.Scan(new MachineSnapshot());

        // With nothing known, the only certain finding is that platform security is absent.
        Assert.Contains(hazards, h => h.Id == "security.platform.missing");
    }
}
