using System.Collections.Immutable;

namespace GodMode.Core.Safety;

public enum ProcessRisk
{
    /// <summary>
    /// Suspending it can cook the hardware. Fan and pump control software holds the fan
    /// curve; frozen, the curve stops responding to temperature while the game keeps
    /// generating heat. This is the only category in GodMode with a physical consequence.
    /// </summary>
    Thermal,

    /// <summary>Suspending it bugchecks or hangs Windows immediately.</summary>
    SystemCritical,

    /// <summary>Suspending it strands the user's controller, keyboard or mouse.</summary>
    Input,

    /// <summary>Suspending it silences or corrupts audio.</summary>
    Audio,

    /// <summary>Suspending it breaks a protected game, or looks like tampering.</summary>
    AntiCheat,

    /// <summary>Suspending it drops the user out of a voice call mid-match.</summary>
    Communication,

    /// <summary>Suspending it breaks GodMode's own recovery.</summary>
    Recovery,
}

public sealed record ProcessProtection(string Process, ProcessRisk Risk, string Explanation);

/// <summary>
/// The processes GodMode will never suspend.
///
/// <para>
/// Two entries in this list have consequences that outlive the session. Suspending fan or
/// pump control software freezes the fan curve while the game keeps producing heat, and
/// suspending a core Windows process bugchecks the machine. Everything else here merely
/// ruins the session.
/// </para>
///
/// <para>
/// Demotion is always available as the safer alternative. A process that must not be
/// suspended can still be moved to the ambient domain and marked as efficiency work, which
/// recovers most of the benefit with none of the risk.
/// </para>
/// </summary>
public static class ProcessSafetyPolicy
{
    public static readonly ImmutableArray<ProcessProtection> Protected =
    [
        // ---- Thermal. The only category here that can damage hardware.
        Thermal("icue", "Corsair iCUE controls case fans and AIO pump speed."),
        Thermal("corsairservice", "Corsair service backing iCUE fan and pump control."),
        Thermal("cue", "Corsair Utility Engine."),
        Thermal("nzxt cam", "NZXT CAM controls AIO pump and fan speed."),
        Thermal("camsvc", "NZXT CAM service."),
        Thermal("fancontrol", "FanControl drives the fan curves on this machine."),
        Thermal("argusmonitor", "Argus Monitor fan control."),
        Thermal("speedfan", "SpeedFan."),
        Thermal("msiafterburner", "MSI Afterburner holds the fan curve and the GPU power limit."),
        Thermal("rtss", "RivaTuner Statistics Server, installed with Afterburner."),
        Thermal("aisuite", "ASUS AI Suite fan control."),
        Thermal("armourycrate", "ASUS Armoury Crate fan and power control."),
        Thermal("asusservice", "ASUS system control service."),
        Thermal("lightingservice", "ASUS Aura, which shares the embedded controller with fan control."),
        Thermal("signalrgb", "SignalRGB can own fan headers depending on configuration."),
        Thermal("openrgb", "OpenRGB can own fan headers depending on configuration."),
        Thermal("hwinfo", "HWiNFO can drive fan control and is the sensor source for other tools."),
        Thermal("gigabytecontrolcenter", "Gigabyte Control Center fan control."),
        Thermal("mysticlight", "MSI Mystic Light, which shares the embedded controller."),

        // ---- System critical. Suspending any of these hangs or bugchecks Windows.
        SystemCritical("csrss", "Client/Server Runtime. Suspending it bugchecks the machine."),
        SystemCritical("wininit", "Windows startup process."),
        SystemCritical("winlogon", "Logon process. Suspending it locks the session out."),
        SystemCritical("services", "Service Control Manager."),
        SystemCritical("lsass", "Local Security Authority. Suspending it bugchecks the machine."),
        SystemCritical("smss", "Session Manager."),
        SystemCritical("system", "The kernel."),
        SystemCritical("registry", "Registry process."),
        SystemCritical("memory compression", "Memory compression. Suspending it stalls paging."),
        SystemCritical("dwm", "Desktop Window Manager. Suspending it freezes all rendering, including the game."),
        SystemCritical("svchost", "Shared service host. Its contents vary and include critical services."),
        SystemCritical("fontdrvhost", "Font driver host. Suspending it stalls text rendering."),
        SystemCritical("audiodg", "Audio device graph. Suspending it stalls the audio engine."),
        SystemCritical("wudfhost", "User-mode driver host. Suspending it stalls the devices it hosts."),
        SystemCritical("sihost", "Shell infrastructure host."),
        SystemCritical("ctfmon", "Text input host."),

        // ---- Input. A stranded controller in a fullscreen shell has no way out.
        Input("hidhide", "HidHide controls which devices games can see."),
        Input("hidhideclient", "HidHide client."),
        Input("ds4windows", "DS4Windows provides the virtual gamepad a game is reading."),
        Input("rewasd", "reWASD remapping."),
        Input("vigembus", "ViGEm virtual gamepad bus."),
        Input("steam", "Steam provides Steam Input controller translation."),
        Input("xoutput", "XOutput controller translation."),

        // ---- Audio.
        Audio("voicemeeter", "VoiceMeeter is in the audio path."),
        Audio("voicemeeterpro", "VoiceMeeter Banana or Potato."),
        Audio("equalizerapo", "Equalizer APO is in the audio path."),
        Audio("peace", "Peace, the Equalizer APO front end."),
        Audio("voicemod", "Voicemod is in the audio path."),

        // ---- Anti-cheat. Charter Article I.
        AntiCheat("easyanticheat", "EasyAntiCheat."),
        AntiCheat("beservice", "BattlEye."),
        AntiCheat("eaanticheat.gameservice", "EA Javelin."),
        AntiCheat("vgtray", "Riot Vanguard tray."),
        AntiCheat("vgc", "Riot Vanguard."),
        AntiCheat("start_protected_game", "EasyAntiCheat launcher."),

        // ---- Communication. Suspending these drops the user mid-match.
        Communication("discord", "Discord. Suspending it drops an active voice call."),
        Communication("teamspeak", "TeamSpeak."),
        Communication("mumble", "Mumble."),
        Communication("ts3client_win64", "TeamSpeak 3."),

        // ---- GodMode itself.
        Recovery("gmsvc", "GodMode service. Suspending it disables crash recovery."),
        Recovery("gmagent", "GodMode session agent. It owns the panic hotkey."),
        Recovery("godmode", "GodMode."),
    ];

