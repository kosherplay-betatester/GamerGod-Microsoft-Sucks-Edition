using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Safety;

/// <summary>
/// The worst outcome GamerGod could produce is not a lost frame — it is a user who cannot
/// reach their own machine. These cover the software whose suspension removes every escape
/// path at once.
/// </summary>
public sealed class LockoutRegressionTests
{
    public static TheoryData<string, string> RemoteSessionHosts() => new()
    {
        { "parsecd.exe", "Parsec host" },
        { "sunshine.exe", "Sunshine, used by Moonlight clients" },
        { "nvstreamer.exe", "NVIDIA GameStream" },
        { "steam_streaming_host.exe", "Steam Remote Play" },
        { "rdpclip.exe", "Remote Desktop" },
        { "TeamViewer.exe", "TeamViewer" },
        { "AnyDesk.exe", "AnyDesk" },
        { "RustDesk.exe", "RustDesk" },
    };

    [Theory]
    [MemberData(nameof(RemoteSessionHosts))]
    public void A_remote_session_host_can_never_be_suspended(string process, string what)
    {
        // Somebody playing from another room has no keyboard at the machine. Suspending the
        // host that carries their screen and input removes the desktop, the panic hotkey,
        // the controller combo and the ability to see anything - all four documented escape
        // paths simultaneously. The only recovery left is walking to the machine.
        Assert.True(
            ProcessSafetyPolicy.IsProtected(process),
            $"{what} is suspendable, which would strand a remote user completely.");
    }

    public static TheoryData<string, string> VrRuntimes() => new()
    {
        { "vrserver.exe", "SteamVR runtime" },
        { "vrcompositor.exe", "SteamVR compositor" },
        { "OVRServer_x64.exe", "Oculus runtime" },
        { "VirtualDesktop.Streamer.exe", "Virtual Desktop streamer" },
        { "ALVR_Dashboard.exe", "ALVR" },
    };

    [Theory]
    [MemberData(nameof(VrRuntimes))]
    public void A_vr_runtime_can_never_be_suspended(string process, string what)
    {
        // The headset is on the user's face and they cannot see a screen to fix it.
        Assert.True(
            ProcessSafetyPolicy.IsProtected(process),
            $"{what} is suspendable, which would freeze the image inside a headset.");
    }

    public static TheoryData<string, string> AccessibilitySoftware() => new()
    {
        { "Narrator.exe", "Windows Narrator" },
        { "Magnify.exe", "Windows Magnifier" },
        { "osk.exe", "On-screen keyboard" },
        { "nvda.exe", "NVDA screen reader" },
        { "jfw.exe", "JAWS screen reader" },
        { "natspeak.exe", "Dragon NaturallySpeaking" },
        { "VoiceAttack.exe", "VoiceAttack" },
    };

    [Theory]
    [MemberData(nameof(AccessibilitySoftware))]
    public void Accessibility_software_can_never_be_suspended(string process, string what)
    {
        // For some users this is the only way to operate the machine. Suspending it does not
        // inconvenience them, it locks them out, and no amount of performance justifies that.
        Assert.True(
            ProcessSafetyPolicy.IsProtected(process),
            $"{what} is suspendable, which could lock a user out of their own machine.");
    }
}

/// <summary>
/// The safety lists are derived from the anti-cheat catalogue rather than duplicated, so
/// adding a vendor in one place can never leave a gap in the other.
/// </summary>
public sealed class DerivedSafetyListTests
{
    [Fact]
    public void Every_kernel_anticheat_service_is_protected_from_being_stopped()
    {
        // An audit found seven products the detector classified as kernel anti-cheat whose
        // services this list left stoppable. GamerGod would correctly identify a title as
        // protected, then stop the service protecting it.
        foreach (var signature in AntiCheatDetector.BuiltInSignatures
                     .Where(s => s.Tier == AntiCheatTier.Kernel))
        {
            foreach (var service in signature.Services)
            {
                var protection = ServiceSafetyPolicy.Explain(service);

                Assert.True(
                    protection is not null,
                    $"{signature.Vendor} service '{service}' is detected as kernel anti-cheat "
                    + "but is not protected from being stopped.");
                Assert.Equal(ProtectionReason.AntiCheat, protection!.Reason);
            }
        }
    }

