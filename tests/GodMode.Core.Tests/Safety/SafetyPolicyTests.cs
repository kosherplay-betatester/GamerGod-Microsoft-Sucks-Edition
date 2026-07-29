using GodMode.Core.Hardware;
using GodMode.Core.Safety;
using GodMode.Core.Tests.Hardware;
using Xunit;

namespace GodMode.Core.Tests.Safety;

/// <summary>
/// The thermal guard. This is the only part of GodMode whose failure mode is physical, so
/// it gets its own class.
/// </summary>
public sealed class ThermalSafetyTests
{
    public static TheoryData<string> FanAndPumpSoftware() =>
    [
        "iCUE.exe", "CorsairService.exe", "NZXT CAM.exe", "FanControl.exe",
        "ArgusMonitor.exe", "SpeedFan.exe", "MSIAfterburner.exe", "RTSS.exe",
        "AISuite.exe", "ArmouryCrate.exe", "LightingService.exe", "SignalRGB.exe",
        "OpenRGB.exe", "HWiNFO.exe", "GigabyteControlCenter.exe", "MysticLight.exe",
    ];

    [Theory]
    [MemberData(nameof(FanAndPumpSoftware))]
    public void Fan_and_pump_software_can_never_be_suspended(string process)
    {
        // Suspending these freezes the fan curve at whatever it was, while the game keeps
        // producing heat. Every other risk in this project ruins a session; this one can
        // ruin hardware.
        var protection = ProcessSafetyPolicy.Explain(process);

        Assert.NotNull(protection);
        Assert.Equal(ProcessRisk.Thermal, protection!.Risk);
    }

    [Theory]
    [MemberData(nameof(FanAndPumpSoftware))]
    public void Fan_software_is_blocked_no_matter_how_the_caller_names_it(string process)
    {
        var bare = Path.GetFileNameWithoutExtension(process);

        foreach (var form in new[]
                 {
                     process,
                     bare,
                     process.ToUpperInvariant(),
                     bare.ToLowerInvariant(),
                     $@"C:\Program Files\Vendor\{process}",
                     $@"""C:\Program Files\Vendor\{process}""",
                 })
        {
            Assert.True(
                ProcessSafetyPolicy.IsProtected(form),
                $"Thermal software slipped through when named '{form}'.");
        }
    }

    [Fact]
    public void A_suspension_list_containing_fan_software_is_filtered_not_rejected()
    {
        // The rest of the list must still be actioned. Refusing everything because one
        // entry is unsafe would push users toward disabling the safety check.
        var (suspendable, blocked) = ProcessSafetyPolicy.Filter(
            ["chrome.exe", "iCUE.exe", "msedge.exe", "NZXT CAM.exe", "Spotify.exe"]);

        Assert.Equal(["chrome.exe", "msedge.exe", "Spotify.exe"], suspendable.ToArray());
        Assert.Equal(2, blocked.Length);
        Assert.All(blocked, b => Assert.Equal(ProcessRisk.Thermal, b.Risk));
    }
}

public sealed class SystemCriticalProcessTests
{
    public static TheoryData<string> ProcessesThatWouldBugcheckOrHang() =>
    [
        "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe",
        "smss.exe", "System", "Registry", "Memory Compression", "dwm.exe",
        "svchost.exe", "fontdrvhost.exe", "audiodg.exe", "WUDFHost.exe",
        "sihost.exe", "ctfmon.exe",
    ];

    [Theory]
    [MemberData(nameof(ProcessesThatWouldBugcheckOrHang))]
    public void Suspending_a_core_windows_process_is_refused(string process)
    {
        var protection = ProcessSafetyPolicy.Explain(process);

        Assert.NotNull(protection);
        Assert.Equal(ProcessRisk.SystemCritical, protection!.Risk);
    }

