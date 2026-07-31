using System.Collections.Immutable;
using GamerGod.Core.Autotune;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Autotune;

/// <summary>
/// The three cases the design is defined by, plus the shape of the search.
/// </summary>
public sealed class TuningPlanTests
{
    private static AbResult Improvement() => Verdict(MeasurementVerdict.Improvement);

    private static AbResult NoEffect() => Verdict(MeasurementVerdict.NoMeasurableEffect);

    private static AbResult Regression() => Verdict(MeasurementVerdict.Regression);

    private static AbResult Verdict(MeasurementVerdict verdict) => new()
    {
        Metric = TuningRules.Primary,
        Verdict = verdict,
        BaselineValue = 90,
        CandidateValue = verdict == MeasurementVerdict.Regression ? 81 : 96,
        DeltaPercent = verdict == MeasurementVerdict.Regression ? -10 : 6.6,
        LowerPercent = verdict == MeasurementVerdict.Regression ? -14 : 2.1,
        UpperPercent = verdict == MeasurementVerdict.Regression ? -6 : 11.0,
        BaselineRuns = TuningRules.MinimumRunsPerArm,
        CandidateRuns = TuningRules.MinimumRunsPerArm,
        ConfidenceLevel = TuningRules.ConfidenceToAct,
    };

    private static TuningDecision Decide(TuningFactor factor, AbResult? evidence) => new()
    {
        Factor = factor,
        Adopted = TuningRules.ShouldAdopt(evidence),
        Evidence = evidence,
    };

    // ---- case one: noise in, nothing out --------------------------------

    [Fact]
    public void Fed_nothing_but_noise_it_recommends_nothing_and_says_so()
    {
        // The single most important behaviour. On a quiet machine with headroom the correct
        // answer really is "nothing measurable", and rendering that as an empty state would
        // teach people the feature is broken when it is being honest.
        var decisions = TuningFactors.InTestOrder
            .Select(f => Decide(f, NoEffect()))
            .ToImmutableArray();

        var report = TuningPlan.Conclude(decisions);

        Assert.False(report.ChangedAnything);
        Assert.False(report.FoundRegression);
        Assert.Equal(TuningPlan.Nothing, report.Recommended);
        Assert.Contains("measurable difference", report.Headline(), StringComparison.Ordinal);
    }

    // ---- case two: a factor that measurably hurts ------------------------

    [Fact]
    public void A_factor_that_measurably_hurts_is_recorded_with_its_evidence_and_left_off()
    {
        var decisions = ImmutableArray.Create(
            Decide(TuningFactor.ConfineToAmbientDomain, Regression()),
            Decide(TuningFactor.DemoteToEfficiencyMode, NoEffect()),
            Decide(TuningFactor.ManagePowerScheme, NoEffect()),
            Decide(TuningFactor.SuppressBackgroundServices, NoEffect()));

        var report = TuningPlan.Conclude(decisions);

        Assert.True(report.FoundRegression);
        Assert.False(report.Recommended.ConfineToAmbientDomain);

        var confinement = decisions[0];
        Assert.NotNull(confinement.Evidence);
        Assert.Contains("worse", confinement.Explain(), StringComparison.Ordinal);

        // The headline must not read as a clean bill of health.
        Assert.Contains("slower", report.Headline(), StringComparison.Ordinal);
    }

    // ---- case three: an aborted run produces no result -------------------

    [Fact]
    public void An_aborted_run_yields_no_result_rather_than_a_weaker_one()
    {
        var aborted = new TuningDecision
        {
            Factor = TuningFactor.ConfineToAmbientDomain,
            Adopted = false,
            Abort = TuningAbort.PresentModeChanged,
        };

        var report = TuningPlan.Conclude([aborted]);

        Assert.False(report.ChangedAnything);
        Assert.Single(report.Aborted);
        Assert.Null(aborted.Evidence);
        Assert.Contains("Could not measure", aborted.Explain(), StringComparison.Ordinal);
        Assert.Contains("discarded as unreliable", report.Headline(), StringComparison.Ordinal);
    }

    // ---- the search --------------------------------------------------

    [Fact]
    public void It_starts_from_everything_off_rather_than_from_the_shipped_defaults()
    {
        // Starting from a configuration somebody already chose would measure the deltas around
        // that choice instead of the choice itself.
        Assert.False(TuningPlan.Nothing.ConfineToAmbientDomain);
        Assert.False(TuningPlan.Nothing.DemoteToEfficiencyMode);
        Assert.False(TuningPlan.Nothing.ManagePowerScheme);
        Assert.True(TuningPlan.Nothing.Services.IsDefaultOrEmpty);
        Assert.False(TuningPlan.Nothing.DryRun);
    }

