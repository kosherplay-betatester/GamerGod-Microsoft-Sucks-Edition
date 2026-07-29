using System.Collections.Immutable;
using System.Text.Json;
using GodMode.Core.Mutations;
using GodMode.Core.Policy;

namespace GodMode.Core.Ledger;

/// <summary>Outcome of applying a set of mutations.</summary>
public sealed record ApplyReport
{
    public required string SessionId { get; init; }

    public required ImmutableArray<string> Applied { get; init; }

    /// <summary>Refused by <see cref="MutationPermit"/> — almost always contact on a protected title.</summary>
    public required ImmutableArray<string> Refused { get; init; }

    public required ImmutableArray<(string Key, string Error)> Failed { get; init; }

    public bool FullySucceeded => Failed.IsEmpty;
}

/// <summary>Outcome of a revert or recovery pass.</summary>
public sealed record RevertReport
{
    public required ImmutableArray<string> Reverted { get; init; }

    public required ImmutableArray<(string Key, string Error)> Failed { get; init; }

    /// <summary>Journalled entries whose implementation could not be rebuilt.</summary>
    public required ImmutableArray<string> Unresolvable { get; init; }

    /// <summary>
    /// True when the machine is provably back to its original state. When false the journal
    /// is deliberately left in place so the next boot retries.
    /// </summary>
    public bool IsClean => Failed.IsEmpty && Unresolvable.IsEmpty;
}

