using GamerGod.Core.Autotune;
using GamerGod.Core.Measurement;
using Xunit;

namespace GamerGod.Core.Tests.Autotune;

/// <summary>
/// What autotune is allowed to conclude.
///
/// <para>
/// These matter more than the search tests. An unattended tuner is a machine for turning noise
/// into confident recommendations, and its output is written to a profile that then changes the
/// machine on every launch. Almost every test here asserts that it stays quiet.
/// </para>
/// </summary>
public sealed class TuningRulesTests
{
    private static AbResult Result(
        MeasurementVerdict verdict,
        Metric metric = Metric.OnePercentLowFps,
        double confidence = TuningRules.ConfidenceToAct,
        int runs = TuningRules.MinimumRunsPerArm) => new()
    {
        Metric = metric,
        Verdict = verdict,
        BaselineValue = 90,
        CandidateValue = 96,
        DeltaPercent = 6.6,
        LowerPercent = 2.1,
        UpperPercent = 11.0,
        BaselineRuns = runs,
        CandidateRuns = runs,
        ConfidenceLevel = confidence,
    };

    // ---- the metric is declared in advance ------------------------------

    [Fact]
    public void The_primary_metric_is_the_one_percent_low()
    {
        // Named as a constant, before any measurement runs. AbComparison reports four metrics,
        // and accepting when any of four clears the bar gives a false-positive rate nearer one
        // in five than one in twenty — which is the arithmetic this category runs on.
        Assert.Equal(Metric.OnePercentLowFps, TuningRules.Primary);
    }

    [Theory]
    [InlineData(Metric.AverageFps)]
    [InlineData(Metric.PointOnePercentLowFps)]
    [InlineData(Metric.ConsistencyPercent)]
    public void An_improvement_on_any_other_metric_decides_nothing(Metric metric)
    {
        // An average that improves while the 1% low falls is a worse experience reported as a
        // better one. The other three are descriptive and may not drive a decision.
        Assert.False(TuningRules.ShouldAdopt(Result(MeasurementVerdict.Improvement, metric)));
    }

    // ---- and the evidence bar is asymmetric -----------------------------

    [Fact]
    public void Changing_the_machine_needs_more_evidence_than_reporting_a_result()
    {
        // Telling somebody what was measured and altering their machine on the strength of it
        // are different acts.
        Assert.True(TuningRules.ConfidenceToAct > TuningRules.ConfidenceToReport);
    }

    [Fact]
    public void An_improvement_measured_at_only_the_reporting_confidence_is_not_adopted() =>
        Assert.False(TuningRules.ShouldAdopt(
            Result(MeasurementVerdict.Improvement, confidence: TuningRules.ConfidenceToReport)));

    [Fact]
    public void An_unattended_tuner_requires_more_runs_than_a_watched_benchmark()
    {
        // A person watching a benchmark can judge a noisy result. A tuner cannot, and its
        // output persists.
        Assert.True(TuningRules.MinimumRunsPerArm >= 7);

        Assert.False(TuningRules.ShouldAdopt(
            Result(MeasurementVerdict.Improvement, runs: TuningRules.MinimumRunsPerArm - 1)));
    }

    [Theory]
    [InlineData(MeasurementVerdict.NoMeasurableEffect)]
    [InlineData(MeasurementVerdict.Regression)]
    [InlineData(MeasurementVerdict.Insufficient)]
    public void Anything_short_of_a_measured_improvement_leaves_the_factor_off(
        MeasurementVerdict verdict) =>
        Assert.False(TuningRules.ShouldAdopt(Result(verdict)));

    [Fact]
    public void No_result_at_all_adopts_nothing() => Assert.False(TuningRules.ShouldAdopt(null));

    [Fact]
    public void A_regression_is_recorded_even_though_the_action_is_the_same()
    {
        // "We tried it and it hurt" and "we tried it and could not tell" lead to the same
        // setting and are different findings. Merging them would hide the only result this
        // project has ever produced that surprised anybody.
        Assert.True(TuningRules.IsRegression(Result(MeasurementVerdict.Regression)));
        Assert.False(TuningRules.IsRegression(Result(MeasurementVerdict.NoMeasurableEffect)));
    }

    // ---- the validity gate, applied before the numbers are looked at ----

    [Fact]
    public void A_valid_run_passes()
    {
        Assert.Equal(
            TuningAbort.None,
            TuningRules.Validate(7, 7, 100_000, 98_000, false, false));
    }

    [Fact]
    public void A_display_mode_change_mid_run_voids_the_comparison()
    {
        // Toggling fullscreen, an overlay appearing, alt-tabbing. The thing being measured
        // changed underneath the measurement.
        Assert.Equal(
            TuningAbort.PresentModeChanged,
            TuningRules.Validate(7, 7, 100_000, 98_000, presentModeChanged: true, false));
    }

    [Fact]
    public void A_game_that_stopped_drawing_voids_the_comparison() =>
        Assert.Equal(
            TuningAbort.TargetStoppedPresenting,
            TuningRules.Validate(7, 7, 100_000, 98_000, false, targetStoppedPresenting: true));

    [Fact]
    public void Arms_that_saw_very_different_amounts_of_work_are_not_compared()
    {
        // The failure this exists for: one arm measured a loading screen and the other measured
        // gameplay. That produces a large, confident, meaningless delta — and autotune would
        // write it to a profile and apply it forever.
        Assert.Equal(
            TuningAbort.ArmsNotComparable,
            TuningRules.Validate(7, 7, 100_000, 40_000, false, false));
    }

    [Fact]
    public void Too_few_runs_is_an_abort_rather_than_a_weaker_answer() =>
        Assert.Equal(
            TuningAbort.NotEnoughRuns,
            TuningRules.Validate(3, 7, 100_000, 98_000, false, false));

    [Fact]
    public void A_run_with_no_frames_is_not_comparable() =>
        Assert.Equal(
            TuningAbort.ArmsNotComparable,
            TuningRules.Validate(7, 7, 0, 98_000, false, false));

    [Fact]
    public void The_gate_reports_the_most_serious_problem_first()
    {
        // A run where the game stopped drawing AND the arms diverge is reported as the game
        // stopping, because that is the cause and the divergence is the symptom.
        Assert.Equal(
            TuningAbort.TargetStoppedPresenting,
            TuningRules.Validate(1, 1, 10, 100_000, presentModeChanged: true, targetStoppedPresenting: true));
    }

    [Fact]
    public void Nothing_autotune_says_claims_it_writes_or_applies_anything()
    {
        // It reports and stops. An early draft of the refusal message said this command
        // "writes its conclusions to a profile that changes your machine", which nothing in the
        // codebase does — the same failure that shipped a protection list naming a process no
        // machine runs, and a README naming four projects that did not exist.
        //
        // Kept as a test rather than a comment because the next person to add persistence will
        // see it fail, which is the moment to check the copy rather than months later.
        var strings = Enum.GetValues<TuningAbort>()
            .Select(a => a.Explain())
            .Concat(Enum.GetValues<TuningFactor>().Select(f => f.Describe()));

        foreach (var text in strings)
        {
            Assert.DoesNotContain("profile", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("will apply", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("saved", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Every_abort_explains_itself_in_words_a_player_can_act_on()
    {
        foreach (var abort in Enum.GetValues<TuningAbort>())
        {
            var text = abort.Explain();

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.DoesNotContain("PresentMode", text, StringComparison.Ordinal);
            Assert.DoesNotContain("arm", text, StringComparison.OrdinalIgnoreCase);
        }
    }
}
