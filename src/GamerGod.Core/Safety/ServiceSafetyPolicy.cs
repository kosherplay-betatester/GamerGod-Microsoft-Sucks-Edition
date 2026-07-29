using System.Collections.Immutable;

namespace GamerGod.Core.Safety;

/// <param name="Reason">Why this service is protected. Shown in the UI when a control is disabled.</param>
public sealed record ServiceProtection(string Service, ProtectionReason Reason, string Explanation);

public enum ProtectionReason
{
    /// <summary>Stopping it breaks device enumeration, hot-plug or a peripheral.</summary>
    Hardware,

    /// <summary>Stopping it breaks controllers, keyboards, mice or on-screen input.</summary>
    Input,

    /// <summary>Stopping it breaks sound output or an external audio interface.</summary>
    Audio,

    /// <summary>Stopping it breaks networking, and with it every multiplayer game.</summary>
    Network,

    /// <summary>Stopping it breaks graphics, variable refresh or GPU control.</summary>
    Graphics,

    /// <summary>Stopping it would prevent a protected game from launching or running.</summary>
    AntiCheat,

    /// <summary>Stopping it would weaken the machine's security. Charter Article V.</summary>
    Security,

    /// <summary>Stopping it destabilises Windows itself.</summary>
    CoreOperatingSystem,

    /// <summary>Stopping it would disable GamerGod's own recovery path.</summary>
    Recovery,
}