    [Fact]
    public void Dwm_is_protected_because_freezing_it_freezes_the_game_too()
    {
        var dwm = ProcessSafetyPolicy.Explain("dwm.exe");

        Assert.NotNull(dwm);
        Assert.Contains("freezes all rendering", dwm!.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_applications_are_still_suspendable()
    {
        foreach (var process in new[]
                 {
                     "chrome.exe", "msedge.exe", "firefox.exe", "Slack.exe",
                     "Teams.exe", "OneDrive.exe", "Dropbox.exe", "notepad.exe",
                 })
        {
            Assert.False(
                ProcessSafetyPolicy.IsProtected(process),
                $"{process} was blocked, which would leave nothing worth suspending.");
        }
    }
}

public sealed class ServiceSafetyPolicyTests
{
    public static TheoryData<string, ProtectionReason> CriticalServices() => new()
    {
        { "PlugPlay", ProtectionReason.Hardware },
        { "DeviceInstall", ProtectionReason.Hardware },
        { "DeviceAssociationService", ProtectionReason.Hardware },
        { "hidserv", ProtectionReason.Input },
        { "XboxGipSvc", ProtectionReason.Input },
        { "bthserv", ProtectionReason.Input },
        { "BTAGService", ProtectionReason.Input },
        { "TextInputManagementService", ProtectionReason.Input },
        { "Audiosrv", ProtectionReason.Audio },
        { "AudioEndpointBuilder", ProtectionReason.Audio },
        { "MMCSS", ProtectionReason.Audio },
        { "Dhcp", ProtectionReason.Network },
        { "Dnscache", ProtectionReason.Network },
        { "WlanSvc", ProtectionReason.Network },
        { "EAAntiCheatService", ProtectionReason.AntiCheat },
        { "EasyAntiCheat", ProtectionReason.AntiCheat },
        { "BEService", ProtectionReason.AntiCheat },
        { "vgk", ProtectionReason.AntiCheat },
        { "WinDefend", ProtectionReason.Security },
        { "mpssvc", ProtectionReason.Security },
        { "RpcSs", ProtectionReason.CoreOperatingSystem },
        { "DcomLaunch", ProtectionReason.CoreOperatingSystem },
        { "lsass", ProtectionReason.CoreOperatingSystem },
        { "EventLog", ProtectionReason.Recovery },
        { "VSS", ProtectionReason.Recovery },
    };

    [Theory]
    [MemberData(nameof(CriticalServices))]
    public void Protected_services_are_never_stoppable(string service, ProtectionReason expected)
    {
        var protection = ServiceSafetyPolicy.Explain(service);

        // lsass is listed as a process rather than a service in some Windows versions;
        // the service list is authoritative for what SCM can be asked to stop.
        if (protection is null && string.Equals(service, "lsass", StringComparison.OrdinalIgnoreCase))
        {
            Assert.True(ProcessSafetyPolicy.IsProtected("lsass.exe"));
            return;
        }

        Assert.NotNull(protection);
        Assert.Equal(expected, protection!.Reason);
    }

    [Theory]
    [InlineData("plugplay")]
    [InlineData("PLUGPLAY")]
    [InlineData("PlugPlay")]
    [InlineData("  PlugPlay  ")]
    public void Service_matching_ignores_case_and_surrounding_space(string name)
    {
        Assert.True(ServiceSafetyPolicy.IsProtected(name));
    }

    [Theory]
    [InlineData("BluetoothUserService_c4694")]
    [InlineData("BluetoothUserService_1a2b3")]
    [InlineData("DeviceAssociationBrokerSvc_deadbeef")]
    public void Per_user_service_instances_are_matched_by_their_base_name(string instance)
    {
        // These carry a random suffix that differs per machine. Matching only the literal
        // name would protect a service on one machine and miss it on the next, which is the
        // worst possible failure shape for a safety list.
        Assert.True(
            ServiceSafetyPolicy.IsProtected(instance),
            $"{instance} was not recognised as a protected per-user service instance.");
    }

    [Fact]
    public void A_suffix_that_is_not_an_instance_id_does_not_cause_a_false_match()
    {
        // "Something_notahexvalue" must not be treated as an instance of "Something".
        Assert.False(ServiceSafetyPolicy.IsProtected("PlugPlay_notahexvalue"));
        Assert.False(ServiceSafetyPolicy.IsProtected("PlugPlay_"));
    }

    [Fact]
    public void Services_worth_stopping_are_still_allowed()
    {
        foreach (var service in new[] { "WSearch", "SysMain", "DiagTrack", "wuauserv", "BITS", "MapsBroker" })
        {
            Assert.False(
                ServiceSafetyPolicy.IsProtected(service),
                $"{service} was blocked, which would leave nothing useful to suspend.");
        }
    }

    [Fact]
    public void A_profile_containing_protected_services_is_filtered_with_reasons()
    {
        var (allowed, blocked) = ServiceSafetyPolicy.Filter(
            ["WSearch", "PlugPlay", "SysMain", "Audiosrv", "DiagTrack", "EAAntiCheatService"]);

        Assert.Equal(["WSearch", "SysMain", "DiagTrack"], allowed.ToArray());
        Assert.Equal(3, blocked.Length);
        Assert.All(blocked, b => Assert.False(string.IsNullOrWhiteSpace(b.Explanation)));
    }

    [Fact]
    public void Every_protection_explains_itself()
    {
        // Charter: a disabled control must say why. An unexplained refusal reads as a bug.
        Assert.All(ServiceSafetyPolicy.Protected, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Service));
            Assert.False(string.IsNullOrWhiteSpace(p.Explanation));
        });

