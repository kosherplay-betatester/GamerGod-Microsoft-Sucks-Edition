using System.Collections.Immutable;

namespace GamerGod.Core.Safety;

public enum ProcessRisk
{
    /// <summary>
    /// Suspending it can cook the hardware. Fan and pump control software holds the fan
    /// curve; frozen, the curve stops responding to temperature while the game keeps
    /// generating heat. This is the only category in GamerGod with a physical consequence.
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

    /// <summary>Suspending it breaks GamerGod's own recovery.</summary>
    Recovery,
}

public sealed record ProcessProtection(string Process, ProcessRisk Risk, string Explanation);

/// <summary>
/// The processes GamerGod will never suspend.
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

        // ---- Laptop thermal management.
        //
        // Laptops matter more here than desktops, not less. They run hotter, have far less
        // thermal headroom, and depend on software far more of the time - and they are the
        // machines GamerGod helps most, because they are the most contended. Freezing a fan
        // curve on a chassis with no margin is the worst thing this project could do.
        Thermal("esif_uf", "Intel Dynamic Platform and Thermal Framework. Manages thermal "
                         + "throttling on most Intel laptops; suspending it removes thermal management."),
        Thermal("ipf_uf", "Intel Innovation Platform Framework, successor to DPTF."),
        Thermal("dptf_helper", "Intel Dynamic Platform and Thermal Framework helper."),
        Thermal("notebookfancontrol", "NoteBook FanControl drives the fan curve directly."),
        Thermal("nbfc", "NoteBook FanControl service."),
        Thermal("controlcenter", "Clevo and Tongfang control centre, which owns laptop fan control."),
        Thermal("msi center", "MSI Center laptop fan and performance control."),
        Thermal("msi center service", "MSI Center service."),
        Thermal("dragon center", "MSI Dragon Center laptop fan control."),
        Thermal("omencommandcenterbackground", "HP OMEN fan and performance control."),
        Thermal("omensystemservice", "HP OMEN system service."),
        Thermal("predatorsense", "Acer PredatorSense fan control."),
        Thermal("nitrosense", "Acer NitroSense fan control."),
        Thermal("awcc", "Alienware Command Center fan control."),
        Thermal("awccservicecontroller", "Alienware Command Center service."),
        Thermal("alienfusionservice", "Alienware thermal profile service."),
        Thermal("lenovolegiontoolkit", "Lenovo Legion Toolkit fan control."),
        Thermal("legionzone", "Lenovo Legion Zone performance and thermal control."),
        Thermal("lenovovantageservice", "Lenovo Vantage, which owns thermal mode on Legion laptops."),
        Thermal("imcontroller", "Lenovo Intelligent Manageability controller, used for thermal mode."),
        Thermal("asusoptimization", "ASUS Optimization service, which owns ROG laptop fan curves."),
        Thermal("asussystemanalysis", "ASUS system analysis, part of the thermal stack."),
        Thermal("rogliveservice", "ASUS ROG Live Service."),
        Thermal("g14controlcenter", "G-Helper and similar ASUS laptop fan controllers."),
        Thermal("ghelper", "G-Helper laptop fan and power control."),
        Thermal("razersynapseservice", "Razer Synapse, which owns fan control on Blade laptops."),
        Thermal("razer synapse 3", "Razer Synapse 3."),
        Thermal("throttlestop", "ThrottleStop controls turbo and thermal limits."),
        Thermal("xtu", "Intel Extreme Tuning Utility."),
        Thermal("ryzenadj", "RyzenAdj sets APU power and thermal limits."),
        Thermal("universalx86tuningutility", "Universal x86 Tuning Utility power and thermal control."),
        Thermal("handheldcompanion", "HandheldCompanion sets TDP and fan behaviour on handhelds."),

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
        // Every process named by AntiCheatDetector is added automatically below, so the two
        // lists cannot drift apart.
        AntiCheat("eaanticheat.gameservicelauncher", "EA Javelin launcher."),

        // ---- Remote play and remote desktop.
        //
        // The worst lockout GamerGod could cause. Somebody playing from another room or
        // another house has no keyboard at the machine: suspending the host that carries
        // their screen and input takes away the desktop, the panic hotkey, the controller
        // combo and the ability to see anything at all - all four documented escape paths at
        // once. The only remaining recovery is physically walking to the machine.
        Input("parsecd", "Parsec host. Suspending it ends the session with no way back in."),
        Input("parsec", "Parsec."),
        Input("sunshine", "Sunshine streaming host, used by Moonlight clients."),
        Input("nvstreamer", "NVIDIA GameStream host."),
        Input("nvcontainer", "NVIDIA container hosting streaming and driver services."),
        Input("steam_streaming_host", "Steam Remote Play host."),
        Input("streaming_client", "Steam Remote Play client host."),
        Input("rdpclip", "Remote Desktop clipboard host."),
        Input("termsrv", "Remote Desktop Services host."),
        Input("teamviewer", "TeamViewer remote session."),
        Input("teamviewer_service", "TeamViewer service."),
        Input("anydesk", "AnyDesk remote session."),
        Input("rustdesk", "RustDesk remote session."),
        Input("remote_assistance_host", "Chrome Remote Desktop host."),
        Input("moonlight", "Moonlight client."),

        // ---- VR runtimes.
        //
        // A headset is on the user's face. Suspending a compositor freezes the image inside
        // it, which is disorienting at best and genuinely unpleasant at worst - and the user
        // cannot see a screen to fix it. VR compositors are also latency-critical, so they
        // belong on the game's domain rather than being demoted.
        Input("vrserver", "SteamVR runtime. Suspending it freezes the image inside a headset."),
        Input("vrcompositor", "SteamVR compositor. Freezing this freezes what the user sees."),
        Input("vrmonitor", "SteamVR monitor."),
        Input("vrdashboard", "SteamVR dashboard."),
        Input("ovrserver_x64", "Oculus runtime."),
        Input("oculusclient", "Oculus client."),
        Input("mixedrealityportal", "Windows Mixed Reality portal."),
        Input("virtualdesktop.streamer", "Virtual Desktop streamer, which carries the headset image."),
        Input("alvr_dashboard", "ALVR streaming host."),
        Input("wivrn", "WiVRn streaming host."),

        // ---- Accessibility.
        //
        // For some users this software is the only way to operate the machine at all.
        // Suspending it does not inconvenience them, it locks them out, and no amount of
        // performance is worth that.
        Input("narrator", "Windows Narrator. For some users this is the only output."),
        Input("magnify", "Windows Magnifier."),
        Input("osk", "On-screen keyboard. May be the only input available."),
        Input("nvda", "NVDA screen reader."),
        Input("jfw", "JAWS screen reader."),
        Input("dragonbar", "Dragon NaturallySpeaking voice control."),
        Input("natspeak", "Dragon NaturallySpeaking engine."),
        Input("tobiigameHub", "Tobii eye tracking."),
        Input("tobii.eyex.engine", "Tobii EyeX engine."),
        Input("eyewarecore", "Eyeware head and eye tracking."),
        Input("voiceattack", "VoiceAttack voice control."),

        // ---- Communication. Suspending these drops the user mid-match.
        Communication("discord", "Discord. Suspending it drops an active voice call."),
        Communication("teamspeak", "TeamSpeak."),
        Communication("mumble", "Mumble."),
        Communication("ts3client_win64", "TeamSpeak 3."),

        // ---- GamerGod itself.
        Recovery("gmsvc", "GamerGod service. Suspending it disables crash recovery."),
        Recovery("gmagent", "GamerGod session agent. It owns the panic hotkey."),
        Recovery("gamergod", "GamerGod."),
    ];

    /// <summary>
    /// Every anti-cheat process the detector knows about, derived rather than duplicated so
    /// the two lists cannot fall out of step as vendors are added.
    /// </summary>
    private static readonly ImmutableArray<ProcessProtection> DerivedAntiCheat =
        Policy.AntiCheatDetector.BuiltInSignatures
            .Where(s => s.Tier == Policy.AntiCheatTier.Kernel)
            .SelectMany(s => s.Processes.Select(name => new ProcessProtection(
                StripExtension(name),
                ProcessRisk.AntiCheat,
                $"{s.Vendor}. Suspending it breaks every game that uses it.")))
            .DistinctBy(p => p.Process, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    /// <summary>Everything protected: the explicit list plus every detector-known anti-cheat.</summary>
    public static ImmutableArray<ProcessProtection> All { get; } =
        Protected
            .Concat(DerivedAntiCheat)
            .DistinctBy(p => p.Process, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

    private static readonly ImmutableDictionary<string, ProcessProtection> Index =
        All.ToImmutableDictionary(p => p.Process, StringComparer.OrdinalIgnoreCase);

    private static string StripExtension(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

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
