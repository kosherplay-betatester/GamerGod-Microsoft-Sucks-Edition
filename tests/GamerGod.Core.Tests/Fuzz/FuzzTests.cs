using System.Collections.Immutable;
using GamerGod.Core.Diagnostics;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Fuzz;

/// <summary>
/// Randomised input across every entry point that reads data GamerGod did not create.
///
/// <para>
/// Fixture tests prove the cases we thought of. These prove the ones we did not: a firmware
/// revision reporting an unfamiliar cache layout, a journal truncated by a power cut, a
/// machine with hardware nobody on the project owns. The bar is not that the answer is
/// clever — it is that nothing throws, nothing silently loses a processor, and no unsafe
/// plan escapes.
/// </para>
///
/// <para>All seeds are fixed, so any failure reproduces exactly.</para>
/// </summary>
public sealed class TopologyFuzzTests
{
    private const int Trials = 3000;

    private static ProcessorSnapshot RandomSnapshot(Random random)
    {
        var coreCount = random.Next(1, 33);
        var threadsPerCore = random.Next(1, 3);
        var classCount = random.Next(1, 4);

        var cores = ImmutableArray.CreateBuilder<PhysicalCore>();
        var nextLp = 0;

        for (var c = 0; c < coreCount && nextLp + threadsPerCore <= 64; c++)
        {
            var lps = new int[threadsPerCore];
            for (var t = 0; t < threadsPerCore; t++)
            {
                lps[t] = nextLp++;
            }

            cores.Add(new PhysicalCore(
                ProcessorMask.FromLogicalProcessors(0, lps),
                (byte)random.Next(0, classCount)));
        }

        if (cores.Count == 0)
        {
            cores.Add(new PhysicalCore(ProcessorMask.FromLogicalProcessors(0, 0), 0));
            nextLp = 1;
        }

        var caches = ImmutableArray.CreateBuilder<CacheInfo>();
        var cacheCount = random.Next(0, 4);
        var used = 0;

        for (var i = 0; i < cacheCount && used < nextLp; i++)
        {
            var span = random.Next(1, nextLp - used + 1);
            caches.Add(new CacheInfo(
                (byte)random.Next(3, 5),
                random.Next(1, 129) * 1024L * 1024L,
                ProcessorMask.Range(0, used, used + span - 1)));
            used += span;
        }

        // Occasionally emit a cache that covers nothing, or overlaps, as firmware sometimes
        // does after a BIOS update.
        if (random.Next(6) == 0)
        {
            caches.Add(new CacheInfo(3, 0, default));
        }

        var preference = Enumerable.Range(0, nextLp)
            .OrderBy(_ => random.Next())
            .Take(random.Next(0, nextLp + 1))
            .ToImmutableArray();

        return new ProcessorSnapshot
        {
            ProcessorName = $"Fuzz CPU {random.Next(1000)}",
            MaxFrequencyMhz = random.Next(0, 6000),
            Cores = cores.ToImmutable(),
            Caches = caches.ToImmutable(),
            PreferredCoreOrder = preference,
        };
    }

    [Fact]
    public void Classification_never_throws_and_never_loses_a_processor()
    {
        var random = new Random(1337);

        for (var trial = 0; trial < Trials; trial++)
        {
            var snapshot = RandomSnapshot(random);
            CpuTopology topology;

            try
            {
                topology = PerformanceDomainClassifier.Classify(snapshot);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Trial {trial} threw {ex.GetType().Name}: {ex.Message}");
                return;
            }

            var expected = snapshot.Cores
                .SelectMany(c => c.Processors.LogicalProcessors)
                .OrderBy(x => x)
                .ToArray();

            var actual = topology.Domains
                .SelectMany(d => d.LogicalProcessors)
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(expected, actual);
            Assert.Equal(actual.Length, actual.Distinct().Count());
            Assert.NotEmpty(topology.Domains);
        }
    }

