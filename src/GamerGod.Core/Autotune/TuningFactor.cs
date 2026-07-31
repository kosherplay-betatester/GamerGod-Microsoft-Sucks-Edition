using System.Collections.Immutable;
using GamerGod.Core.Engine;

namespace GamerGod.Core.Autotune;

/// <summary>
/// One thing autotune is allowed to change.
///
/// <para>
/// The list is closed, and deliberately short: it is exactly the four levers
/// <see cref="AmbientOptions"/> already exposes. An autotuner whose search space grows over time
/// becomes a general optimiser, and a general optimiser over machine state is the thing this
/// project exists as an alternative to.
/// </para>
/// </summary>
public enum TuningFactor
{
    /// <summary>Move background work off the game's cores. The one worth measuring first.</summary>
    ConfineToAmbientDomain,

    /// <summary>Mark background work as efficiency work.</summary>
    DemoteToEfficiencyMode,

    /// <summary>Activate a duplicated high-performance power scheme.</summary>
    ManagePowerScheme,

    /// <summary>Pause the search indexer and the update stack.</summary>
    SuppressBackgroundServices,
}

public static class TuningFactors
{
    /// <summary>
    /// The order factors are tested in, and it is not arbitrary.
    ///
    /// <para>
    /// Largest expected effect first, so a run that is cut short has still answered the question
    /// most likely to matter. Confinement is the headline lever on a machine with more than one
    /// performance domain; suppressing services is last because its measured effect on a healthy
    /// machine is close to nothing, which is why it ships off by default.
    /// </para>
    /// </summary>
    public static ImmutableArray<TuningFactor> InTestOrder { get; } =
    [
        TuningFactor.ConfineToAmbientDomain,
        TuningFactor.DemoteToEfficiencyMode,
        TuningFactor.ManagePowerScheme,
        TuningFactor.SuppressBackgroundServices,
    ];

    /// <summary>The services suppressed when that factor is on. Same list the engine uses.</summary>
    public static ImmutableArray<string> Services { get; } =
        ["WSearch", "SysMain", "DiagTrack", "wuauserv", "BITS"];

    public static string Describe(this TuningFactor factor) => factor switch
    {
        TuningFactor.ConfineToAmbientDomain => "moving background apps off your game's cores",
        TuningFactor.DemoteToEfficiencyMode => "setting background apps to efficiency mode",
        TuningFactor.ManagePowerScheme => "using a high-performance power plan",
        _ => "pausing the search indexer and update checks",
    };

    /// <summary>Reads a factor's current value out of an options set.</summary>
    public static bool IsOn(this AmbientOptions options, TuningFactor factor)
    {
        ArgumentNullException.ThrowIfNull(options);

        return factor switch
        {
            TuningFactor.ConfineToAmbientDomain => options.ConfineToAmbientDomain,
            TuningFactor.DemoteToEfficiencyMode => options.DemoteToEfficiencyMode,
            TuningFactor.ManagePowerScheme => options.ManagePowerScheme,
            _ => !options.Services.IsDefaultOrEmpty,
        };
    }

    /// <summary>Returns a copy with one factor set. Everything else is left exactly as it was.</summary>
    public static AmbientOptions With(this AmbientOptions options, TuningFactor factor, bool on)
    {
        ArgumentNullException.ThrowIfNull(options);

        return factor switch
        {
            TuningFactor.ConfineToAmbientDomain => options with { ConfineToAmbientDomain = on },
            TuningFactor.DemoteToEfficiencyMode => options with { DemoteToEfficiencyMode = on },
            TuningFactor.ManagePowerScheme => options with { ManagePowerScheme = on },
            _ => options with { Services = on ? Services : [] },
        };
    }
}
