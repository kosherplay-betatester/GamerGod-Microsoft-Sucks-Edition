using System.Collections.Immutable;
using GamerGod.Core.Autotune;
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
/// Measures each setting on this machine and keeps only what measurably helped.
///
/// <para>
/// The decisions live in <c>GamerGod.Core.Autotune</c> and are tested without a network, a game
/// or a machine. This file is the driver: it runs the comparisons, applies the validity gate to
/// what came back, and prints the outcome. It decides nothing on its own — every judgement here
/// is a call into <see cref="TuningRules"/>, so the rules cannot quietly differ between what is
/// tested and what runs.
/// </para>
/// </summary>
public static class AutotuneCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var capture = new PresentMonCapture();

        if (!capture.Source.IsAvailable)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"  Cannot tune: {capture.Source.Unavailable}");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Tuning is measurement. Without frame data there is nothing to measure,");
            Console.Error.WriteLine("  and guessing which settings help is what this command exists to replace.");
            Console.Error.WriteLine();
            return 4;
        }

        var seconds = Argument(args, "--seconds", 30);
        var repetitions = Argument(args, "--runs", TuningRules.MinimumRunsPerArm);
        var processId = Argument(args, "--pid", 0);

        if (repetitions < TuningRules.MinimumRunsPerArm)
        {
            // Refused rather than silently raised. Somebody asking for three runs wants a
            // quicker answer, and the honest response is that a quicker answer is not one this
            // command is willing to act on.
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                $"  Tuning needs at least {TuningRules.MinimumRunsPerArm} runs per setting.");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "  Fewer than that cannot separate a real difference from ordinary variation,");
            Console.Error.WriteLine(
                "  and a recommendation drawn from noise is worse than no recommendation.");
            Console.Error.WriteLine("  Use 'gamergod bench' for a quicker look at a single setting.");
            Console.Error.WriteLine();
            return 2;
        }

        var topology = new WindowsTopologyProvider().Classify();
        var operations = new WindowsAmbientOperations();

        var ledger = new MutationLedger(
            new FileJournal(StateLayout.SessionJournal),
            new AmbientMutationResolver(operations, topology));

        var engine = new AmbientEngine(operations, ledger);

        var permit = GameIntegrityPolicy.Evaluate(
            "tuning session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

        var restore = new RestoreStatus { Availability = RestoreAvailability.Unknown };
        var runner = new AbRunner(capture);

        Announce(seconds, repetitions);

        var decisions = ImmutableArray<TuningDecision>.Empty;

        try
        {
            while (TuningPlan.Next(decisions) is { } step)
            {
                Console.WriteLine();
                Console.WriteLine($"  Measuring {step.Factor.Describe()}…");

                var decision = await MeasureAsync(
                    step, runner, engine, ledger, topology, permit, restore,
                    processId, seconds, repetitions);

                decisions = decisions.Add(decision);

                Console.WriteLine($"    {decision.Explain()}");
            }
        }
        finally
        {
            // Whatever happened, the machine goes back. A tuning pass that left settings
            // applied would be changing the machine as a side effect of measuring it.
            await ledger.RevertAsync();
        }

        return Report(TuningPlan.Conclude(decisions));
    }

    private static async Task<TuningDecision> MeasureAsync(
        TuningStep step,
        AbRunner runner,
        AmbientEngine engine,
        MutationLedger ledger,
        CpuTopology topology,
        MutationPermit permit,
        RestoreStatus restore,
        int processId,
        int seconds,
        int repetitions)
    {
        AbRun run;

        try
        {
            run = await runner.RunAsync(
                applyBaseline: async _ =>
                {
                    await ledger.RevertAsync();
                    await Apply(engine, topology, permit, restore, step.Without);
                },
                applyCandidate: async _ =>
                {
                    await ledger.RevertAsync();
                    await Apply(engine, topology, permit, restore, step.With);
                },
                processId,
                TimeSpan.FromSeconds(seconds),
                repetitions);
        }
        catch (Exception)
        {
            return new TuningDecision
            {
                Factor = step.Factor,
                Adopted = false,
                Abort = TuningAbort.TargetStoppedPresenting,
            };
        }

        var abort = TuningRules.Validate(
            run.Baseline.Length,
            run.Candidate.Length,
            run.Baseline.Sum(c => (long)c.Series.FrameTimesMs.Length),
            run.Candidate.Sum(c => (long)c.Series.FrameTimesMs.Length),
            presentModeChanged: PresentModeChanged(run),
            targetStoppedPresenting: !run.HasData);

        if (abort != TuningAbort.None)
        {
            return new TuningDecision { Factor = step.Factor, Adopted = false, Abort = abort };
        }

        var result = AbComparison.Compare(
            TuningRules.Primary,
            [.. run.Baseline.Select(c => c.Series)],
            [.. run.Candidate.Select(c => c.Series)],
            TuningRules.ConfidenceToAct);

        return new TuningDecision
        {
            Factor = step.Factor,
            Adopted = TuningRules.ShouldAdopt(result),
            Evidence = result,
        };
    }

    /// <summary>
    /// Whether presentation changed path between the two arms.
    ///
    /// <para>
    /// A game that went from independent flip to composed — a display-mode switch, an overlay
    /// appearing, alt-tabbing — is not the same measurement on both sides, and the difference
    /// would be attributed to the setting under test.
    /// </para>
    /// </summary>
    private static bool PresentModeChanged(AbRun run)
    {
        var modes = run.Baseline
            .Concat(run.Candidate)
            .SelectMany(c => c.Frames)
            .Select(f => f.PresentMode)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return modes.Count > 1;
    }

    private static async Task Apply(
        AmbientEngine engine,
        CpuTopology topology,
        MutationPermit permit,
        RestoreStatus restore,
        AmbientOptions options)
    {
        // Everything off means nothing to apply, and entering with no mutations would still
        // write a session to the journal for no reason.
        if (!options.ConfineToAmbientDomain
            && !options.DemoteToEfficiencyMode
            && !options.ManagePowerScheme
            && options.Services.IsDefaultOrEmpty)
        {
            return;
        }

        await engine.EnterAsync(
            Guid.NewGuid().ToString("N"), topology, permit, options, restore);
    }

    private static void Announce(int seconds, int repetitions)
    {
        var comparisons = TuningPlan.TotalComparisons;
        var minutes = comparisons * repetitions * 2 * seconds / 60;

        Console.WriteLine();
        Console.WriteLine("  Autotune measures each setting on this machine and keeps what helped.");
        Console.WriteLine();
        Console.WriteLine($"  {comparisons} settings, {repetitions} runs each side, {seconds}s a run.");
        Console.WriteLine($"  About {minutes} minutes with something running the whole time.");
        Console.WriteLine();
        Console.WriteLine("  Play normally. Do not alt-tab, change display mode, or open an overlay —");
        Console.WriteLine("  any of those changes what is being measured and the run is discarded.");
        Console.WriteLine();
        Console.WriteLine($"  Judged on your 1% low, at {TuningRules.ConfidenceToAct:P0} confidence.");
        Console.WriteLine("  Anything it cannot measure is left off.");
    }

    private static int Report(TuningReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"  {report.Headline()}");
        Console.WriteLine();

        foreach (var decision in report.Decisions)
        {
            var mark = decision.Adopted ? "on " : "off";
            Console.WriteLine($"    [{mark}] {decision.Factor.Describe()}");
        }

        Console.WriteLine();

        if (!report.ChangedAnything)
        {
            Console.WriteLine("  Nothing is being recommended, and that is a result rather than a failure.");
            Console.WriteLine("  A machine with headroom to spare has nothing for these settings to recover.");
            Console.WriteLine("  GamerGod would rather tell you that than invent an improvement.");
            Console.WriteLine();
        }

        // Deliberately does not write a profile. Measuring and then changing the machine
        // without being asked is the behaviour this product exists as an alternative to.
        Console.WriteLine("  Nothing has been changed. Turn on what you want in Settings, or run");
        Console.WriteLine("  'gamergod on' to apply the settings you already have.");
        Console.WriteLine();

        return report.Aborted.Length == report.Decisions.Length && report.Decisions.Length > 0 ? 6 : 0;
    }

    private static int Argument(string[] args, string name, int fallback)
    {
        var index = Array.FindIndex(
            args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

        return index >= 0 && index + 1 < args.Length
               && int.TryParse(args[index + 1], out var value) && value > 0
            ? value
            : fallback;
    }
}