    [Fact]
    public void Every_classified_topology_produces_a_safe_routing_plan()
    {
        // The property that matters most: no random hardware shape may yield a plan that
        // would strand Windows on too few processors.
        var random = new Random(24680);

        for (var trial = 0; trial < Trials; trial++)
        {
            var topology = PerformanceDomainClassifier.Classify(RandomSnapshot(random));
            var result = RoutingSafety.ValidateTopologyPlan(topology);

            Assert.True(
                result.IsSafe,
                $"Trial {trial} on '{topology.ProcessorName}' ({topology.Kind}, "
                + $"{topology.Domains.Length} domains) produced an unsafe plan: {result.Explain()}");
        }
    }

    [Fact]
    public void The_game_domain_is_always_a_real_domain_of_this_machine()
    {
        var random = new Random(97531);

        for (var trial = 0; trial < Trials; trial++)
        {
            var topology = PerformanceDomainClassifier.Classify(RandomSnapshot(random));

            Assert.Contains(topology.GameDomain, topology.Domains);
            Assert.DoesNotContain(topology.GameDomain, topology.AmbientDomains);
            Assert.Equal(topology.Domains.Length - 1, topology.AmbientDomains.Length);
            Assert.NotEmpty(topology.GameDomain.LogicalProcessors);
            Assert.Equal(DomainClass.Performance, topology.GameDomain.Class);

            // The summary is user-facing text built from this data; it must never fail.
            Assert.False(string.IsNullOrWhiteSpace(topology.Summary()));
        }
    }

    [Fact]
    public void Classification_is_deterministic()
    {
        var random = new Random(555);

        for (var trial = 0; trial < 500; trial++)
        {
            var snapshot = RandomSnapshot(random);

            var first = PerformanceDomainClassifier.Classify(snapshot);
            var second = PerformanceDomainClassifier.Classify(snapshot);

            Assert.Equal(first.Kind, second.Kind);
            Assert.Equal(first.GameDomain.Id, second.GameDomain.Id);
            Assert.Equal(
                first.Domains.Select(d => d.Processors.Mask),
                second.Domains.Select(d => d.Processors.Mask));
        }
    }
}

public sealed class PolicyFuzzTests
{
    /// <summary>
    /// The single most important property in the project: if anything that looks like
    /// kernel anti-cheat is present, contact is refused. No random combination of noise may
    /// produce a grant.
    /// </summary>
    [Fact]
    public void Contact_is_never_granted_when_any_kernel_signature_is_present()
    {
        var random = new Random(4242);
        var kernelSignatures = AntiCheatDetector.BuiltInSignatures
            .Where(s => s.Tier == AntiCheatTier.Kernel)
            .ToArray();

        var noise = new[]
        {
            "WSearch", "SysMain", "chrome.exe", "steam.exe", "nvlddmkm.sys",
            "amd3dvcache.sys", "game.exe", "steam_api64.dll", "d3d12.dll",
        };

        for (var trial = 0; trial < 5000; trial++)
        {
            var signature = kernelSignatures[random.Next(kernelSignatures.Length)];

            var services = new List<string>();
            var drivers = new List<string>();
            var processes = new List<string>();
            var files = new List<string>();

            // Plant exactly one genuine marker, in a random slot, buried in noise.
            var slot = random.Next(4);
            if (slot == 0 && !signature.Services.IsDefaultOrEmpty)
            {
                services.Add(signature.Services[random.Next(signature.Services.Length)]);
            }
            else if (slot == 1 && !signature.Drivers.IsDefaultOrEmpty)
            {
                drivers.Add(signature.Drivers[random.Next(signature.Drivers.Length)]);
            }
            else if (slot == 2 && !signature.Processes.IsDefaultOrEmpty)
            {
                processes.Add(signature.Processes[random.Next(signature.Processes.Length)]);
            }
            else if (!signature.FileMarkers.IsDefaultOrEmpty)
            {
                files.Add(signature.FileMarkers[random.Next(signature.FileMarkers.Length)]);
            }
            else
            {
                continue;
            }

            for (var n = random.Next(0, 8); n > 0; n--)
            {
                services.Add(noise[random.Next(noise.Length)]);
                files.Add(noise[random.Next(noise.Length)]);
            }

            var environment = new AntiCheatEnvironment
            {
                InstalledServices = [.. services],
                LoadedDrivers = [.. drivers],
                RunningProcesses = [.. processes],
                GameDirectoryEntries = [.. files],
            };

            var permit = GameIntegrityPolicy.Evaluate(
                "Fuzzed Title", environment, ContactPreference.WhenProvablySafe);

            Assert.True(
                permit.IsAmbientOnly,
                $"Trial {trial}: {signature.Vendor} marker present but contact was granted.");
            Assert.Equal(AntiCheatTier.Kernel, permit.Assessment.Tier);
        }
    }