        Assert.All(ProcessSafetyPolicy.Protected, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Process));
            Assert.False(string.IsNullOrWhiteSpace(p.Explanation));
        });
    }

    [Fact]
    public void No_duplicate_entries_hide_a_conflicting_reason()
    {
        var services = ServiceSafetyPolicy.Protected.Select(p => p.Service).ToArray();
        Assert.Equal(services.Length, services.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var processes = ProcessSafetyPolicy.Protected.Select(p => p.Process).ToArray();
        Assert.Equal(processes.Length, processes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_input_is_not_treated_as_protected_and_does_not_throw(string? input)
    {
        Assert.False(ServiceSafetyPolicy.IsProtected(input!));
        Assert.False(ProcessSafetyPolicy.IsProtected(input!));
    }
}

/// <summary>
/// Guards the failure mode that would make a machine unrecoverable: confining Windows to a
/// processor set it cannot run on.
/// </summary>
public sealed class RoutingSafetyTests
{
    private static CpuTopology Reference() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    [Fact]
    public void The_reference_machine_produces_a_safe_plan()
    {
        Assert.True(RoutingSafety.ValidateTopologyPlan(Reference()).IsSafe);
    }

    [Fact]
    public void An_empty_ambient_mask_is_refused()
    {
        // The catastrophic case: every background process confined to nothing. Windows has
        // nowhere to schedule the shell, the watchdog or the panic hotkey, and the only way
        // out is the power button.
        var topology = Reference();

        var result = RoutingSafety.Validate(
            topology.GameDomain.Processors, default, topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "ambient.mask.empty");
        Assert.Contains("nowhere to schedule", result.Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_game_mask_is_refused()
    {
        var topology = Reference();

        var result = RoutingSafety.Validate(default, topology.AmbientMask, topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "game.mask.empty");
    }

    [Fact]
    public void Confining_windows_to_a_single_processor_is_refused()
    {
        var topology = Reference();

        var result = RoutingSafety.Validate(
            topology.GameDomain.Processors,
            ProcessorMask.FromLogicalProcessors(0, 31),
            topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "ambient.mask.too.small");
    }

    [Fact]
    public void Overlapping_masks_are_refused()
    {
        var topology = Reference();

        var result = RoutingSafety.Validate(
            ProcessorMask.Range(0, 0, 16),   // one processor into the ambient domain
            ProcessorMask.Range(0, 16, 31),
            topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "mask.overlap");
    }

    [Fact]
    public void A_mask_selecting_processors_the_machine_lacks_is_refused()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7800X3D()); // 16 threads

        var result = RoutingSafety.Validate(
            ProcessorMask.Range(0, 0, 7),
            ProcessorMask.Range(0, 40, 47),
            topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "ambient.mask.unknown.processors");
    }

    [Fact]
    public void Masks_in_different_processor_groups_are_refused()
    {
        var topology = Reference();

        var result = RoutingSafety.Validate(
            new ProcessorMask(0, 0x000000000000FFFF),
            new ProcessorMask(1, 0x00000000FFFF0000),
            topology);

        Assert.False(result.IsSafe);
        Assert.Contains(result.Violations, v => v.Code == "mask.group.mismatch");
    }

    [Fact]
    public void A_single_domain_machine_is_safe_precisely_because_it_does_not_partition()
    {
        var topology = PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7800X3D());

        Assert.False(topology.CanPartition);
        Assert.True(RoutingSafety.ValidateTopologyPlan(topology).IsSafe);
    }

    [Fact]
    public void Every_known_cpu_produces_a_safe_plan()
    {
        foreach (var snapshot in new[]
                 {
                     KnownCpus.Ryzen7950X3D(),
                     KnownCpus.Ryzen7950X3DInverted(),
                     KnownCpus.Ryzen7800X3D(),
                     KnownCpus.Ryzen7950X(),
                     KnownCpus.CoreI9_12900K(),
                     KnownCpus.CoreUltra7_155H(),
                     KnownCpus.NoLastLevelCache(),
                 })
        {
            var topology = PerformanceDomainClassifier.Classify(snapshot);
            var result = RoutingSafety.ValidateTopologyPlan(topology);

            Assert.True(
                result.IsSafe,
                $"{snapshot.ProcessorName} produced an unsafe plan: {result.Explain()}");
        }
    }

    [Fact]
    public void Dropping_smt_siblings_never_produces_an_empty_mask()
    {
        foreach (var snapshot in new[]
                 {
                     KnownCpus.Ryzen7950X3D(),
                     KnownCpus.CoreI9_12900K(),
                     KnownCpus.CoreUltra7_155H(),
                     KnownCpus.NoLastLevelCache(),
                 })
        {
            var topology = PerformanceDomainClassifier.Classify(snapshot);

            foreach (var domain in topology.Domains)
            {
                var restricted = RoutingSafety.WithoutSmtSiblings(domain);

                Assert.False(restricted.IsEmpty, $"{snapshot.ProcessorName} D{domain.Id} emptied.");
                Assert.Equal(0UL, restricted.Mask & ~domain.Processors.Mask);
            }
        }
    }
}
