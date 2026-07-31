using System.Globalization;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Measurement;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Safety;
using GamerGod.Windows;

namespace GamerGod.Cli;

/// <summary>
/// Measures whether GamerGod actually does anything on this machine.
///
/// <para>
/// This is the command that makes every other claim in the product legitimate, and the one
/// that is allowed to conclude that a lever does nothing. Charter Article VII exists because
/// this category is full of tools that never ran this test.
/// </para>
/// </summary>
internal static class BenchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var capture = new PresentMonCapture();

        Console.WriteLine();
        Accent("  Proof run", ConsoleColor.Cyan);
        Console.WriteLine();

        if (!capture.Source.IsAvailable)
        {
            // The honest failure. No numbers, no estimates, no "expected" figures.
            Accent("  Cannot measure this machine.", ConsoleColor.Yellow);
            Console.WriteLine();
            Console.WriteLine($"  {capture.Source.Unavailable}");

            if (capture.Source.Remedy is { } remedy)
            {
                Console.WriteLine();
                Console.WriteLine($"  {remedy}");
            }

            Console.WriteLine();
            Console.WriteLine("  GamerGod will not estimate a result. Until something can measure");
            Console.WriteLine("  your frames, it has nothing to tell you about them.");
            Console.WriteLine();
            return 6;
        }

        var seconds = ParseInt(args, "--seconds") ?? 30;
        var repetitions = ParseInt(args, "--runs") ?? 5;
        var processId = ParseInt(args, "--pid");

        var minutes = EstimateMinutes(seconds, repetitions);

        Console.WriteLine($"  Source      {capture.Source.Name}");
        Console.WriteLine($"  Plan        {repetitions} runs per arm, {seconds}s each, interleaved");
        Console.WriteLine($"  Target      {(processId is null ? "whatever is presenting frames" : $"process {processId}")}");
        Console.WriteLine();
        Console.WriteLine("  Start your game and leave it in a repeatable scene — a menu, a");
        Console.WriteLine("  benchmark, or standing still somewhere. Consistency matters more");
        Console.WriteLine($"  than load. This will take about {minutes} minute{(minutes == 1 ? "" : "s")}.");
        Console.WriteLine();

        // Without this warning the command happily measures the desktop and reports a real,
        // correctly-computed, completely misleading result. Moving background apps off the
        // game's cores is *supposed* to make a background app slower, so benchmarking one
        // shows the feature working while looking like the feature failing.
        Accent("  This only means anything with a game running.", ConsoleColor.Yellow);
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  If nothing is, GamerGod will measure whatever is drawing to your");
        Console.WriteLine("  screen — a browser, this terminal — and report that it got slower.");
        Console.WriteLine("  That answer would be arithmetically correct and worthless: making");
        Console.WriteLine("  background apps slower is the entire point.");
        Console.WriteLine();
        Console.WriteLine("  Use --pid to name your game if you want to be certain.");
        Console.WriteLine();
        Console.Write("  Press Enter when your game is running, or Ctrl+C to stop. ");
        Console.ReadLine();
        Console.WriteLine();

        var operations = new WindowsAmbientOperations();
        var topology = new WindowsTopologyProvider().Classify();

        // Kept apart from the session journal so a run that applies and reverts many times
        // cannot interleave its record with a user's live session.
        var ledger = new MutationLedger(
            new FileJournal(StateLayout.BenchJournal),
            new AmbientMutationResolver(operations, topology));
        var engine = new AmbientEngine(operations, ledger);

        var permit = GameIntegrityPolicy.Evaluate(
            "bench session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

        var restore = new RestoreStatus { Availability = RestoreAvailability.Unknown };
        var runner = new AbRunner(capture);
        var completed = 0;

        var run = await runner.RunAsync(
            applyBaseline: async _ =>
            {
                await ledger.RevertAsync();
                Report(ref completed, repetitions, "baseline");
            },
            applyCandidate: async _ =>
            {
                await engine.EnterAsync(
                    Guid.NewGuid().ToString("N"),
                    topology,
                    permit,
                    new AmbientOptions(),
                    restore);
                Report(ref completed, repetitions, "GamerGod on");
            },
            processId,
            TimeSpan.FromSeconds(seconds),
            repetitions);

        // Always leave the machine as it was found, whatever the result.
        await ledger.RevertAsync();

        Console.WriteLine();
        Console.WriteLine();

        if (!run.HasData)
        {
            Accent("  No frames were captured.", ConsoleColor.Yellow);
            Console.WriteLine();
            Console.WriteLine("  Nothing was presenting frames, or the capture could not read them.");
            Console.WriteLine("  No result is being reported, because there is no result.");
            Console.WriteLine();
            return 6;
        }

        Accent("  Result", ConsoleColor.White);
        Console.WriteLine();

        foreach (var result in run.Compare())
        {
            var colour = result.Verdict switch
            {
                MeasurementVerdict.Improvement => ConsoleColor.Green,
                MeasurementVerdict.Regression => ConsoleColor.Red,
                _ => ConsoleColor.DarkGray,
            };

            Console.Write("    ");
            Accent(Tag(result.Verdict), colour);
            Console.WriteLine($"  {result.Explain()}");
        }

        Console.WriteLine();
        Console.WriteLine("  These are measurements from your machine, not estimates, and any");
        Console.WriteLine("  result whose range includes zero is reported as no effect — even");
        Console.WriteLine("  when the internet insists otherwise.");

        if (run.Compare().Any(r => r.Verdict == MeasurementVerdict.Regression))
        {
            Console.WriteLine();
            Accent("  Something got worse. Worth checking before you trust it:", ConsoleColor.Yellow);
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("  Was a game actually running and presenting frames? If GamerGod");
            Console.WriteLine("  measured a background app instead, a slowdown is the feature");
            Console.WriteLine("  working, not failing. Re-run with --pid pointing at your game.");
            Console.WriteLine();
            Console.WriteLine("  If it was your game, then on this hardware and in this scene the");
            Console.WriteLine("  answer is that GamerGod did not help. That is a real result and");
            Console.WriteLine("  it will not be dressed up as anything else.");
        }

        Console.WriteLine();

        return 0;
    }

    private static void Report(ref int completed, int repetitions, string arm)
    {
        completed++;
        var total = repetitions * 2;
        Console.Write($"\r  Measuring   [{completed}/{total}] {arm}".PadRight(60));
    }

    private static string Tag(MeasurementVerdict verdict) => verdict switch
    {
        MeasurementVerdict.Improvement => "[BETTER]",
        MeasurementVerdict.Regression => "[WORSE ]",
        MeasurementVerdict.NoMeasurableEffect => "[  --  ]",
        _ => "[  ??  ]",
    };

    private static int EstimateMinutes(int seconds, int repetitions) =>
        Math.Max(1, (int)Math.Ceiling(seconds * repetitions * 2 / 60.0));

    private static int? ParseInt(string[] args, string flag)
    {
        var index = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

        if (index < 0 || index + 1 >= args.Length)
        {
            return null;
        }

        return int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static void Accent(string text, ConsoleColor colour)
    {
        if (Console.IsOutputRedirected)
        {
            Console.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        Console.Write(text);
        Console.ForegroundColor = previous;
    }
}