    [Fact]
    public void Case_and_whitespace_never_defeat_detection()
    {
        foreach (var signature in AntiCheatDetector.BuiltInSignatures
                     .Where(s => s.Tier == AntiCheatTier.Kernel))
        {
            if (signature.Services.IsDefaultOrEmpty)
            {
                continue;
            }

            foreach (var form in new[]
                     {
                         signature.Services[0].ToUpperInvariant(),
                         signature.Services[0].ToLowerInvariant(),
                         $"  {signature.Services[0]}  ",
                     })
            {
                var permit = GameIntegrityPolicy.Evaluate(
                    signature.Vendor,
                    new AntiCheatEnvironment { InstalledServices = [form] },
                    ContactPreference.WhenProvablySafe);

                Assert.True(permit.IsAmbientOnly, $"'{form}' defeated {signature.Vendor} detection.");
            }
        }
    }

    [Fact]
    public void An_environment_of_pure_noise_is_treated_as_protected()
    {
        var random = new Random(8080);

        for (var trial = 0; trial < 2000; trial++)
        {
            var junk = Enumerable.Range(0, random.Next(0, 20))
                .Select(_ => $"svc{random.Next(100000)}")
                .ToImmutableArray();

            var permit = GameIntegrityPolicy.Evaluate(
                "Unknown",
                new AntiCheatEnvironment { InstalledServices = junk, GameDirectoryEntries = junk },
                ContactPreference.WhenProvablySafe);

            // Unknown must behave exactly as strictly as Kernel. Absence of evidence is
            // never evidence of absence here.
            Assert.True(permit.IsAmbientOnly);
        }
    }

    [Fact]
    public void Safety_lists_never_throw_on_hostile_input()
    {
        var random = new Random(31337);
        var chars = "abcXYZ019 _-.\\/\"'\t\r\n\0éüñ日本語".ToCharArray();

        for (var trial = 0; trial < 5000; trial++)
        {
            var text = new string(
                Enumerable.Range(0, random.Next(0, 40))
                    .Select(_ => chars[random.Next(chars.Length)])
                    .ToArray());

            // No input from the operating system may crash a safety check. A safety check
            // that throws is a safety check that gets wrapped in a catch and skipped.
            _ = ServiceSafetyPolicy.IsProtected(text);
            _ = ProcessSafetyPolicy.IsProtected(text);
            _ = Core.Workloads.EmulatorCatalog.Identify(text);
        }
    }
}

public sealed class JournalResilienceTests
{
    [Fact]
    public async Task A_journal_truncated_by_a_power_cut_still_recovers_what_it_can()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gamergod-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.journal");

