using GamerGod.Core.Diagnostics;
using Xunit;

namespace GamerGod.Core.Tests.Diagnostics;

/// <summary>
/// Regression cover for a false positive found the first time the scan ran on real
/// hardware. It reported two deliberately disabled devices as failures — including one a
/// vendor ships disabled by default — which would have filled a healthy machine's first-run
/// report with red text. A diagnostic nobody believes is worse than no diagnostic.
/// </summary>
public sealed class DisabledDeviceTests
{
    private static MachineSnapshot With(params DeviceEntry[] devices) => new()
    {
        Devices = [.. devices],
        SecureBootEnabled = true,
        TpmReady = true,
        SystemRestoreEnabled = true,
        PerformanceDomainCount = 2,
        PhysicalMemoryGigabytes = 32,
        NetworkAdapters =
        [
            new NetworkAdapterEntry("Ethernet", "Intel I225-V", true, false, false, 2_500_000_000),
        ],
    };

    [Fact]
    public void A_device_the_user_disabled_is_not_reported_as_broken()
    {
        var hazards = HazardScanner.Scan(With(
            new DeviceEntry("Virtual Desktop Monitor", "Display", "Disabled"),
            new DeviceEntry("AMD Application Compatibility Database", "System", "Disabled")));

        Assert.DoesNotContain(hazards, h => h.Id.StartsWith("device.error", StringComparison.Ordinal));
        Assert.All(hazards, h => Assert.Equal(HazardSeverity.Info, h.Severity));
    }

    [Theory]
    [InlineData("Cannot start")]
    [InlineData("Stopped by Windows (code 43)")]
    [InlineData("Driver failed to load")]
    [InlineData("Drivers not installed")]
    public void A_device_that_genuinely_failed_is_still_reported(string status)
    {
        var hazards = HazardScanner.Scan(With(
            new DeviceEntry("NVIDIA GeForce RTX 5070 Ti", "Display", status)));

        var failure = Assert.Single(hazards, h => h.Id.StartsWith("device.error", StringComparison.Ordinal));
        Assert.Equal(HazardSeverity.High, failure.Severity);
    }

    [Fact]
    public void Disabled_virtual_displays_do_not_count_toward_the_clutter_warning()
    {
        // Two virtual adapters, but one is switched off, so only one is actually
        // participating in composition. That is not clutter.
        var hazards = HazardScanner.Scan(With(
            new DeviceEntry("Virtual Desktop Monitor", "Display", "Disabled"),
            new DeviceEntry("Parsec Virtual Display Adapter", "Display", "OK")));

        Assert.DoesNotContain(hazards, h => h.Id == "display.virtual.multiple");
    }

    [Fact]
    public void Two_live_virtual_displays_still_warn()
    {
        var hazards = HazardScanner.Scan(With(
            new DeviceEntry("Virtual Desktop Monitor", "Display", "OK"),
            new DeviceEntry("Parsec Virtual Display Adapter", "Display", "OK")));

        Assert.Contains(hazards, h => h.Id == "display.virtual.multiple");
    }
}