/// <summary>
/// The services GamerGod will never stop, whatever a profile asks for.
///
/// <para>
/// This list is not advisory and a profile cannot override it. It exists because the
/// difference between a tuning tool and a broken machine is usually one entry on a list
/// like this — stopping <c>PlugPlay</c> silently kills hot-plug until reboot, stopping
/// <c>Audiosrv</c> takes the sound with it, and stopping an anti-cheat service turns a
/// working game into one that will not start.
/// </para>
///
/// <para>
/// Additions are welcome; removals require evidence. The asymmetry is deliberate: a wrong
/// entry costs a little performance, a missing one costs a working system.
/// </para>
/// </summary>
public static class ServiceSafetyPolicy
{
    public static readonly ImmutableArray<ServiceProtection> Protected =
    [
        // ---- Device enumeration and hot-plug. Peripherals stop appearing without these.
        Deny("PlugPlay", ProtectionReason.Hardware,
            "Plug and Play. Stopping this kills device hot-plug until the machine reboots."),
        Deny("DeviceInstall", ProtectionReason.Hardware, "Installs drivers for newly attached devices."),
        Deny("DeviceAssociationService", ProtectionReason.Hardware, "Pairs and tracks connected devices."),
        Deny("DeviceAssociationBrokerSvc", ProtectionReason.Hardware, "Device pairing broker."),
        Deny("DsmSvc", ProtectionReason.Hardware, "Device Setup Manager."),
        Deny("UserDataSvc", ProtectionReason.Hardware, "Device state broker used during enumeration."),

        // ---- Input. A stranded controller in a fullscreen shell is unrecoverable.
        Deny("hidserv", ProtectionReason.Input,
            "Human Interface Device service. Volume keys, media keys and many gamepads route through it."),
        Deny("XboxGipSvc", ProtectionReason.Input, "Xbox controllers and the wireless adapter."),
        Deny("BTAGService", ProtectionReason.Input, "Bluetooth audio and controller gateway."),
        Deny("BthAvctpSvc", ProtectionReason.Input, "Bluetooth AVCTP, used by controllers and headsets."),
        Deny("bthserv", ProtectionReason.Input, "Bluetooth support. Wireless controllers depend on it."),
        Deny("BluetoothUserService", ProtectionReason.Input, "Per-user Bluetooth service."),
        Deny("TextInputManagementService", ProtectionReason.Input,
            "On-screen keyboard. In a controller-only shell this is the only way to type."),
        Deny("TabletInputService", ProtectionReason.Input, "Touch and pen input."),

        // ---- Audio.
        Deny("Audiosrv", ProtectionReason.Audio, "Windows Audio. Stopping it silences the machine."),
        Deny("AudioEndpointBuilder", ProtectionReason.Audio,
            "Builds audio endpoints. USB interfaces and DACs disappear without it."),
        Deny("MMCSS", ProtectionReason.Audio,
            "Multimedia Class Scheduler. Games and audio engines rely on it for thread priority - "
            + "stopping it degrades exactly the latency GamerGod exists to improve."),

        // ---- Network. Every multiplayer game depends on these.
        Deny("Dhcp", ProtectionReason.Network, "DHCP client."),
        Deny("Dnscache", ProtectionReason.Network, "DNS resolution."),
        Deny("NlaSvc", ProtectionReason.Network, "Network location awareness."),
        Deny("netprofm", ProtectionReason.Network, "Network list service."),
        Deny("nsi", ProtectionReason.Network, "Network store interface."),
        Deny("WlanSvc", ProtectionReason.Network, "Wi-Fi. On a wireless-only machine this is the connection."),
        Deny("LanmanWorkstation", ProtectionReason.Network, "SMB client. Game libraries on a NAS need it."),
        Deny("NetSetupSvc", ProtectionReason.Network, "Network setup service."),

        // ---- Graphics and power.
        Deny("NVDisplay.ContainerLocalSystem", ProtectionReason.Graphics,
            "NVIDIA display container. Stopping it can drop G-Sync and driver settings."),
        Deny("AMD External Events Utility", ProtectionReason.Graphics,
            "AMD external events. FreeSync and driver features depend on it."),
        Deny("amd3dvcache", ProtectionReason.Graphics,
            "AMD 3D V-Cache optimiser. It participates in the very scheduling GamerGod tunes."),
        Deny("AmdPPM", ProtectionReason.Graphics, "AMD processor power management."),
        Deny("Power", ProtectionReason.CoreOperatingSystem, "Power service. Core to processor power states."),

        // ---- Anti-cheat. Charter Article I.
        Deny("EAAntiCheatService", ProtectionReason.AntiCheat,
            "EA Javelin. Battlefield 6 will not run without it."),
        Deny("EasyAntiCheat", ProtectionReason.AntiCheat, "EasyAntiCheat."),
        Deny("EasyAntiCheat_EOS", ProtectionReason.AntiCheat, "EasyAntiCheat for Epic Online Services."),
        Deny("BEService", ProtectionReason.AntiCheat, "BattlEye."),
        Deny("vgc", ProtectionReason.AntiCheat, "Riot Vanguard client."),
        Deny("vgk", ProtectionReason.AntiCheat, "Riot Vanguard kernel component."),
        Deny("xhunter1", ProtectionReason.AntiCheat, "XIGNCODE3."),
        Deny("npggsvc", ProtectionReason.AntiCheat, "nProtect GameGuard."),
        Deny("Steam Client Service", ProtectionReason.AntiCheat, "Steam client service."),
        Deny("SteamInputDriver", ProtectionReason.Input, "Steam Input controller support."),

        // ---- Security. Charter Article V: never weakened for frames.
        Deny("WinDefend", ProtectionReason.Security, "Microsoft Defender."),
        Deny("SecurityHealthService", ProtectionReason.Security, "Windows Security."),
        Deny("wscsvc", ProtectionReason.Security, "Security Center."),
        Deny("mpssvc", ProtectionReason.Security, "Windows Firewall."),
        Deny("Sense", ProtectionReason.Security, "Defender for Endpoint."),
        Deny("webthreatdefsvc", ProtectionReason.Security, "Web threat defence."),
        Deny("SgrmBroker", ProtectionReason.Security, "System Guard runtime monitor."),

        // ---- Core Windows. Stopping any of these destabilises the machine outright.
        Deny("RpcSs", ProtectionReason.CoreOperatingSystem, "Remote Procedure Call. Almost everything uses it."),
        Deny("DcomLaunch", ProtectionReason.CoreOperatingSystem, "DCOM server launcher."),
        Deny("LSM", ProtectionReason.CoreOperatingSystem, "Local session manager."),
        Deny("CryptSvc", ProtectionReason.CoreOperatingSystem, "Cryptographic services. Signature checks need it."),
        Deny("ProfSvc", ProtectionReason.CoreOperatingSystem, "User profile service."),
        Deny("Themes", ProtectionReason.CoreOperatingSystem, "Desktop themes. The shell misbehaves without it."),
        Deny("gpsvc", ProtectionReason.CoreOperatingSystem, "Group policy."),
        Deny("Schedule", ProtectionReason.CoreOperatingSystem, "Task Scheduler. Boot recovery is scheduled work."),
        Deny("SamSs", ProtectionReason.CoreOperatingSystem, "Security Accounts Manager."),
        Deny("Winmgmt", ProtectionReason.CoreOperatingSystem, "WMI. Widely depended upon by drivers and tools."),

        // ---- GamerGod's own recovery path.
        Deny("EventLog", ProtectionReason.Recovery, "Event log. GamerGod's diagnostics and post-crash trail."),
        Deny("VSS", ProtectionReason.Recovery, "Volume Shadow Copy. Restore points depend on it."),
        Deny("swprv", ProtectionReason.Recovery, "Shadow copy provider."),
    ];

