using System.Collections.Immutable;
using GamerGod.Core.Mutations;

namespace GamerGod.Core.Safety;

/// <summary>Whether the machine can take a restore point right now.</summary>
public enum RestoreAvailability
{
    Unknown,

    /// <summary>System Protection is on for the system drive and a point can be created.</summary>
    Enabled,

    /// <summary>
    /// Present but switched off for the system drive. This is the default on a large share
    /// of Windows 11 installations, so it must be detected rather than assumed.
    /// </summary>
    Disabled,

    /// <summary>The Volume Shadow Copy service is missing or stopped.</summary>
    Unavailable,
}

public sealed record RestoreStatus
{
    public required RestoreAvailability Availability { get; init; }

    public int ExistingPoints { get; init; }

    /// <summary>
    /// Windows throttles restore point creation to one per this many minutes. Zero means no
    /// throttle. When non-zero, a requested point may silently not be created — which is
    /// why <see cref="RestorePointResult.Created"/> must be checked rather than assumed.
    /// </summary>
    public int CreationFrequencyMinutes { get; init; }

    public string? Detail { get; init; }

    public bool CanCreate => Availability == RestoreAvailability.Enabled;
}

public sealed record RestorePointResult
{
    public required bool Created { get; init; }

    public long? SequenceNumber { get; init; }

    /// <summary>Populated when <see cref="Created"/> is false. Always shown to the user.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Creates Windows System Restore points. Implemented in <c>GamerGod.Windows</c> over the
/// <c>SystemRestore</c> WMI class.
/// </summary>
public interface ISystemRestore
{
    ValueTask<RestoreStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask<RestorePointResult> CreateAsync(string description, CancellationToken cancellationToken);
}

public enum SafetyVerdict
{
    /// <summary>Nothing outlives a reboot. The journal alone is sufficient.</summary>
    Proceed,

    /// <summary>Boot-persistent work ahead, and a restore point must be taken first.</summary>
    RequireRestorePoint,

    /// <summary>
    /// Boot-persistent work ahead but no restore point is possible, and the user has
    /// accepted that. The journal and the boot-recovery pass remain in force.
    /// </summary>
    ProceedWithoutRestorePoint,

    /// <summary>
    /// Boot-persistent work ahead, no restore point possible, and no acknowledgement.
    /// GamerGod declines rather than making an unprotected change.
    /// </summary>
    Blocked,
}

public sealed record SafetyDecision
{
    public required SafetyVerdict Verdict { get; init; }

    public required ImmutableArray<string> BootPersistentKeys { get; init; }

    public required string Explanation { get; init; }

    public bool MayProceed => Verdict != SafetyVerdict.Blocked;
}

/// <summary>
/// The second rung of GamerGod's safety ladder.
///
/// <para>
/// The journal is the primary guarantee and covers the overwhelming majority of what
/// GamerGod does — stopping a service or demoting a process cannot survive a reboot no
/// matter what happens. A restore point is not needed for those, and taking one before
/// every session would be theatre.
/// </para>
///
/// <para>
/// It <em>is</em> needed for the small set of changes that write state outliving a reboot:
/// registry values and per-device interrupt affinity policy. Those are the only changes
/// where a bad outcome could survive the machine restarting, so they are the only ones that
/// require the heavier net.
/// </para>
///
/// <para>The full ladder, in order of use:</para>
/// <list type="number">
///   <item>Journal — every change, always, flushed before it is applied.</item>
///   <item>Restore point — before any boot-persistent change.</item>
///   <item>Boot-recovery pass — an auto-start service reverts any journal left dirty.</item>
///   <item>Reboot — guaranteed to work because of rungs one and three.</item>
/// </list>
/// </summary>
public static class SafetyGate
{
    /// <summary>Mutations that would outlive a reboot without help.</summary>
    public static ImmutableArray<string> BootPersistent(IEnumerable<IMutation> mutations)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        return mutations.Where(m => m.IsBootPersistent).Select(m => m.Key).ToImmutableArray();
    }

    public static SafetyDecision Evaluate(
        IEnumerable<IMutation> mutations,
        RestoreStatus restoreStatus,
        bool userAcknowledgedNoRestorePoint = false)
    {
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(restoreStatus);

        var persistent = BootPersistent(mutations);

        if (persistent.IsEmpty)
        {
            return new SafetyDecision
            {
                Verdict = SafetyVerdict.Proceed,
                BootPersistentKeys = persistent,
                Explanation =
                    "Nothing in this session outlives a reboot, so the journal is sufficient. "
                    + "No restore point is needed and none will be created.",
            };
        }

        if (restoreStatus.CanCreate)
        {
            return new SafetyDecision
            {
                Verdict = SafetyVerdict.RequireRestorePoint,
                BootPersistentKeys = persistent,
                Explanation =
                    $"{persistent.Length} change(s) in this session write state that survives a "
                    + "reboot. A System Restore point will be created before any of them is applied.",
            };
        }

        if (userAcknowledgedNoRestorePoint)
        {
            return new SafetyDecision
            {
                Verdict = SafetyVerdict.ProceedWithoutRestorePoint,
                BootPersistentKeys = persistent,
                Explanation =
                    "System Protection is unavailable and you chose to continue. The journal and "
                    + "the boot-recovery pass still apply, so rebooting will still undo these "
                    + "changes — you are giving up the extra net, not the guarantee.",
            };
        }

        return new SafetyDecision
        {
            Verdict = SafetyVerdict.Blocked,
            BootPersistentKeys = persistent,
            Explanation =
                "System Protection is turned off for this drive, so no restore point can be "
                + $"created, and {persistent.Length} change(s) in this session would survive a "
                + "reboot. Turn System Protection on, drop those changes, or confirm you accept "
                + "the risk. GamerGod will not make an unprotected persistent change silently.",
        };
    }

    /// <summary>
    /// The restore point label. Timestamped by the caller rather than generated here so the
    /// value is deterministic and testable.
    /// </summary>
    public static string DescribeRestorePoint(string sessionId, int changeCount) =>
        $"GamerGod {sessionId[..Math.Min(8, sessionId.Length)]} — before {changeCount} persistent change(s)";
}
