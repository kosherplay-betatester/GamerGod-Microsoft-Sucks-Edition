using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Measurement;

namespace GamerGod.Core.Autotune;

/// <summary>What testing one factor concluded.</summary>
public sealed record TuningDecision
{
    public required TuningFactor Factor { get; init; }

    /// <summary>True when the factor is on in the recommendation.</summary>
    public required bool Adopted { get; init; }

    /// <summary>Why the run was discarded, or <see cref="TuningAbort.None"/>.</summary>
    public TuningAbort Abort { get; init; }

    /// <summary>
    /// The comparison this decision rests on, or null when the run was aborted.
    ///
    /// <para>
    /// Kept on the decision rather than summarised into it. A recommendation that cannot produce
    /// the measurement behind it is a preference wearing a number, and this project has one job
    /// that depends on the difference.
    /// </para>
    /// </summary>
    public AbResult? Evidence { get; init; }

    /// <summary>Plain account of what happened, in the words Article VII allows.</summary>
    public string Explain()
    {
        var what = Factor.Describe();

        if (Abort != TuningAbort.None)
        {
            return $"Could not measure {what}: {Abort.Explain()}. Nothing was changed for it.";
        }

        if (Evidence is not { } result)
        {
            return $"Did not measure {what}. Nothing was changed for it.";
        }

        if (TuningRules.IsRegression(result))
        {
            return $"Measured {what} making things worse on this machine, so it is off. "
                + result.Explain();
        }

        return Adopted
            ? $"Measured {what} helping on this machine, so it is on. {result.Explain()}"
            : $"Could not measure {what} making a difference on this machine, so it is off. "
                + result.Explain();
    }
}

/// <summary>
/// Everything a tuning pass concluded, and the evidence for each part of it.
/// </summary>
public sealed record TuningReport
{
    public required ImmutableArray<TuningDecision> Decisions { get; init; }

    /// <summary>The settings this pass recommends. Never applied by the report itself.</summary>
    public required AmbientOptions Recommended { get; init; }

    /// <summary>
    /// True when at least one factor was adopted on measured evidence.
    ///
    /// <para>
    /// The negative case is not an empty state and must not be rendered as one. On a quiet
    /// machine with headroom the correct answer really is "nothing here made a measurable
    /// difference", and that is a result — arguably the most valuable one this product can
    /// produce, because every competitor would have claimed four improvements.
    /// </para>
    /// </summary>
    public bool ChangedAnything => Decisions.Any(d => d.Adopted);

    public bool FoundRegression => Decisions.Any(d => TuningRules.IsRegression(d.Evidence));

    /// <summary>Runs that were thrown away rather than believed.</summary>
    public ImmutableArray<TuningDecision> Aborted =>
        [.. Decisions.Where(d => d.Abort != TuningAbort.None)];

    public string Headline()
    {
        if (Decisions.IsDefaultOrEmpty)
        {
            return "Nothing was measured.";
        }

        var adopted = Decisions.Count(d => d.Adopted);
        var aborted = Aborted.Length;

        if (adopted == 0)
        {
            var nothing = FoundRegression
                ? "Nothing here made this machine faster, and one setting made it slower."
                : "Nothing here made a measurable difference on this machine.";

            return aborted == 0
                ? nothing
                : $"{nothing} {aborted} of {Decisions.Length} runs were discarded as unreliable.";
        }

        return adopted == 1
            ? "One setting measurably helped on this machine."
            : $"{adopted} settings measurably helped on this machine.";
    }
}
