using System.Reflection;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Charter;

/// <summary>
/// Locates the repository so the source tree itself can be inspected.
/// </summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    public static IEnumerable<(string Path, string[] Lines)> ProductionSources()
    {
        var src = Path.Combine(Root, "src");

        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            // Generated output is not authored code and is rebuilt from what is.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (Path.GetRelativePath(Root, file), File.ReadAllLines(file));
        }
    }

    /// <summary>
    /// Lines that are actually code. Comment lines are excluded so that documenting a
    /// prohibition does not itself trip the check — <c>NativeMethods.cs</c> deliberately
    /// lists the banned APIs in prose so a reviewer can see the whole set in one place.
    /// </summary>
    public static IEnumerable<(int Number, string Text)> CodeLines(string[] lines)
    {
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            if (inBlockComment)
            {
                if (trimmed.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = false;
                }

                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                if (!trimmed.Contains("*/", StringComparison.Ordinal))
                {
                    inBlockComment = true;
                }

                continue;
            }

            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }

            yield return (i + 1, lines[i]);
        }
    }

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CHARTER.md")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the repository root. The Charter compliance tests inspect the "
            + "source tree and cannot run without it.");
    }
}

/// <summary>
/// One test per Charter article, so the promises in <c>CHARTER.md</c> fail the build rather
/// than merely embarrassing us.
/// </summary>
public sealed class CharterComplianceTests
{
    // ------------------------------------------------------------------
    // Article II — never ship a kernel driver.
    // Article III — never inject into, hook, or modify a game.
    // ------------------------------------------------------------------

    // The match is a case-insensitive substring, so a bare name also catches its suffixed and
    // decorated forms: "SetWindowsHookEx" covers SetWindowsHookExW/A, and "DebugActiveProcess"
    // covers DebugActiveProcessStop. Entries are therefore written in their shortest unambiguous
    // form, and the Nt/Zw spellings of the same syscall are listed separately because neither is
    // a substring of the other.
    //
    // Deliberately NOT banned, so that nobody "completes the set" later and forces the next
    // contributor to suppress a check to do legitimate work:
    //   OpenProcess          — required for CPU Set assignment. The control is the access mask,
    //                          and PROCESS_ALL_ACCESS / PROCESS_VM_* are banned below.
    //   MapViewOfFile        — maps into our OWN address space; it is how we read PresentMon's
    //                          shared frametime buffer. Only the ...OfSection forms, which can
    //                          target another process, are prohibited.
    //   VirtualProtect       — the bare form operates on the calling process and the runtime
    //                          itself uses it. Only the remote Ex form is an injection step.
    //   GetForegroundWindow  — non-injecting and in-process. This is the sanctioned way for the
    //                          overlay to notice the focused window changed; banning it would
    //                          leave that requirement with no legal implementation at all.
    public static TheoryData<string, string> BannedApis() => new()
    {
        { "WriteProcessMemory", "Article III: modifying another process's memory." },
        { "ReadProcessMemory", "Article III: reading another process's memory." },
        { "CreateRemoteThread", "Article III: executing code inside another process." },
        { "NtCreateThreadEx", "Article III: executing code inside another process." },

        // The ntdll route to CreateRemoteThread. Banning only the Win32 name would leave the
        // identical capability available one layer down.
        { "RtlCreateUserThread", "Article III: executing code inside another process." },

        { "VirtualAllocEx", "Article III: allocating memory inside another process." },

        // Changing page protection in another process is the step immediately before writing to
        // it, and the usual way a patcher makes a code page writable.
        { "VirtualProtectEx", "Article III: changing memory protection in another process." },

        { "QueueUserAPC", "Article III: an injection primitive." },
        { "SetThreadContext", "Article III: hijacking another process's thread." },

        // Manual mapping: writing a section into another process's address space is DLL
        // injection with no LoadLibrary call to give it away.
        { "NtMapViewOfSection", "Article III: mapping an image into another process." },
        { "ZwMapViewOfSection", "Article III: mapping an image into another process." },

        // Attaching as a debugger grants read/write of the target's memory and full control of
        // its threads without a single other banned name appearing at the call site. Article III
        // is about the capability, not the spelling — and a kernel anti-cheat treats a debugger
        // attach on its protected process as a cheat outright.
        { "DebugActiveProcess", "Article III: a debugger attach is functionally injection." },

        { "SetWindowsHookEx", "Article III: hooking across process boundaries." },

        // The overlay needs to know when the foreground window changes, and SetWinEventHook is
        // the obvious API for it. With WINEVENT_INCONTEXT it maps our DLL into every process
        // that raises the event, the game included. WINEVENT_OUTOFCONTEXT does not inject — but
        // that distinction is a flag argument at one call site, not something the type system
        // or a reviewer's diff can hold. An anti-cheat sees the same registration either way.
        // Poll GetForegroundWindow from the session agent instead; it touches nothing.
        { "SetWinEventHook", "Article III: installs a cross-process hook." },

        { "PROCESS_ALL_ACCESS", "Least privilege: no handle may request full access." },
        { "PROCESS_VM_WRITE", "Article III: write access to another process's memory." },
        { "PROCESS_VM_OPERATION", "Article III: memory operations on another process." },
        { "PROCESS_CREATE_THREAD", "Article III: thread creation in another process." },
        { "NtLoadDriver", "Article II: loading a kernel driver." },
        { "ZwLoadDriver", "Article II: loading a kernel driver." },
        { "SetupCopyOEMInf", "Article II: installing a driver package." },
        { "bcdedit", "Article VI: editing the boot configuration store." },
        { "DisableRealtimeMonitoring", "Article V: disabling Defender real-time protection." },
        { "Remove-AppxPackage", "Article VI: removing Windows components is not reversible." },
        { "ChangeServiceConfig", "Article VI: service start type must never be modified." },
    };

