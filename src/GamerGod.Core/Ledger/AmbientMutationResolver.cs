using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Mutations;

namespace GamerGod.Core.Ledger;

/// <summary>
/// Rebuilds ambient mutations from a journal, so a process that never applied anything can
/// still undo it.
///
/// <para>
/// Every revert goes through here, including the ones inside the process that did the
/// applying — the ledger always resolves from the journal rather than from objects it is
/// holding, because the interesting case is the cold one: a service starting after a
/// bugcheck, <c>gamergod off</c> from a fresh shell, the boot pass on a machine that lost
/// power. A key this cannot rebuild is a change that stays on the machine.
/// </para>
///
/// <para>
/// There used to be two of these, both private to the command-line tool, and they had
/// drifted: the copy behind <c>gamergod bench</c> had no case for
/// <c>confine:ambient-domain</c>, so an interrupted measurement run left a journal it could
/// not clear. One copy, in the domain, is the fix — and the reason this class is public
/// despite having exactly one method.
/// </para>
/// </summary>
public sealed class AmbientMutationResolver : IMutationResolver
{
    private const string ServicePrefix = "service:";

    private readonly IAmbientOperations _os;
    private readonly CpuTopology _topology;

    public AmbientMutationResolver(IAmbientOperations os, CpuTopology topology)
    {
        _os = os ?? throw new ArgumentNullException(nameof(os));
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    }

    /// <summary>
    /// Rebuilds from the key, not the journalled type name.
    ///
    /// <para>
    /// The key is the identity <see cref="IMutation"/> documents as stable; a type name moves
    /// whenever somebody renames a namespace. Binding to the type would make a refactor
    /// unrecoverable for anyone whose journal was written by the previous build, which is
    /// precisely the person recovery exists for.
    /// </para>
    /// </summary>
    public IMutation? Resolve(string mutationType, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        if (key.StartsWith(ServicePrefix, StringComparison.Ordinal))
        {
            var name = key[ServicePrefix.Length..];

            // "service:" with nothing after it would build a mutation that tries to start a
            // service with no name. Unresolvable is the honest answer.
            return string.IsNullOrWhiteSpace(name) ? null : new ServiceSuspensionMutation(_os, name);
        }

        return key switch
        {
            "ecoqos:background" => new EfficiencyModeMutation(_os, ImmutableArray<ProcessInfo>.Empty),

            "power:scheme" => new PowerSchemeMutation(_os, "GamerGod"),

            // Affinity outlives the process that set it, so unlike a job object it has to be
            // put back deliberately. The masks come from the journalled capture; the empty
            // target list here is correct, because a cold rebuild knows nothing about which
            // processes were affected and must not invent any.
            "affinity:ambient-domain" => new AffinityConfinementMutation(
                _os, default, ImmutableArray<ProcessInfo>.Empty, _topology),

            // A job object's limits exist only while a handle to it is open, so a confinement
            // held by a process that has since exited was lifted by Windows before this code
            // ran. There is nothing to undo, and saying so is what lets the ledger retire the
            // entry instead of carrying it forever.
            "confine:ambient-domain" => new ReleasedConfinementMutation(),

            // Unknown - a journal from a newer build, most likely. The ledger records it as
            // unresolvable and leaves it in place so a later version can finish the job.
            _ => null,
        };
    }
}

/// <summary>
/// A background confinement whose owning process is gone.
///
/// <para>
/// This is the strongest reversal guarantee GamerGod has, and it belongs to Windows rather
/// than to us: a job object's affinity limit exists only while a handle to it is open, so a
/// crash, a kill or a power loss lifts it without any of our code running. Recovery's whole
/// job here is to agree that there is nothing left to do.
/// </para>
/// </summary>
public sealed class ReleasedConfinementMutation : IMutation
{
    public string Key => "confine:ambient-domain";

    public MutationTier Tier => MutationTier.ProcessDemotion;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    public string Describe() => "release background confinement";

    public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        // An empty object rather than a default JsonElement: the ledger writes whatever
        // comes back here into the journal, and a default element throws on GetRawText
        // instead of serialising. Cloned so it does not depend on a document that is about
        // to return its buffer to the pool.
        using var empty = JsonDocument.Parse("{}");
        return ValueTask.FromResult(empty.RootElement.Clone());
    }

    public ValueTask ApplyAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