    [Fact]
    public void Every_kernel_anticheat_process_is_protected_from_being_suspended()
    {
        foreach (var signature in AntiCheatDetector.BuiltInSignatures
                     .Where(s => s.Tier == AntiCheatTier.Kernel))
        {
            foreach (var process in signature.Processes)
            {
                Assert.True(
                    ProcessSafetyPolicy.IsProtected(process),
                    $"{signature.Vendor} process '{process}' is detected as kernel anti-cheat "
                    + "but is not protected from being suspended.");
            }
        }
    }

    [Theory]
    [InlineData("faceit")]
    [InlineData("ricochet")]
    [InlineData("mhyprot2")]
    [InlineData("mhyprot3")]
    [InlineData("aceammo")]
    [InlineData("acegame")]
    [InlineData("denuvo")]
    public void The_seven_services_the_audit_found_unprotected_are_now_protected(string service)
    {
        Assert.True(ServiceSafetyPolicy.IsProtected(service));
    }
}

/// <summary>
/// A contact grant is the token that unlocks touching a game. These prove it cannot be
/// obtained, forged, or relocated onto a protected title.
/// </summary>
public sealed class ContactGrantIntegrityTests
{
    private static AntiCheatAssessment Kernel() => new()
    {
        Tier = AntiCheatTier.Kernel,
        Findings = [new AntiCheatFinding("Riot Vanguard", AntiCheatTier.Kernel, "driver 'vgk.sys'")],
    };

    [Fact]
    public void A_grant_cannot_be_moved_onto_a_protected_title()
    {
        // The defect: MutationPermit.Contact had a public init setter, so ordinary record
        // syntax could move a grant issued for a safe title onto a kernel-protected one,
        // never touching the guarded factory.
        var safe = GameIntegrityPolicy.Evaluate(
            "PCSX2",
            new AntiCheatAssessment { Tier = AntiCheatTier.None, Findings = [] },
            ContactPreference.WhenProvablySafe);

        Assert.NotNull(safe.Contact);

        var protectedTitle = GameIntegrityPolicy.Evaluate("Valorant", Kernel());

        // Even given a real grant, the permit refuses contact because its own assessment
        // says the title is protected. Both conditions are required, every time.
        var forged = protectedTitle with { Contact = safe.Contact };

        Assert.False(forged.Allows(new ContactMutation()));
        Assert.True(forged.Filter([new ContactMutation()]).IsEmpty);
    }

    [Fact]
    public void The_contact_property_cannot_be_set_from_outside_the_assembly()
    {
        var setter = typeof(MutationPermit).GetProperty(nameof(MutationPermit.Contact))!.SetMethod!;

        Assert.False(setter.IsPublic, "MutationPermit.Contact has a public setter.");
    }

    [Fact]
    public void A_hand_built_assessment_cannot_claim_safety_while_naming_a_kernel_product()
    {
        // Tier is data and can be set to anything. The findings are the evidence, and where
        // they contradict the declared tier, the evidence wins.
        var contradictory = new AntiCheatAssessment
        {
            Tier = AntiCheatTier.None,
            Findings = [new AntiCheatFinding("Riot Vanguard", AntiCheatTier.Kernel, "driver 'vgk.sys'")],
        };

        var permit = GameIntegrityPolicy.Evaluate(
            "Valorant", contradictory, ContactPreference.WhenProvablySafe);

        Assert.True(permit.IsAmbientOnly);
        Assert.Null(permit.Contact);
    }

    [Fact]
    public void A_weak_finding_does_not_contradict_a_genuinely_safe_assessment()
    {
        // Only a conclusive kernel finding overrides the declared tier. A reported-but-weak
        // marker must not block a title that is genuinely safe.
        var assessment = new AntiCheatAssessment
        {
            Tier = AntiCheatTier.None,
            Findings = [new AntiCheatFinding("Valve Anti-Cheat", AntiCheatTier.UserMode, "file", false)],
        };

        var permit = GameIntegrityPolicy.Evaluate(
            "Some Emulator", assessment, ContactPreference.WhenProvablySafe);

        Assert.False(permit.IsAmbientOnly);
    }

    private sealed class ContactMutation : IMutation
    {
        public string Key => "cpuset:game";

        public MutationTier Tier => MutationTier.CpuRouting;

        public MutationVisibility Visibility => MutationVisibility.Contact;

        public bool IsBootPersistent => false;

        public string Describe() => Key;

        public ValueTask<System.Text.Json.JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

        public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RevertAsync(System.Text.Json.JsonElement capture, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