    [Theory]
    [MemberData(nameof(BannedApis))]
    public void No_production_source_uses_a_banned_api(string api, string reason)
    {
        var offences = new List<string>();

        foreach (var (path, lines) in Repo.ProductionSources())
        {
            foreach (var (number, text) in Repo.CodeLines(lines))
            {
                if (text.Contains(api, StringComparison.OrdinalIgnoreCase))
                {
                    offences.Add($"{path}:{number}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            $"'{api}' appears in production code. {reason}\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_native_surface_is_confined_to_one_project()
    {
        // Every P/Invoke lives in GamerGod.Windows so a reviewer can audit the complete set
        // of things GamerGod can ask the operating system to do by reading one folder.
        var offences = new List<string>();

        foreach (var (path, lines) in Repo.ProductionSources())
        {
            var isNativeProject = path.Contains(
                $"GamerGod.Windows{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

            if (isNativeProject)
            {
                continue;
            }

            foreach (var (number, text) in Repo.CodeLines(lines))
            {
                if (text.Contains("DllImport", StringComparison.Ordinal)
                    || text.Contains("LibraryImport", StringComparison.Ordinal))
                {
                    offences.Add($"{path}:{number}");
                }
            }
        }

        Assert.True(
            offences.Count == 0,
            "P/Invoke found outside GamerGod.Windows:\n  " + string.Join("\n  ", offences));
    }

    [Fact]
    public void The_domain_never_reaches_the_operating_system()
    {
        // Article VI depends on this. The ledger's crash-recovery property tests only mean
        // something because Core can be driven against a simulated machine, and that is only
        // possible while Core has no path to a real one.
        var referenced = typeof(IMutation).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        Assert.DoesNotContain("GamerGod.Windows", referenced);
        Assert.DoesNotContain("System.Management", referenced);
    }

    // ------------------------------------------------------------------
    // Article I — never break a game.
    // ------------------------------------------------------------------

    [Fact]
    public void Kernel_anticheat_can_never_receive_a_contact_grant()
    {
        foreach (var signature in AntiCheatDetector.BuiltInSignatures
                     .Where(s => s.Tier == AntiCheatTier.Kernel))
        {
            foreach (var preference in Enum.GetValues<ContactPreference>())
            {
                var environment = new AntiCheatEnvironment
                {
                    InstalledServices = signature.Services.IsDefaultOrEmpty ? [] : [signature.Services[0]],
                    LoadedDrivers = signature.Drivers.IsDefaultOrEmpty ? [] : [signature.Drivers[0]],
                    RunningProcesses = signature.Processes.IsDefaultOrEmpty ? [] : [signature.Processes[0]],
                    GameDirectoryEntries = signature.FileMarkers.IsDefaultOrEmpty ? [] : [signature.FileMarkers[0]],
                };

                var permit = GameIntegrityPolicy.Evaluate(signature.Vendor, environment, preference);

                Assert.True(
                    permit.IsAmbientOnly,
                    $"{signature.Vendor} with preference {preference} was granted contact.");
            }
        }
    }

    [Fact]
    public void There_is_no_way_to_override_the_integrity_policy()
    {
        // A force flag would, in practice, be the setting that gets somebody banned. This
        // asserts none exists by inspecting the public surface rather than trusting review.
        var members = typeof(GameIntegrityPolicy)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Select(m => m.Name)
            .Concat(typeof(MutationPermit).GetProperties().Select(p => p.Name))
            .Concat(typeof(ContactPreference).GetEnumNames());

        foreach (var member in members)
        {
            foreach (var forbidden in new[] { "Force", "Override", "Bypass", "Ignore", "Unsafe", "Anyway" })
            {
                Assert.False(
                    member.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"'{member}' looks like an escape hatch around the integrity policy.");
            }
        }
    }

    [Fact]
    public void Contact_grants_cannot_be_constructed_from_outside_the_policy()
    {
        var constructors = typeof(ContactGrant).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);

        Assert.Empty(constructors);
    }

    // ------------------------------------------------------------------
    // Article V — never weaken security for frames.
    // ------------------------------------------------------------------

    [Fact]
    public void Security_services_are_permanently_protected()
    {
        foreach (var service in new[] { "WinDefend", "SecurityHealthService", "mpssvc", "wscsvc", "Sense" })
        {
            var protection = ServiceSafetyPolicy.Explain(service);

            Assert.NotNull(protection);
            Assert.Equal(ProtectionReason.Security, protection!.Reason);
        }
    }

    // ------------------------------------------------------------------
    // Article VI — nothing survives a reboot unless you said so.
    // ------------------------------------------------------------------

    [Fact]
    public void Every_mutation_must_declare_visibility_tier_and_persistence()
    {
        // The interface is the guarantee. If any of these were optional, a mutation could
        // be written that the policy engine and the safety gate could not reason about.
        foreach (var property in new[] { "Visibility", "Tier", "IsBootPersistent", "Key" })
        {
            Assert.NotNull(typeof(IMutation).GetProperty(property));
        }

        foreach (var method in new[] { "CaptureAsync", "ApplyAsync", "RevertAsync" })
        {
            Assert.NotNull(typeof(IMutation).GetMethod(method));
        }
    }

    [Fact]
    public void Boot_persistent_work_is_gated_behind_the_safety_ladder()
    {
        var persistent = new StubMutation(bootPersistent: true);

        var blocked = SafetyGate.Evaluate(
            [persistent],
            new RestoreStatus { Availability = RestoreAvailability.Disabled });

        Assert.Equal(SafetyVerdict.Blocked, blocked.Verdict);
        Assert.False(blocked.MayProceed);
    }

    // ------------------------------------------------------------------
    // Article VII — never claim an unmeasured benefit.
    // ------------------------------------------------------------------

    [Fact]
    public void No_shipped_tuning_profile_claims_to_be_verified()
    {
        Assert.All(
            Core.Workloads.EmulatorCatalog.All.Concat(Core.Workloads.EmulatorCatalog.VirtualizedGuests),
            p => Assert.False(p.Verified, $"{p.Name} ships marked as verified without evidence."));
    }

    [Fact]
    public void The_readout_is_not_advertised_as_showing_numbers_it_does_not_have()
    {
        // The settings screen said the readout showed "frames per second, your 1% low, and how
        // many hitches this session". It showed three em-dashes: MainWindow builds every
        // ReadoutSnapshot with those fields null, because nothing captures frames outside a
        // benchmark. Article VII broken where a user can see both halves at once — the promise
        // on the settings screen, the contradiction on their second monitor.
        //
        // Pinned from both ends. If somebody wires the numbers up, the snapshot stops being
        // hard-coded and this test says so, which is the moment the copy may promise them again.
        var window = Path.Combine(Repo.Root, "src", "GamerGod.Ui", "MainWindow.xaml.cs");
        var source = File.ReadAllText(window);

        var unfed = source.Contains("Fps: null", StringComparison.Ordinal)
            && source.Contains("OnePercentLowFps: null", StringComparison.Ordinal)
            && source.Contains("Hitches: null", StringComparison.Ordinal);

        if (!unfed)
        {
            return;
        }

        foreach (var file in (string[])["src/GamerGod.Ui/MainWindow.xaml", "README.md"])
        {
            var text = File.ReadAllText(Path.Combine(Repo.Root, file));

            // The promise these made, in the words they made it in. Both now say the fields read
            // as dashes, and both say why.
            Assert.False(
                text.Contains("readout in the corner: frames per second", StringComparison.OrdinalIgnoreCase)
                || text.Contains("armed state, frames per second, 1% low, hitch count", StringComparison.OrdinalIgnoreCase),
                $"{file} advertises readout numbers that nothing feeds.");

            Assert.Contains("dashes", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class StubMutation(bool bootPersistent) : IMutation
    {
        public string Key => "stub";

        public MutationTier Tier => MutationTier.Registry;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => bootPersistent;

        public string Describe() => "stub";

        public ValueTask<System.Text.Json.JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

        public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RevertAsync(System.Text.Json.JsonElement capture, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