    private static readonly ImmutableDictionary<string, ServiceProtection> Index =
        Protected.ToImmutableDictionary(p => p.Service, StringComparer.OrdinalIgnoreCase);

    private static ServiceProtection Deny(string service, ProtectionReason reason, string explanation) =>
        new(service, reason, explanation);

    /// <summary>
    /// True when GamerGod must never stop this service.
    ///
    /// <para>
    /// Per-user services carry a random suffix — <c>CDPUserSvc_c4694</c> is one instance of
    /// <c>CDPUserSvc</c> — so the suffix is stripped before matching. Without that, a
    /// protected service would slip through on one machine and not another, which is the
    /// worst possible failure shape for a safety list.
    /// </para>
    /// </summary>
    public static bool IsProtected(string serviceName) => Explain(serviceName) is not null;

    public static ServiceProtection? Explain(string serviceName)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return null;
        }

        var name = serviceName.Trim();

        if (Index.TryGetValue(name, out var direct))
        {
            return direct;
        }

        var stripped = StripPerUserSuffix(name);
        return stripped is not null && Index.TryGetValue(stripped, out var byBase) ? byBase : null;
    }

    /// <summary>
    /// Filters a proposed stop list down to what is permitted. Rejections are returned
    /// separately so the UI can explain each one rather than silently doing less than asked.
    /// </summary>
    public static (ImmutableArray<string> Allowed, ImmutableArray<ServiceProtection> Blocked) Filter(
        IEnumerable<string> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        var allowed = ImmutableArray.CreateBuilder<string>();
        var blocked = ImmutableArray.CreateBuilder<ServiceProtection>();

        foreach (var service in proposed)
        {
            var protection = Explain(service);
            if (protection is null)
            {
                allowed.Add(service);
            }
            else
            {
                blocked.Add(protection);
            }
        }

        return (allowed.ToImmutable(), blocked.ToImmutable());
    }

    /// <summary>
    /// Removes a trailing <c>_</c> plus hex-like instance id from a per-user service name.
    /// Returns null when there is no such suffix.
    /// </summary>
    private static string? StripPerUserSuffix(string name)
    {
        var underscore = name.LastIndexOf('_');
        if (underscore <= 0 || underscore == name.Length - 1)
        {
            return null;
        }

        var suffix = name.AsSpan(underscore + 1);
        foreach (var c in suffix)
        {
            if (!Uri.IsHexDigit(c))
            {
                return null;
            }
        }

        return name[..underscore];
    }
}
