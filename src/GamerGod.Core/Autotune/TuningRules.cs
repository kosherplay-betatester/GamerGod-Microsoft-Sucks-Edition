using GamerGod.Core.Measurement;

namespace GamerGod.Core.Autotune;

/// <summary>Why a measurement was thrown away rather than believed.</summary>
public enum TuningAbort
{
    None,

    /// <summary>The game stopped presenting frames part way through.</summary>
    TargetStoppedPresenting,

    /// <summary>Presentation changed path mid-run — fullscreen toggled, an overlay appeared.</summary>
    PresentModeChanged,

    /// <summary>The two arms saw wildly different amounts of work.</summary>
    ArmsNotComparable,

    /// <summary>Fewer runs completed than the decision requires.</summary>
    NotEnoughRuns,
}

/// <summary>
/// What autotune is allowed to conclude, and when.
///
/// <para>
/// This file matters more than the search does. A search over four booleans is trivial; the hard
/// part is that an unattended tuner is a machine for converting noise into confident
/// recommendations, and a recommendation is read as a finding. The stopping rules therefore come
/// first and are stated as rules rather than left implicit in a loop.
/// </para>
///
/// <para>
/// Autotune reports and stops. It writes no profile and applies nothing — measuring a machine
/// and then changing it without being asked is the behaviour this product exists as an
/// alternative to. If that ever changes, these thresholds are the ones that would need raising,
/// not lowering.
/// </para>
/// </summary>
public static class TuningRules
{
    /// <summary>
    /// The one metric allowed to decide anything, declared before any measurement runs.
    ///
    /// <para>
    /// <see cref="AbComparison"/> reports four. Accepting a configuration when <em>any</em> of
    /// four clears 95% gives a false-positive rate nearer one in five than one in twenty, and
    /// that arithmetic is exactly how this category manufactures wins. One metric is named here,
    /// in advance, and the other three are descriptive only.
    /// </para>
    ///
    /// <para>
    /// The 1% low, because the question a player is actually asking is whether it is
    /// <em>smooth</em>. An average that improves while the 1% low falls is a worse experience
    /// reported as a better one.
    /// </para>
    /// </summary>
    public const Metric Primary = Metric.OnePercentLowFps;

    /// <summary>
    /// Runs per arm before a decision may be made. Higher than a user-initiated proof run.
    ///
    /// <para>
    /// A person watching a benchmark can judge a noisy result themselves. An unattended tuner
    /// cannot, and its output persists — so it buys more evidence before it is allowed to speak.
    /// </para>
    /// </summary>
    public const int MinimumRunsPerArm = 7;

    /// <summary>
    /// The confidence needed to <em>change</em> the machine.
    ///
    /// <para>
    /// Deliberately stricter than the level the same result is reported at. Telling somebody
    /// what was measured and altering their machine on the strength of it are different acts,
    /// and the second should need more evidence than the first.
    /// </para>
    /// </summary>
    public const double ConfidenceToAct = 0.99;

    /// <summary>The confidence a result is reported at.</summary>
    public const double ConfidenceToReport = 0.95;

    /// <summary>
    /// How different the two arms' frame counts may be before the comparison is void.
    ///
    /// <para>
    /// The failure this prevents: one arm measured a loading screen and the other measured
    /// gameplay. That produces a large, confident, completely meaningless delta — and autotune
    /// would report it as a finding, and somebody would act on it.
    /// </para>
    /// </summary>
    public const double MaximumFrameCountDivergence = 0.25;

    /// <summary>
    /// Checks a completed run is worth drawing a conclusion from, before looking at the result.
    ///
    /// <para>
    /// Deliberately evaluated first and separately. A validity gate applied after seeing the
    /// numbers is one that gets argued with.
    /// </para>
    /// </summary>
    public static TuningAbort Validate(
        int baselineRuns,
        int candidateRuns,
        long baselineFrames,
        long candidateFrames,
        bool presentModeChanged,
        bool targetStoppedPresenting)
    {
        if (targetStoppedPresenting)
        {
            return TuningAbort.TargetStoppedPresenting;
        }

        if (presentModeChanged)
        {
            return TuningAbort.PresentModeChanged;
        }

        if (baselineRuns < MinimumRunsPerArm || candidateRuns < MinimumRunsPerArm)
        {
            return TuningAbort.NotEnoughRuns;
        }

        if (baselineFrames <= 0 || candidateFrames <= 0)
        {
            return TuningAbort.ArmsNotComparable;
        }

        var larger = (double)Math.Max(baselineFrames, candidateFrames);
        var smaller = (double)Math.Min(baselineFrames, candidateFrames);

        return (larger - smaller) / larger > MaximumFrameCountDivergence
            ? TuningAbort.ArmsNotComparable
            : TuningAbort.None;
    }

    /// <summary>
    /// Whether a measured result is strong enough to change the machine.
    ///
    /// <para>
    /// Only an <see cref="MeasurementVerdict.Improvement"/> on the primary metric, measured at
    /// the acting confidence, counts. Everything else — no measurable effect, a regression, an
    /// improvement on some other metric — leaves the factor exactly as it was.
    /// </para>
    /// </summary>
    public static bool ShouldAdopt(AbResult? result) =>
        result is { Verdict: MeasurementVerdict.Improvement }
        && result.Metric == Primary
        && result.ConfidenceLevel >= ConfidenceToAct
        && result.BaselineRuns >= MinimumRunsPerArm
        && result.CandidateRuns >= MinimumRunsPerArm;

    /// <summary>
    /// Whether a factor made things measurably worse, which is worth recording even though the
    /// action — leave it off — is the same as for no effect.
    ///
    /// <para>
    /// The distinction is the whole reason this project measures. "We tried it and it hurt" is a
    /// finding; "we tried it and could not tell" is a different one, and a report that merged
    /// them would be hiding the only result that has ever surprised anybody here.
    /// </para>
    /// </summary>
    public static bool IsRegression(AbResult? result) =>
        result is { Verdict: MeasurementVerdict.Regression } && result.Metric == Primary;

    public static string Explain(this TuningAbort abort) => abort switch
    {
        TuningAbort.TargetStoppedPresenting =>
            "the game stopped drawing part way through, so the two halves are not comparable",
        TuningAbort.PresentModeChanged =>
            "the way frames reached the screen changed part way through — a display mode switch, "
            + "an overlay appearing, or alt-tabbing — which changes what was being measured",
        TuningAbort.ArmsNotComparable =>
            "the two halves saw very different amounts of work, so the difference between them "
            + "is not about the setting",
        TuningAbort.NotEnoughRuns =>
            $"fewer than {MinimumRunsPerArm} runs completed on each side",
        _ => "no problem",
    };
}