        try
        {
            var journal = new FileJournal(path);

            await journal.AppendAsync(
                new JournalEntry { Op = JournalOp.SessionBegin, SessionId = "s1" }, default);
            await journal.AppendAsync(
                new JournalEntry
                {
                    Op = JournalOp.Capture,
                    SessionId = "s1",
                    Key = "service:WSearch",
                    MutationType = "Stub",
                    State = """{"Previous":"Running"}""",
                },
                default);

            // Simulate a torn final write: append a half-formed line, as a machine losing
            // power mid-flush would leave behind.
            await File.AppendAllTextAsync(path, "{\"Op\":\"Cap");

            var entries = await journal.ReadAllAsync(default);

            Assert.Equal(2, entries.Length);
            Assert.Equal(JournalOp.Capture, entries[1].Op);
            Assert.Equal("service:WSearch", entries[1].Key);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Garbage_lines_are_skipped_rather_than_aborting_recovery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gamergod-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.journal");

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllLinesAsync(path,
            [
                "not json at all",
                """{"Op":"Capture","SessionId":"s1","Key":"a","MutationType":"Stub","State":"{}"}""",
                "",
                "%%%%",
                """{"Op":"Reverted","SessionId":"s1","Key":"a"}""",
                "{ unterminated",
            ]);

            var entries = await new FileJournal(path).ReadAllAsync(default);

            // Both valid lines survive; the noise between them is ignored. Refusing to read
            // a journal because one line is damaged would strand a machine.
            Assert.Equal(2, entries.Length);
            Assert.Equal(JournalOp.Capture, entries[0].Op);
            Assert.Equal(JournalOp.Reverted, entries[1].Op);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_missing_journal_is_an_empty_journal_not_an_error()
    {
        var path = Path.Combine(Path.GetTempPath(), "gamergod-tests", Guid.NewGuid().ToString("N"), "none.journal");

        Assert.Empty(await new FileJournal(path).ReadAllAsync(default));
    }

    [Fact]
    public async Task Concurrent_writers_never_corrupt_a_line()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gamergod-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "session.journal");

        try
        {
            var journal = new FileJournal(path);

            // The service and a recovery pass can both write. Interleaved partial lines
            // would make the journal unreadable exactly when it is needed.
            await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
                journal.AppendAsync(
                    new JournalEntry
                    {
                        Op = JournalOp.Capture,
                        SessionId = "s1",
                        Key = $"key{i}",
                        MutationType = "Stub",
                        State = """{"Previous":"x"}""",
                    },
                    default).AsTask()));

            var entries = await journal.ReadAllAsync(default);

            Assert.Equal(40, entries.Length);
            Assert.Equal(40, entries.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}

public sealed class HazardScannerFuzzTests
{
    [Fact]
    public void The_scanner_never_throws_on_arbitrary_machine_data()
    {
        var random = new Random(6543);
        var words = new[] { "", " ", "Display", "Media", "OK", "Error", "Disabled", "\\??\\C:\\x.sys", "日本語", "\0" };

        for (var trial = 0; trial < 3000; trial++)
        {
            string Word() => words[random.Next(words.Length)];

            var snapshot = new MachineSnapshot
            {
                Devices = [.. Enumerable.Range(0, random.Next(0, 12))
                    .Select(_ => new DeviceEntry(Word(), Word(), Word(), Word()))],
                KernelDrivers = [.. Enumerable.Range(0, random.Next(0, 12))
                    .Select(_ => new KernelDriverEntry(Word(), Word(), random.Next(2) == 0))],
                NetworkAdapters = [.. Enumerable.Range(0, random.Next(0, 6))
                    .Select(_ => new NetworkAdapterEntry(
                        Word(), Word(), random.Next(2) == 0, random.Next(2) == 0,
                        random.Next(2) == 0, random.Next(0, 1_000_000)))],
                InstalledServices = [.. Enumerable.Range(0, random.Next(0, 12)).Select(_ => Word())],
                PhysicalMemoryGigabytes = random.Next(0, 256),
                PerformanceDomainCount = random.Next(0, 5),
            };

            var hazards = HazardScanner.Scan(snapshot);

            // Findings are addressed to a nervous user on first launch, so the contract is
            // strict: unique ids, ordered by severity, and anything actionable says what to do.
            var ids = hazards.Select(x => x.Id).ToArray();
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());

            for (var i = 1; i < hazards.Length; i++)
            {
                Assert.True(hazards[i - 1].Severity >= hazards[i].Severity);
            }

            Assert.All(hazards.Where(x => x.Severity >= HazardSeverity.Low),
                x => Assert.False(string.IsNullOrWhiteSpace(x.Remedy)));
        }
    }
}