/// <summary>
/// Applies and reverses machine changes with a durable record of every step.
///
/// <para>
/// Three rules make Charter Article VI true rather than aspirational:
/// </para>
/// <list type="number">
///   <item>Capture is journalled and flushed <em>before</em> the change is applied, so a
///   crash can never leave a change the journal does not know about.</item>
///   <item>Revert walks strictly descending tier, so the shell returns before the slower
///   service restarts finish and the user sees a desktop immediately.</item>
///   <item>A failing revert is recorded and the chain continues. One stubborn service must
///   never strand everything after it.</item>
/// </list>
/// </summary>
public sealed class MutationLedger(IJournal journal, IMutationResolver resolver)
{
    private readonly IJournal _journal = journal ?? throw new ArgumentNullException(nameof(journal));
    private readonly IMutationResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public async ValueTask<ApplyReport> ApplyAsync(
        string sessionId,
        IEnumerable<IMutation> mutations,
        MutationPermit permit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(permit);

        var proposed = mutations.ToImmutableArray();
        var allowed = permit.Filter(proposed);
        var refused = proposed.Where(m => !permit.Allows(m)).Select(m => m.Key).ToImmutableArray();

        await _journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = sessionId,
                Description = permit.Explain(),
            },
            cancellationToken).ConfigureAwait(false);

        var applied = ImmutableArray.CreateBuilder<string>();
        var failed = ImmutableArray.CreateBuilder<(string, string)>();

        foreach (var mutation in allowed.OrderBy(m => m.Tier))
        {
            JsonElement capture;
            try
            {
                capture = await mutation.CaptureAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing has changed yet, so this is simply skipped. Applying without a
                // capture would create a change we could not undo.
                failed.Add((mutation.Key, $"capture failed: {ex.Message}"));
                await AppendFailure(sessionId, mutation, ex, cancellationToken).ConfigureAwait(false);
                continue;
            }

            await _journal.AppendAsync(
                new JournalEntry
                {
                    Op = JournalOp.Capture,
                    SessionId = sessionId,
                    Key = mutation.Key,
                    MutationType = mutation.GetType().FullName ?? mutation.GetType().Name,
                    Tier = mutation.Tier,
                    Visibility = mutation.Visibility,
                    IsBootPersistent = mutation.IsBootPersistent,
                    State = capture.GetRawText(),
                    Description = mutation.Describe(),
                },
                cancellationToken).ConfigureAwait(false);

            try
            {
                await mutation.ApplyAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failed.Add((mutation.Key, ex.Message));
                await AppendFailure(sessionId, mutation, ex, cancellationToken).ConfigureAwait(false);

                // The capture line is already durable, so revert will still put this back
                // even though apply threw. Partial application is recoverable; that is the
                // reason capture goes first.
                continue;
            }

            applied.Add(mutation.Key);

            await _journal.AppendAsync(
                new JournalEntry { Op = JournalOp.Applied, SessionId = sessionId, Key = mutation.Key },
                cancellationToken).ConfigureAwait(false);
        }

        return new ApplyReport
        {
            SessionId = sessionId,
            Applied = applied.ToImmutable(),
            Refused = refused,
            Failed = failed.ToImmutable(),
        };
    }

    /// <summary>
    /// Reverts everything the journal records, whether or not this process applied it.
    ///
    /// <para>
    /// This is the entry point for all five recovery triggers: user toggle, game exit,
    /// watchdog timeout, logoff, and the boot-time pass over a journal left behind by a
    /// machine that lost power. It must therefore assume nothing about current state.
    /// </para>
    /// </summary>
    public async ValueTask<RevertReport> RevertAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        // The FIRST capture for a key is the state to restore, not the most recent one.
        //
        // If two mutations touch the same key, the second captured a value GodMode had
        // already changed. Restoring that would leave the machine holding our own
        // intermediate value and reporting success — the exact silent corruption this
        // ledger exists to prevent. Once a key is fully reverted its record is dropped, so
        // a later capture correctly becomes the new original.
        var captures = new Dictionary<string, JournalEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            switch (entry.Op)
            {
                case JournalOp.Capture:
                    if (!captures.ContainsKey(entry.Key))
                    {
                        captures[entry.Key] = entry;
                    }

                    break;
                case JournalOp.Reverted:
                    captures.Remove(entry.Key);
                    break;
            }
        }

        var outstanding = captures.Values
            .OrderByDescending(e => e.Tier)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToImmutableArray();

        var done = ImmutableArray.CreateBuilder<string>();
        var failed = ImmutableArray.CreateBuilder<(string, string)>();
        var unresolvable = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in outstanding)
        {
            var mutation = _resolver.Resolve(entry.MutationType, entry.Key);
            if (mutation is null)
            {
                unresolvable.Add(entry.Key);
                continue;
            }

            JsonElement state;
            try
            {
                using var document = JsonDocument.Parse(entry.State ?? "{}");
                state = document.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                failed.Add((entry.Key, $"unreadable capture: {ex.Message}"));
                continue;
            }

            try
            {
                await mutation.RevertAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Recorded, then on to the next one. A single failure must never strand the
                // shell or leave services stopped.
                failed.Add((entry.Key, ex.Message));
                await _journal.AppendAsync(
                    new JournalEntry
                    {
                        Op = JournalOp.RevertFailed,
                        SessionId = entry.SessionId,
                        Key = entry.Key,
                        Error = ex.Message,
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            done.Add(entry.Key);

            await _journal.AppendAsync(
                new JournalEntry { Op = JournalOp.Reverted, SessionId = entry.SessionId, Key = entry.Key },
                cancellationToken).ConfigureAwait(false);
        }

        var report = new RevertReport
        {
            Reverted = done.ToImmutable(),
            Failed = failed.ToImmutable(),
            Unresolvable = unresolvable.ToImmutable(),
        };

        if (report.IsClean && outstanding.Length > 0)
        {
            await _journal.AppendAsync(
                new JournalEntry { Op = JournalOp.SessionEnd, SessionId = outstanding[0].SessionId },
                cancellationToken).ConfigureAwait(false);
        }

        return report;
    }

    /// <summary>
    /// True when the journal describes changes that are still applied — the dirty flag a
    /// service checks at boot.
    /// </summary>
    public async ValueTask<bool> HasOutstandingChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        var outstanding = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Op == JournalOp.Capture)
            {
                outstanding.Add(entry.Key);
            }
            else if (entry.Op == JournalOp.Reverted)
            {
                outstanding.Remove(entry.Key);
            }
        }

        return outstanding.Count > 0;
    }

    private ValueTask AppendFailure(
        string sessionId,
        IMutation mutation,
        Exception exception,
        CancellationToken cancellationToken) =>
        _journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.Failed,
                SessionId = sessionId,
                Key = mutation.Key,
                Tier = mutation.Tier,
                Error = exception.Message,
            },
            cancellationToken);
}