    private static readonly ImmutableDictionary<string, ProcessProtection> Index =
        Protected.ToImmutableDictionary(p => p.Process, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when suspending this process could harm hardware or hang the machine.</summary>
    public static bool IsProtected(string processName) => Explain(processName) is not null;

    public static ProcessProtection? Explain(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }

        // Accept a bare name, a name with extension, or a full path, because callers get
        // process identity from several different APIs and a safety check that depends on
        // the caller normalising first is a safety check that will eventually be skipped.
        var name = processName.Trim().Trim('"');

        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return Index.TryGetValue(name, out var protection) ? protection : null;
    }

    /// <summary>
    /// Splits a proposed suspension list into what may be suspended and what may not.
    /// Blocked processes remain valid targets for domain confinement and efficiency-mode
    /// demotion, which is where most of the benefit is anyway.
    /// </summary>
    public static (ImmutableArray<string> Suspendable, ImmutableArray<ProcessProtection> Blocked) Filter(
        IEnumerable<string> proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);

        var suspendable = ImmutableArray.CreateBuilder<string>();
        var blocked = ImmutableArray.CreateBuilder<ProcessProtection>();

        foreach (var process in proposed)
        {
            var protection = Explain(process);
            if (protection is null)
            {
                suspendable.Add(process);
            }
            else
            {
                blocked.Add(protection);
            }
        }

        return (suspendable.ToImmutable(), blocked.ToImmutable());
    }

    private static ProcessProtection Thermal(string p, string why) => new(p, ProcessRisk.Thermal, why);

    private static ProcessProtection SystemCritical(string p, string why) =>
        new(p, ProcessRisk.SystemCritical, why);

    private static ProcessProtection Input(string p, string why) => new(p, ProcessRisk.Input, why);

    private static ProcessProtection Audio(string p, string why) => new(p, ProcessRisk.Audio, why);

    private static ProcessProtection AntiCheat(string p, string why) => new(p, ProcessRisk.AntiCheat, why);

    private static ProcessProtection Communication(string p, string why) =>
        new(p, ProcessRisk.Communication, why);

    private static ProcessProtection Recovery(string p, string why) => new(p, ProcessRisk.Recovery, why);
}
