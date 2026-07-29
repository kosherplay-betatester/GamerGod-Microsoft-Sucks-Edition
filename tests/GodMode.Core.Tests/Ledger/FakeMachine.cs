using System.Text.Json;
using GodMode.Core.Ledger;
using GodMode.Core.Mutations;

namespace GodMode.Core.Tests.Ledger;

/// <summary>Thrown to simulate the process dying mid-session.</summary>
internal sealed class SimulatedCrash : Exception
{
    public SimulatedCrash()
        : base("Simulated process death")
    {
    }
}

/// <summary>
/// A simulated machine. Every value a mutation could change is one string in a dictionary,
/// which makes "did we put the machine back exactly as it was" a simple equality check.
///
/// <para>
/// This is why <c>GodMode.Core</c> is forbidden from referencing <c>GodMode.Windows</c>: the
/// entire ledger can be exercised thousands of times, with induced crashes, on a build
/// server with no admin rights and nothing real to break.
/// </para>
/// </summary>
internal sealed class FakeMachine
{
    private readonly Dictionary<string, string> _state = new(StringComparer.Ordinal);

    public string this[string key]
    {
        get => _state.TryGetValue(key, out var v) ? v : "<absent>";
        set => _state[key] = value;
    }

    public FakeMachine Snapshot()
    {
        var copy = new FakeMachine();
        foreach (var (k, v) in _state)
        {
            copy._state[k] = v;
        }

        return copy;
    }

    public bool Matches(FakeMachine other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (_state.Count != other._state.Count)
        {
            return false;
        }

        foreach (var (k, v) in _state)
        {
            if (!other._state.TryGetValue(k, out var o) || !string.Equals(v, o, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public string Describe() =>
        string.Join(", ", _state.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>A mutation that sets one key on a <see cref="FakeMachine"/> to a new value.</summary>
internal sealed class FakeMutation(
    FakeMachine machine,
    string key,
    MutationTier tier,
    string newValue,
    MutationVisibility visibility = MutationVisibility.Ambient,
    bool bootPersistent = false) : IMutation
{
    public string Key => key;

    public MutationTier Tier => tier;

    public MutationVisibility Visibility => visibility;

    public bool IsBootPersistent => bootPersistent;

    /// <summary>Set to make apply throw, modelling a service that refuses to stop.</summary>
    public bool FailOnApply { get; init; }

    /// <summary>Set to make revert throw, modelling a change that cannot be undone.</summary>
    public bool FailOnRevert { get; init; }

    public int RevertCount { get; private set; }

    public string Describe() => $"set {key} to {newValue}";

    public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(JsonSerializer.SerializeToElement(new Capture(machine[key])));

    public ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        if (FailOnApply)
        {
            throw new InvalidOperationException($"apply refused for {key}");
        }

        machine[key] = newValue;
        return ValueTask.CompletedTask;
    }

    public ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        RevertCount++;

        if (FailOnRevert)
        {
            throw new InvalidOperationException($"revert refused for {key}");
        }

        var previous = capture.Deserialize<Capture>()!.Previous;
        machine[key] = previous;
        return ValueTask.CompletedTask;
    }

    internal sealed record Capture(string Previous);
}

/// <summary>
/// Rebuilds mutations from journal entries, standing in for the real registry that maps a
/// journalled type name back to an implementation after a cold restart.
/// </summary>
internal sealed class FakeResolver(FakeMachine machine) : IMutationResolver
{
    private readonly Dictionary<string, MutationTier> _tiers = new(StringComparer.Ordinal);

    public HashSet<string> Unresolvable { get; } = new(StringComparer.Ordinal);

    public void Remember(IMutation mutation) => _tiers[mutation.Key] = mutation.Tier;

    public IMutation? Resolve(string mutationType, string key)
    {
        if (Unresolvable.Contains(key))
        {
            return null;
        }

        var tier = _tiers.TryGetValue(key, out var t) ? t : MutationTier.Registry;

        // The value does not matter on the revert path: revert writes back the captured
        // previous value and ignores it entirely.
        return new FakeMutation(machine, key, tier, "<irrelevant>");
    }
}

/// <summary>
/// Wraps a journal and throws once a chosen number of writes have landed, modelling the
/// machine dying at an arbitrary point. Entries written before the crash stay durable,
/// exactly as they would on disk.
/// </summary>
internal sealed class CrashingJournal(IJournal inner, int crashAfterWrites) : IJournal
{
    public int Writes { get; private set; }

    public async ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (Writes >= crashAfterWrites)
        {
            throw new SimulatedCrash();
        }

        Writes++;
        await inner.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<System.Collections.Immutable.ImmutableArray<JournalEntry>> ReadAllAsync(
        CancellationToken cancellationToken) => inner.ReadAllAsync(cancellationToken);
}
