using System.Collections.Immutable;
using GamerGod.Core.Hardware;

namespace GamerGod.Core.Engine;

/// <summary>A running process, as much as GamerGod needs to know about one.</summary>
/// <param name="Id">Process id.</param>
/// <param name="Name">Executable name without extension.</param>
/// <param name="WorkingSetBytes">Private working set, used to rank what is worth demoting.</param>
public sealed record ProcessInfo(int Id, string Name, long WorkingSetBytes)
{
    /// <summary>True once a process is large enough that demoting it could matter.</summary>
    public bool IsWorthDemoting => WorkingSetBytes >= 50L * 1024 * 1024;
}

/// <summary>Whether a service is running, and how it is configured to start.</summary>
public sealed record ServiceInfo(string Name, bool IsRunning, string StartType);

/// <summary>
/// Everything the ambient levers need from the operating system.
///
/// <para>
/// This interface is the seam that keeps <c>GamerGod.Core</c> free of P/Invoke. The
/// mutations below are the first code in the project that changes a real machine, so being
/// able to drive every one of them against a simulated operating system — with induced
/// failures, in a test that needs no administrator rights — is what makes them safe to write
/// at all.
/// </para>
/// </summary>
public interface IAmbientOperations
{
    // ---- Observation -------------------------------------------------

    ValueTask<ImmutableArray<ProcessInfo>> ListProcessesAsync(CancellationToken cancellationToken);

    ValueTask<ServiceInfo?> QueryServiceAsync(string name, CancellationToken cancellationToken);

    // ---- Efficiency mode ---------------------------------------------

    /// <summary>
    /// Reads whether a process is currently running in efficiency mode, so it can be put
    /// back exactly as it was rather than assumed to have been normal.
    /// </summary>
    ValueTask<bool> IsEfficiencyModeAsync(int processId, CancellationToken cancellationToken);

    /// <summary>
    /// Turns Windows' own efficiency mode on or off for a process — the same mechanism Task
    /// Manager exposes: base priority drops to low and the scheduler prefers efficient cores
    /// and lower clocks.
    /// </summary>
    ValueTask SetEfficiencyModeAsync(int processId, bool enabled, CancellationToken cancellationToken);

    // ---- Domain confinement ------------------------------------------

    /// <summary>
    /// Confines a set of processes to a processor mask, returning a handle that lifts the
    /// confinement when released.
    ///
    /// <para>
    /// Reversal is the reason this returns a handle rather than being applied per process.
    /// A job object's limits vanish when the last handle to it closes, so if GamerGod dies
    /// the confinement dies with it — the operating system performs the revert whether or
    /// not any of our code gets to run.
    /// </para>
    /// </summary>
    ValueTask<IConfinement> ConfineAsync(
        string name,
        ProcessorMask mask,
        IEnumerable<int> processIds,
        CancellationToken cancellationToken);

    /// <summary>Reads a process's current processor affinity, so it can be restored exactly.</summary>
    ValueTask<ProcessorMask> GetAffinityAsync(int processId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a process's processor affinity.
    ///
    /// <para>
    /// This is the confinement mechanism for a short-lived caller. A job object's limits
    /// exist only while a handle to it is open, so a command-line tool that exits after
    /// applying would release the confinement microseconds later — and report success. An
    /// affinity mask outlives the process that set it, which means it has to be journalled
    /// and restored rather than relying on the operating system to clean up.
    /// </para>
    /// </summary>
    ValueTask SetAffinityAsync(int processId, ProcessorMask mask, CancellationToken cancellationToken);

    // ---- Services ----------------------------------------------------

    ValueTask StopServiceAsync(string name, CancellationToken cancellationToken);

    ValueTask StartServiceAsync(string name, CancellationToken cancellationToken);

    // ---- Power -------------------------------------------------------

    ValueTask<Guid> GetActivePowerSchemeAsync(CancellationToken cancellationToken);

    ValueTask SetActivePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken);

    /// <summary>
    /// Duplicates a scheme so GamerGod never edits the user's own power plan. Returns the
    /// new scheme's id.
    /// </summary>
    ValueTask<Guid> DuplicatePowerSchemeAsync(Guid source, string name, CancellationToken cancellationToken);

    ValueTask DeletePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken);
}

/// <summary>
/// Live confinement of a group of processes. Disposing lifts it.
/// </summary>
public interface IConfinement : IAsyncDisposable
{
    string Name { get; }

    int ProcessCount { get; }
}
