using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Windows;

namespace GamerGod.Cli;

/// <summary>
/// Wires the engine to the command line. This is where GamerGod first changes a machine.
/// </summary>
internal static class SessionCommands
{
    private static string StateRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GamerGod");

    private static string JournalPath => Path.Combine(StateRoot, "state", "session.journal");

    public static async Task<int> OnAsync(bool dryRun)
    {
        var operations = new WindowsAmbientOperations();
        var topology = new WindowsTopologyProvider().Classify();
        var journal = new FileJournal(JournalPath);
        var ledger = new MutationLedger(journal, new AmbientMutationResolver(operations, topology));
        var engine = new AmbientEngine(operations, ledger);

        if (await ledger.HasOutstandingChangesAsync())
        {
            Console.Error.WriteLine("  GamerGod is already on. Run 'gamergod off' first.");
            return 4;
        }

        // No game is running yet, so there is no title to assess and nothing to opt in. The
        // permit is ambient-only by construction, which is exactly what this engine emits.
        var permit = GameIntegrityPolicy.Evaluate(
            "desktop session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

        var options = new AmbientOptions { DryRun = dryRun };

        var receipt = await engine.EnterAsync(
            sessionId: Guid.NewGuid().ToString("N"),
            topology,
            permit,
            options,
            await ReadRestoreStatusAsync());

        Console.WriteLine();
        Accent(dryRun ? "  Dry run - nothing was changed" : "  Game Mode on", ConsoleColor.Green);
        Console.WriteLine();
        Console.WriteLine($"  {receipt.Explain()}");
        Console.WriteLine();

        if (receipt.Partitioned)
        {
            var cache = topology.GameDomain.LastLevelCacheBytes / (1024 * 1024);
            Console.WriteLine(
                $"  Your games {(dryRun ? "would get" : "get")} D{topology.GameDomain.Id} — "
                + $"{topology.GameDomain.LogicalProcessorCount} threads and {cache} MB of cache, "
                + "to themselves.");
            Console.WriteLine();
        }

        if (!receipt.Failed.IsEmpty)
        {
            Accent("  Some changes did not apply:", ConsoleColor.Yellow);
            Console.WriteLine();
            foreach (var (key, error) in receipt.Failed)
            {
                Console.WriteLine($"    {key}: {error}");
            }

            Console.WriteLine();
        }

        Console.WriteLine("  Run 'gamergod off' when you're done. Rebooting also undoes everything.");
        Console.WriteLine();

        return 0;
    }

    public static async Task<int> OffAsync()
    {
        var operations = new WindowsAmbientOperations();
        var topology = new WindowsTopologyProvider().Classify();
        var journal = new FileJournal(JournalPath);
        var ledger = new MutationLedger(journal, new AmbientMutationResolver(operations, topology));

        if (!await ledger.HasOutstandingChangesAsync())
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing to undo. GamerGod is not currently changing anything.");
            Console.WriteLine();
            return 0;
        }

        var report = await ledger.RevertAsync();

        Console.WriteLine();
        Accent("  Game Mode off", ConsoleColor.Green);
        Console.WriteLine();
        Console.WriteLine("  Receipt");

        foreach (var key in report.Reverted)
        {
            Console.WriteLine($"    restored  {key}");
        }

        foreach (var (key, error) in report.Failed)
        {
            Console.WriteLine($"    FAILED    {key}: {error}");
        }

        foreach (var key in report.Unresolvable)
        {
            Console.WriteLine($"    UNKNOWN   {key}");
        }

        Console.WriteLine();

        if (report.IsClean)
        {
            Console.WriteLine("  Your machine is exactly as it was.");
        }
        else
        {
            Accent("  Some changes could not be undone. Reboot to restore them.", ConsoleColor.Yellow);
        }

        Console.WriteLine();
        return report.IsClean ? 0 : 5;
    }

    public static async Task<int> StatusAsync()
    {
        var journal = new FileJournal(JournalPath);
        var ledger = new MutationLedger(journal, new NullResolver());
        var active = await ledger.HasOutstandingChangesAsync();

        Console.WriteLine();

        if (active)
        {
            Accent("  Game Mode is ON", ConsoleColor.Green);
            Console.WriteLine();

            var entries = await journal.ReadAllAsync(default);
            var outstanding = entries
                .Where(e => e.Op == JournalOp.Capture)
                .Select(e => e.Description ?? e.Key)
                .Distinct(StringComparer.Ordinal);

            foreach (var description in outstanding)
            {
                Console.WriteLine($"    {description}");
            }

            Console.WriteLine();
            Console.WriteLine("  Run 'gamergod off' to undo all of it.");
        }
        else
        {
            Console.WriteLine("  Game Mode is off. GamerGod is not changing anything.");
        }

        Console.WriteLine();
        return 0;
    }

    private static ValueTask<RestoreStatus> ReadRestoreStatusAsync()
    {
        // Nothing this engine emits outlives a reboot, so the safety gate never asks for a
        // restore point today. Reported honestly rather than claimed.
        return ValueTask.FromResult(new RestoreStatus
        {
            Availability = RestoreAvailability.Unknown,
            Detail = "not required: no change in this session survives a reboot",
        });
    }

    private static void Accent(string text, ConsoleColor colour)
    {
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    private sealed class NullResolver : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key) => null;
    }

    /// <summary>
    /// Rebuilds ambient mutations from a journal so a cold process — one that never applied
    /// anything — can still undo it.
    /// </summary>
    private sealed class AmbientMutationResolver(IAmbientOperations os, CpuTopology topology) : IMutationResolver
    {
        public IMutation? Resolve(string mutationType, string key)
        {
            if (key.StartsWith("service:", StringComparison.Ordinal))
            {
                return new ServiceSuspensionMutation(os, key["service:".Length..]);
            }

            return key switch
            {
                "ecoqos:background" => new EfficiencyModeMutation(os, ImmutableArray<ProcessInfo>.Empty),
                "power:scheme" => new PowerSchemeMutation(os, "GamerGod"),

                // Affinity outlives the process that set it, so unlike a job object it has
                // to be put back deliberately. The captured masks come from the journal.
                "affinity:ambient-domain" => new AffinityConfinementMutation(
                    os, default, ImmutableArray<ProcessInfo>.Empty, topology),

                // A confinement held by a process that has since exited is already lifted by
                // Windows, so a cold revert has nothing to do and should say so rather than
                // reporting a failure.
                "confine:ambient-domain" => new ReleasedConfinement(),
                _ => null,
            };
        }
    }

    private sealed class ReleasedConfinement : IMutation
    {
        public string Key => "confine:ambient-domain";

        public MutationTier Tier => MutationTier.ProcessDemotion;

        public MutationVisibility Visibility => MutationVisibility.Ambient;

        public bool IsBootPersistent => false;

        public string Describe() => "release background confinement";

        public ValueTask<System.Text.Json.JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(System.Text.Json.JsonDocument.Parse("{}").RootElement);

        public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask RevertAsync(
            System.Text.Json.JsonElement capture, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
