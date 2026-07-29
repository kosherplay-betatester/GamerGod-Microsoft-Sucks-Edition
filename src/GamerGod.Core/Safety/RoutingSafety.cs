using System.Collections.Immutable;
using GamerGod.Core.Hardware;

namespace GamerGod.Core.Safety;

/// <summary>A reason a proposed CPU routing plan is unsafe to apply.</summary>
/// <param name="Code">Stable identifier for tests and logs.</param>
public sealed record RoutingViolation(string Code, string Explanation);

public sealed record RoutingSafetyResult
{
    public required ImmutableArray<RoutingViolation> Violations { get; init; }

    public bool IsSafe => Violations.IsEmpty;

    public string Explain() => IsSafe
        ? "Routing plan is safe."
        : string.Join(" ", Violations.Select(v => v.Explanation));
}

/// <summary>
/// Refuses CPU routing plans that could make the machine unusable.
///
/// <para>
/// This guards against the worst failure mode GamerGod has. Confining every background
/// process to an empty processor mask does not merely slow the machine down — it leaves
/// Windows with nowhere to schedule itself. The desktop stops responding, the watchdog
/// cannot run, and the panic hotkey cannot be serviced, because the code that would handle
/// all three has no core to run on. The only remaining escape is the power button.
/// </para>
///
/// <para>
/// Every mask is therefore validated here before it reaches an API. A plan that fails
/// validation is not clamped or corrected — it is refused, because a silently corrected
/// plan hides the bug that produced it.
/// </para>
/// </summary>
public static class RoutingSafety
{
    /// <summary>
    /// The fewest logical processors the rest of Windows may be confined to. Defined on
    /// <see cref="CpuTopology"/> so the classifier and this validator can never disagree.
    /// </summary>
    public const int MinimumAmbientProcessors = CpuTopology.MinimumAmbientProcessors;

    public static RoutingSafetyResult Validate(
        ProcessorMask gameMask,
        ProcessorMask ambientMask,
        CpuTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var violations = ImmutableArray.CreateBuilder<RoutingViolation>();

        if (gameMask.IsEmpty)
        {
            violations.Add(new RoutingViolation(
                "game.mask.empty",
                "The game would be given no processors at all, which prevents it from running."));
        }

        if (ambientMask.IsEmpty)
        {
            violations.Add(new RoutingViolation(
                "ambient.mask.empty",
                "Every background process would be confined to no processors, leaving Windows "
                + "nowhere to schedule itself. The desktop, the watchdog and the panic key "
                + "would all stop responding."));
        }
        else if (ambientMask.Count < MinimumAmbientProcessors)
        {
            violations.Add(new RoutingViolation(
                "ambient.mask.too.small",
                $"The rest of Windows would be confined to {ambientMask.Count} logical "
                + $"processor(s). At least {MinimumAmbientProcessors} are required so the shell, "
                + "audio and GamerGod's own watchdog remain responsive."));
        }

        if (!gameMask.IsEmpty && !ambientMask.IsEmpty)
        {
            if (gameMask.Group != ambientMask.Group)
            {
                violations.Add(new RoutingViolation(
                    "mask.group.mismatch",
                    "The game and ambient masks address different processor groups. GamerGod "
                    + "does not split a workload across processor groups, because a mask in "
                    + "the wrong group silently selects the wrong processors."));
            }
            else if ((gameMask.Mask & ambientMask.Mask) != 0)
            {
                violations.Add(new RoutingViolation(
                    "mask.overlap",
                    "The game and ambient masks overlap, so background work would be scheduled "
                    + "on the game's processors. That is the contention this partition exists "
                    + "to remove."));
            }
        }

        var machineMask = MachineMask(topology);

        if (!gameMask.IsEmpty && (gameMask.Mask & ~machineMask) != 0)
        {
            violations.Add(new RoutingViolation(
                "game.mask.unknown.processors",
                "The game mask selects processors this machine does not have."));
        }

        if (!ambientMask.IsEmpty && (ambientMask.Mask & ~machineMask) != 0)
        {
            violations.Add(new RoutingViolation(
                "ambient.mask.unknown.processors",
                "The ambient mask selects processors this machine does not have."));
        }

        return new RoutingSafetyResult { Violations = violations.ToImmutable() };
    }

    /// <summary>
    /// Validates the plan a topology produces on its own. A classifier change that made
    /// partitioning unsafe would be caught here rather than on a user's machine.
    /// </summary>
    public static RoutingSafetyResult ValidateTopologyPlan(CpuTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        if (!topology.CanPartition)
        {
            // Nothing is confined on a single-domain machine, so there is no mask to get
            // wrong. Declining to partition is always safe.
            return new RoutingSafetyResult { Violations = [] };
        }

        return Validate(topology.GameDomain.Processors, topology.AmbientMask, topology);
    }

    /// <summary>
    /// Restricts a mask to whole physical cores, for policies that avoid SMT siblings.
    /// Returns the original mask unchanged if the restriction would empty it, because a
    /// narrower-but-empty mask is never an improvement.
    /// </summary>
    public static ProcessorMask WithoutSmtSiblings(PerformanceDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);

        var restricted = domain.PhysicalCoresOnlyMask;
        return restricted.IsEmpty ? domain.Processors : restricted;
    }

    private static ulong MachineMask(CpuTopology topology)
    {
        var mask = 0UL;
        foreach (var domain in topology.Domains)
        {
            mask |= domain.Processors.Mask;
        }

        return mask;
    }
}
