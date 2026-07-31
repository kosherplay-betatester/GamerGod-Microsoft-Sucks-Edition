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
    // Shared with the background service, which recovers this exact file at boot. They are
    // separate processes that never speak to each other except through it.
    private static string JournalPath => StateLayout.SessionJournal;

    /// <summary>
    /// Turns Game Mode on, with the levers the caller asked for.
    ///
    /// <para>
    /// <paramref name="options"/> is not optional decoration. The desktop application cannot
    /// write the revert journal — that directory is administrators-only on purpose — so it
    /// brokers arming through this command. For a while it did that by launching
    /// <c>gamergod on</c> with no arguments, which meant every lever the user had chosen was
    /// discarded and this method's own defaults were applied instead: unticking "move
    /// background apps off your game's cores" still confined them, and ticking "pause the
    /// search indexer" stopped nothing. The confirmation dialog listed the user's settings
    /// immediately before ignoring them.
    /// </para>
    /// </summary>
    public static async Task<int> OnAsync(bool dryRun, AmbientOptions? options = null)
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

        // DryRun is always the caller's, never the passed options', because --dry-run is a
        // property of this invocation rather than of the configuration being applied.
        var effective = (options ?? new AmbientOptions()) with { DryRun = dryRun };

        var receipt = await engine.EnterAsync(
            sessionId: Guid.NewGuid().ToString("N"),
            topology,
            permit,
            effective,
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

    /// <summary>
    /// Puts everything back — everything GamerGod applied, not everything one file recorded.
    ///
    /// <para>
    /// This read only the session journal, and <c>gamergod bench</c> writes to a second one. So
    /// interrupting a benchmark left every background process pinned to the ambient mask and in
    /// efficiency mode, while <c>off</c> answered "Nothing to undo. GamerGod is not currently
    /// changing anything." and <c>status</c> agreed. The only thing that cleaned it up was the
    /// next reboot.
    /// </para>
    ///
    /// <para>
    /// An escape path that covers one of the two places changes are recorded is not an escape
    /// path. The service already reads both, through <see cref="StateLayout.Journals"/>; this
    /// now does the same.
    /// </para>
    /// </summary>
    public static async Task<int> OffAsync()
    {
        var operations = new WindowsAmbientOperations();
        var topology = new WindowsTopologyProvider().Classify();

        var ledgers = StateLayout.Journals()
            .Select(path => new MutationLedger(
                new FileJournal(path), new AmbientMutationResolver(operations, topology)))
            .ToArray();

        var outstanding = false;

        foreach (var candidate in ledgers)
        {
            outstanding |= await candidate.HasOutstandingChangesAsync();
        }

        if (!outstanding)
        {
            Console.WriteLine();
            Console.WriteLine("  Nothing to undo. GamerGod is not currently changing anything.");
            Console.WriteLine();
            return 0;
        }

        var reverted = ImmutableArray.CreateBuilder<string>();
        var failures = ImmutableArray.CreateBuilder<(string Key, string Error)>();
        var unresolvable = ImmutableArray.CreateBuilder<string>();

        foreach (var candidate in ledgers)
        {
            // Each journal independently. One that cannot be reverted must not stop the other
            // from being — the same rule the ledger applies between keys.
            var one = await candidate.RevertAsync();

            reverted.AddRange(one.Reverted);
            failures.AddRange(one.Failed);
            unresolvable.AddRange(one.Unresolvable);
        }

        var report = new RevertReport
        {
            Reverted = reverted.ToImmutable(),
            Failed = failures.ToImmutable(),
            Unresolvable = unresolvable.ToImmutable(),
        };

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

    /// <summary>
    /// Reports whether anything is applied. Exit code 0 when nothing is,
    /// <see cref="GameModeIsOnExitCode"/> when something is.
    ///
    /// <para>
    /// The exit code exists so a script can ask without parsing English. The uninstaller is the
    /// caller that needs it: it must not delete GamerGod while changes are still recorded as
    /// applied, because GamerGod is the only thing that knows how to undo them, and it used to
    /// answer that question by looking for a journal <em>file</em> — which exists from the first
    /// time Game Mode is ever turned on and says nothing about whether anything is still applied.
    /// </para>
    /// </summary>
    public static async Task<int> StatusAsync()
    {
        // Both journals, for the same reason 'off' reads both: a benchmark that was
        // interrupted leaves real changes on this machine, and reporting "off" while they are
        // applied is the one answer this command must never give.
        var active = false;

        foreach (var path in StateLayout.Journals())
        {
            var ledger = new MutationLedger(new FileJournal(path), new NullResolver());
            active |= await ledger.HasOutstandingChangesAsync();
        }

        Console.WriteLine();

        if (active)
        {
            Accent("  Game Mode is ON", ConsoleColor.Green);
            Console.WriteLine();

            // Listed from every journal, so a change made by an interrupted benchmark appears
            // here rather than being applied and invisible.
            var captures = new List<JournalEntry>();

            foreach (var path in StateLayout.Journals())
            {
                captures.AddRange(await new FileJournal(path).ReadAllAsync(default));
            }

            var outstanding = captures
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
        return active ? GameModeIsOnExitCode : 0;
    }

    /// <summary>
    /// What <c>gamergod status</c> returns when changes are still applied. Distinct from the
    /// failure codes so a script can tell "on" from "this command did not work".
    /// </summary>
    public const int GameModeIsOnExitCode = 10;

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
}
