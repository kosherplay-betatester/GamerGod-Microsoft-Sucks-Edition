using System.Text.Json;

namespace GodMode.Core.Mutations;

/// <summary>
/// Whether a change is observable from inside a game process.
///
/// <para>
/// This is the load-bearing type in GodMode. Charter Article I promises that a machine
/// running GodMode is indistinguishable from a normal Windows install, as seen from inside
/// a game. That promise is kept by classifying every single change as either invisible to
/// the game or not, and refusing to apply the visible ones where an anti-cheat is watching.
/// </para>
/// </summary>
public enum MutationVisibility
{
    /// <summary>
    /// Changes only what <em>other</em> processes do, or global OS state the game has no
    /// way to attribute to us. The game's modules, handles, registry view, device list and
    /// environment are all untouched; it simply runs on a quieter machine.
    ///
    /// <para>
    /// Examples: demoting a browser to EcoQoS, confining background work to another
    /// performance domain, stopping the search indexer, swapping the power scheme, steering
    /// device interrupts, closing the shell.
    /// </para>
    ///
    /// <para>Always permitted, for every title, with no exceptions.</para>
    /// </summary>
    Ambient,

    /// <summary>
    /// Touches the game process itself, or state keyed to the game's identity.
    ///
    /// <para>
    /// Examples: setting CPU sets or affinity on the game, writing an
    /// <c>AppCompatFlags\Layers</c> entry named after its executable, suspending it.
    /// </para>
    ///
    /// <para>
    /// Requires a <see cref="GodMode.Core.Policy.ContactGrant"/>, which
    /// <see cref="GodMode.Core.Policy.GameIntegrityPolicy"/> will not issue when kernel
    /// anti-cheat is present or when the anti-cheat situation is unknown.
    /// </para>
    /// </summary>
    Contact,
}

/// <summary>
/// Ordering class for apply and revert. Mutations are applied in ascending tier and
/// reverted in strict descending tier.
///
/// <para>
/// The ordering is deliberate: the shell comes down last and goes back up first, so a user
/// recovering from a crash is looking at a desktop again while the slower service restarts
/// are still finishing. That single choice is the difference between "it recovered" and
/// "it felt broken for twenty seconds".
/// </para>
/// </summary>
public enum MutationTier
{
    /// <summary>Power scheme selection. Reverted last.</summary>
    Power = 0,

    /// <summary>Registry values, including per-device interrupt affinity policy.</summary>
    Registry = 10,

    /// <summary>Service state. Stop only - start type is never modified.</summary>
    Service = 20,

    /// <summary>EcoQoS demotion, working-set trim, job-object confinement, suspension.</summary>
    ProcessDemotion = 30,

    /// <summary>CPU sets and affinity.</summary>
    CpuRouting = 40,

    /// <summary>Shell teardown. Reverted first.</summary>
    Shell = 50,
}

/// <summary>
/// A single reversible change to the machine.
///
/// <para>
/// The contract is deliberately narrow: capture what exists, apply the change, put it back.
/// A change that cannot describe its own inverse cannot be expressed as an
/// <see cref="IMutation"/>, and therefore cannot be shipped — which is exactly the
/// constraint Charter Article VI needs.
/// </para>
/// </summary>
public interface IMutation
{
    /// <summary>
    /// Stable identity, e.g. <c>service:WSearch</c> or <c>ecoqos:pid:4812</c>. Used to
    /// deduplicate repeated applications and to make revert idempotent.
    /// </summary>
    string Key { get; }

    MutationTier Tier { get; }

    MutationVisibility Visibility { get; }

    /// <summary>
    /// True when this change would outlive a reboot without help. Such mutations must also
    /// be registered for boot recovery, because Charter Article VI requires that rebooting
    /// is always a valid escape.
    /// </summary>
    bool IsBootPersistent { get; }

    /// <summary>Human-readable description, shown in the UI and written to the journal.</summary>
    string Describe();

    /// <summary>
    /// Reads current state into a serialisable record. Must not change anything: this runs
    /// before the journal entry is written, and a capture with side effects would leave
    /// changes the journal has no record of.
    /// </summary>
    ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the change. May throw; the ledger records the failure and continues with the
    /// remaining mutations rather than abandoning the session half-applied.
    /// </summary>
    ValueTask ApplyAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Restores the captured state.
    ///
    /// <para>
    /// Must be idempotent and must not throw merely because the change is already undone —
    /// this runs from cold processes after crashes and at boot, where the current state is
    /// unknown. Throwing on "nothing to do" would abort recovery for everything after it.
    /// </para>
    /// </summary>
    ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken);
}