    [Fact]
    public void The_first_thing_tested_is_the_lever_most_likely_to_matter()
    {
        // Largest expected effect first, so a pass cut short has still answered the question
        // most worth asking.
        var step = TuningPlan.Next([]);

        Assert.NotNull(step);
        Assert.Equal(TuningFactor.ConfineToAmbientDomain, step!.Factor);
    }

    [Fact]
    public void Each_step_differs_from_its_baseline_by_exactly_one_factor()
    {
        var step = TuningPlan.Next([])!;

        foreach (var factor in TuningFactors.InTestOrder)
        {
            var differs = step.Without.IsOn(factor) != step.With.IsOn(factor);

            Assert.True(
                factor == step.Factor ? differs : !differs,
                $"{factor} should {(factor == step.Factor ? "" : "not ")}differ between the arms");
        }
    }

    [Fact]
    public void A_later_factor_is_measured_on_top_of_what_already_won()
    {
        // One-factor-at-a-time against the best so far, so a factor that only pays off once
        // another is on is still found — provided the other comes first in the order, which is
        // why the order is fixed rather than arbitrary.
        var decided = ImmutableArray.Create(
            Decide(TuningFactor.ConfineToAmbientDomain, Improvement()));

        var step = TuningPlan.Next(decided)!;

        Assert.Equal(TuningFactor.DemoteToEfficiencyMode, step.Factor);
        Assert.True(step.Without.ConfineToAmbientDomain);
        Assert.True(step.With.ConfineToAmbientDomain);
    }

    [Fact]
    public void The_pass_ends_after_a_budget_fixed_before_it_started()
    {
        // No peeking, and no running until something is found — which, given enough attempts,
        // always happens.
        var decided = TuningFactors.InTestOrder
            .Select(f => Decide(f, NoEffect()))
            .ToImmutableArray();

        Assert.Null(TuningPlan.Next(decided));
        Assert.Equal(TuningFactors.InTestOrder.Length, TuningPlan.TotalComparisons);
    }

    [Fact]
    public void The_recommendation_is_exactly_what_survived()
    {
        var decided = ImmutableArray.Create(
            Decide(TuningFactor.ConfineToAmbientDomain, Improvement()),
            Decide(TuningFactor.DemoteToEfficiencyMode, Regression()),
            Decide(TuningFactor.ManagePowerScheme, Improvement()),
            Decide(TuningFactor.SuppressBackgroundServices, NoEffect()));

        var report = TuningPlan.Conclude(decided);

        Assert.True(report.Recommended.ConfineToAmbientDomain);
        Assert.False(report.Recommended.DemoteToEfficiencyMode);
        Assert.True(report.Recommended.ManagePowerScheme);
        Assert.True(report.Recommended.Services.IsDefaultOrEmpty);
        Assert.True(report.ChangedAnything);
    }

    [Fact]
    public void No_factor_is_ever_measured_twice()
    {
        var decided = ImmutableArray<TuningDecision>.Empty;
        var seen = new List<TuningFactor>();

        while (TuningPlan.Next(decided) is { } step)
        {
            Assert.DoesNotContain(step.Factor, seen);
            seen.Add(step.Factor);
            decided = decided.Add(Decide(step.Factor, NoEffect()));
        }

        Assert.Equal(TuningFactors.InTestOrder.Length, seen.Count);
    }

    [Fact]
    public void A_tuning_pass_never_measures_a_dry_run()
    {
        // A dry run measures nothing, so an arm that used one would be comparing the machine
        // against itself and reporting the noise as a result.
        var decided = ImmutableArray<TuningDecision>.Empty;

        while (TuningPlan.Next(decided) is { } step)
        {
            Assert.False(step.Without.DryRun);
            Assert.False(step.With.DryRun);
            decided = decided.Add(Decide(step.Factor, Improvement()));
        }
    }

    [Fact]
    public void Every_decision_that_adopted_something_carries_the_measurement_behind_it()
    {
        // A recommendation that cannot produce its evidence is a preference wearing a number.
        var decided = TuningFactors.InTestOrder
            .Select(f => Decide(f, Improvement()))
            .ToImmutableArray();

        Assert.All(
            decided.Where(d => d.Adopted),
            d => Assert.NotNull(d.Evidence));
    }
}
