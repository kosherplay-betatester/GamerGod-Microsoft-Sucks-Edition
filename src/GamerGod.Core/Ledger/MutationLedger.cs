using System.Collections.Immutable;
using System.Text.Json;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;

namespace GamerGod.Core.Ledger;

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
/// A session the journal still shows as having changed this machine, and who armed it.
///
/// <para>
/// The ledger reports the recorded identity and does not probe it. Deciding whether a process
/// is still running is an operating-system question, and answering it here would put an OS
/// call in the one project that must not have any — which is what lets the whole ledger be
/// driven against a simulated machine, crashes and all.
/// </para>
/// </summary>
public sealed record OutstandingSession
{
    public required string SessionId { get; init; }

    /// <summary>
    /// The process that armed it, or null when the journal records none — an older journal, or
    /// a caller that applied and exited.
    /// </summary>
    public ProcessIdentity? Owner { get; init; }

    /// <summary>
    /// True when the machine has restarted since this session began, in which case the owner
    /// is gone whatever any probe says now.
    /// </summary>
    public required bool MachineRestartedSince { get; init; }

    /// <summary>
    /// True when every change this session captured also reached a terminal entry — applied,
    /// or recorded as failed.
    /// </summary>
    ///
    /// <para>
    /// This is what separates the two situations that otherwise leave an identical journal:
    /// a session that was armed deliberately and whose tool then exited, and a session whose
    /// process died half way through applying. Both are ownerless, both are in the current
    /// boot, and neither has a <see cref="JournalOp.SessionEnd"/> — that is only written by a
    /// clean revert.
    /// </para>
    ///
    /// <para>
    /// The difference matters because the machine is in a different condition. A completed
    /// session left the machine exactly where the user asked for it, and reverting it would
    /// overrule them. An interrupted one left the machine in a state nobody chose — some
    /// changes applied, others not — and that is what boot recovery exists to undo.
    /// </para>
    ///
    /// <para>
    /// Found the hard way: the first attempt at ownership treated "no owner" as "orphaned",
    /// which meant `gamergod on` followed by a service restart turned Game Mode off. Only a
    /// real machine showed it, because the command-line tool applies and exits by design and
    /// every test had been written against a fake that did the same.
    /// </para>
    public required bool ApplyCompleted { get; init; }
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

    /// <summary>
    /// The mutation objects this ledger applied, by key, while it is still the process that
    /// applied them.
    ///
    /// <para>
    /// Some changes cannot be described entirely by their journalled capture, because they are
    /// held open by a resource rather than written to the machine: a job object's affinity
    /// limit exists only while a handle to it is open, and the handle belongs to the object
    /// that opened it. Reverting through a copy rebuilt from the journal closes nothing, so
    /// the confinement stays in force while the journal reports it lifted.
    /// </para>
    ///
    /// <para>
    /// A list per key, not a single object: nothing stops a caller applying the same key twice
    /// before reverting, and each apply opens its own resource. Remembering only the most
    /// recent would close one handle and leak the other.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, List<IMutation>> _live = new(StringComparer.Ordinal);

    private readonly Lock _liveGate = new();

    public ValueTask<ApplyReport> ApplyAsync(
        string sessionId,
        IEnumerable<IMutation> mutations,
        MutationPermit permit,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(sessionId, mutations, permit, owner: null, cancellationToken);

    /// <summary>
    /// Applies a session on behalf of a named process.
    ///
    /// <para>
    /// The owner is the process whose death means this session has been orphaned. Supplying
    /// one is what lets a boot-recovery pass leave a live session alone instead of ending it;
    /// callers that apply and exit correctly supply none, because there is nobody left to
    /// claim the session and it must be recovered rather than stranded.
    /// </para>
    /// </summary>
    public async ValueTask<ApplyReport> ApplyAsync(
        string sessionId,
        IEnumerable<IMutation> mutations,
        MutationPermit permit,
        ProcessIdentity? owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(permit);

        var proposed = mutations.ToImmutableArray();
        var allowed = permit.Filter(proposed);
        var refused = proposed.Where(m => !permit.Allows(m)).Select(m => m.Key).ToImmutableArray();

        // Held for the whole operation. A revert that overlapped this would see a capture
        // whose change has not landed yet, undo nothing, and permanently cancel the capture -
        // leaving the machine modified with the journal reporting clean.
        await using var scope = await _journal
            .AcquireExclusiveAsync(cancellationToken)
            .ConfigureAwait(false);

        await _journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = sessionId,
                Description = permit.Explain(),

                // Stamped so a later read can tell whether the machine has restarted since.
                // Without it the journal outlives a reboot and reports changes as still
                // applied when the operating system already discarded every one of them.
                MachineBootedAtUtcTicks = MachineBoot.At().UtcTicks,

                // Recorded so boot recovery can tell a session orphaned by a crash from one
                // that is still running. Both halves of the identity or neither: a bare
                // process id cannot be told apart from the same id belonging to something
                // else, and recovery treats a half-written owner as no owner.
                OwnerProcessId = owner?.ProcessId ?? 0,
                OwnerStartedAtUtcTicks = owner?.StartedAtUtcTicks ?? 0,
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

            // Remembered here rather than after a successful apply, because capture is
            // journalled first precisely so a half-applied change is still recoverable — and
            // the object that got half way through is the one that knows what it managed to
            // do. A mutation whose apply threw must still revert through itself.
            Remember(mutation);

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
    public ValueTask<RevertReport> RevertAsync(CancellationToken cancellationToken = default) =>
        RevertCoreAsync(retainedSessions: null, cancellationToken);

    /// <summary>
    /// Reverts everything except the named sessions.
    ///
    /// <para>
    /// This is boot recovery's entry point, and only boot recovery's: a user asking for
    /// <c>gamergod off</c> means all of it, whoever armed it. Retaining a session is what
    /// stops a service restart ending one that is still running.
    /// </para>
    /// </summary>
    public ValueTask<RevertReport> RevertExceptAsync(
        IReadOnlySet<string> retainedSessions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(retainedSessions);
        return RevertCoreAsync(retainedSessions, cancellationToken);
    }

    private async ValueTask<RevertReport> RevertCoreAsync(
        IReadOnlySet<string>? retainedSessions, CancellationToken cancellationToken)
    {
        // Excludes any concurrent apply. Without this the snapshot below can be stale before
        // it is even read, and acting on it destroys the record of a change still in flight.
        await using var scope = await _journal
            .AcquireExclusiveAsync(cancellationToken)
            .ConfigureAwait(false);

        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var captures = OutstandingKeys(entries, RestartedSessions(entries));

        var outstanding = captures.Values
            // A key a retained session still holds is left exactly as it is. Neither answer
            // available here is right: the retained session's capture is the user's original
            // and restoring it would end the live session, while a later session's capture is
            // a value GamerGod itself wrote and restoring that would be corruption reported as
            // a revert. Doing nothing is the only honest option until the live session ends.
            .Where(k => retainedSessions is null || !k.Sessions.Any(retainedSessions.Contains))
            .Select(k => k.First)
            .OrderByDescending(e => e.Tier)
            .ThenBy(e => e.Key, StringComparer.Ordinal)
            .ToImmutableArray();

        var done = ImmutableArray.CreateBuilder<string>();
        var failed = ImmutableArray.CreateBuilder<(string, string)>();
        var unresolvable = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in outstanding)
        {
            // The objects this ledger applied, when it is still the process that applied them.
            // Preferred over a rebuild because a rebuilt copy holds no handle: reverting one
            // closes no job object and leaves the confinement in force while reporting it
            // lifted. The journal stays the source of truth for the cold case below, which is
            // the case recovery exists for.
            var live = LiveInstances(entry.Key);

            if (live.IsEmpty)
            {
                var rebuilt = _resolver.Resolve(entry.MutationType, entry.Key);
                if (rebuilt is null)
                {
                    unresolvable.Add(entry.Key);
                    continue;
                }

                live = [rebuilt];
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

            string? error = null;

            // Every instance is attempted, even after one fails.
            //
            // This used to break on the first exception, and LiveInstances returns newest
            // first, so an instance that failed permanently sat at the head of the list and
            // every older one behind it was never reverted at all. Forget only runs on
            // success, so the next attempt rebuilt the same order and stopped in the same
            // place — a DomainConfinementMutation's job object stayed open for the life of the
            // process, holding its affinity limit, while the report said only that the key
            // could not be put back.
            //
            // That contradicted this class's own rule three lines below: one stubborn resource
            // must never strand everything after it. The rule was applied between keys and not
            // within one.
            foreach (var mutation in live)
            {
                try
                {
                    await mutation.RevertAsync(state, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The first failure is the one reported. Later ones are recorded in the
                    // same line rather than overwriting it, because a key with two broken
                    // instances is a different situation from one with a single failure.
                    error = error is null ? ex.Message : $"{error}; {ex.Message}";
                }
            }

            if (error is not null)
            {
                // Recorded, then on to the next one. A single failure must never strand the
                // shell or leave services stopped. The objects are kept, so the next attempt
                // in this process still reaches whatever resource they hold.
                failed.Add((entry.Key, error));
                await _journal.AppendAsync(
                    new JournalEntry
                    {
                        Op = JournalOp.RevertFailed,
                        SessionId = entry.SessionId,
                        Key = entry.Key,
                        Error = error,
                    },
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Dropped only once the entry is genuinely retired. Revert is idempotent and gets
            // called again — by the watchdog, by 'gamergod off', by the boot pass — and
            // holding the object past that point would release the same resource twice.
            Forget(entry.Key);
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

        // One per session that actually ended, not one naming whichever session happened to sort
        // first. A revert routinely spans more than one: a crashed session and the one that
        // replaced it are both outstanding, and both are put back by the same pass. Naming only
        // outstanding[0] left every other session in the journal with no terminal entry, so the
        // record said they were still applied for ever after — and Compact below, which is the
        // only thing that stops these files growing without bound, would have believed it.
        if (report.IsClean)
        {
            foreach (var session in outstanding
                .Select(e => e.SessionId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal))
            {
                await _journal.AppendAsync(
                    new JournalEntry { Op = JournalOp.SessionEnd, SessionId = session },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // Still inside the exclusive scope, which is the only place this is safe.
        await CompactAsync(cancellationToken).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Above this many lines, a revert drops the history of sessions that are finished with.
    ///
    /// <para>
    /// High enough that an ordinary machine never reaches it and the journal stays a readable
    /// account of recent sessions — which is what a user or an auditor opens it for. A single
    /// armed session writes on the order of ten lines, so this is hundreds of sessions.
    /// </para>
    /// </summary>
    private const int CompactionThreshold = 2000;

    /// <summary>
    /// Drops journal lines that describe nothing about the machine's current state.
    ///
    /// <para>
    /// <b>What is kept is decided by the same function that decides what to revert.</b> A
    /// session survives compaction when it still owns an outstanding key —
    /// <see cref="OutstandingKeys"/>, the one place in this class that defines "still applied",
    /// including its rule that a non-boot-persistent capture from a previous boot is already
    /// undone. Anything a second definition kept or dropped differently would eventually
    /// disagree with the revert path, and the direction it disagreed in would decide whether the
    /// machine could be put back.
    /// </para>
    ///
    /// <para>
    /// A retained session keeps <em>all</em> of its lines, not just the outstanding captures.
    /// Its <see cref="JournalOp.SessionBegin"/> carries the boot timestamp that
    /// <see cref="RestartedSessions"/> reads, and dropping that would make a pre-reboot session
    /// look current — reversing the very rule this method depends on.
    /// </para>
    ///
    /// <para>
    /// The caller must already hold the journal's exclusive scope. Reading, deciding and
    /// replacing are three steps, and a concurrent apply landing between the first and the third
    /// would be erased by it.
    /// </para>
    /// </summary>
    private async ValueTask CompactAsync(CancellationToken cancellationToken)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);

        if (entries.Length < CompactionThreshold)
        {
            return;
        }

        var keep = OutstandingKeys(entries, RestartedSessions(entries))
            .Values
            .SelectMany(k => k.Sessions)
            .ToHashSet(StringComparer.Ordinal);

        await _journal
            .ReplaceAllAsync(
                entries.Where(e => keep.Contains(e.SessionId)).ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// True when the journal describes changes that are still applied — the dirty flag a
    /// service checks at boot.
    /// </summary>
    public async ValueTask<bool> HasOutstandingChangesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return OutstandingKeys(entries, RestartedSessions(entries)).Count > 0;
    }

    /// <summary>
    /// Every session the journal still shows as having changed this machine, with the owner it
    /// recorded — what boot recovery needs in order to tell an orphan from a live session.
    ///
    /// <para>
    /// Ordered by session id so a caller's report reads the same way twice.
    /// </para>
    /// </summary>
    public async ValueTask<ImmutableArray<OutstandingSession>> OutstandingSessionsAsync(
        CancellationToken cancellationToken = default)
    {
        var entries = await _journal.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        var restarted = RestartedSessions(entries);
        var owners = new Dictionary<string, ProcessIdentity?>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Op != JournalOp.SessionBegin)
            {
                continue;
            }

            // Both halves or neither. A recorded id with no start time is indistinguishable
            // from the same id belonging to something else, and reporting it as an identity
            // would let a chance match keep a stranded machine stranded.
            owners[entry.SessionId] = entry is { OwnerProcessId: > 0, OwnerStartedAtUtcTicks: > 0 }
                ? new ProcessIdentity(entry.OwnerProcessId, entry.OwnerStartedAtUtcTicks)
                : null;
        }

        // A capture with no terminal entry means the process died between writing the record
        // and finishing the work. Counted per session and per key, because a session is only
        // complete when every one of its changes is.
        var pending = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Key is not { Length: > 0 } key)
            {
                continue;
            }

            switch (entry.Op)
            {
                case JournalOp.Capture:
                    if (!pending.TryGetValue(entry.SessionId, out var open))
                    {
                        open = new HashSet<string>(StringComparer.Ordinal);
                        pending[entry.SessionId] = open;
                    }

                    open.Add(key);
                    break;

                // Failed counts as terminal. The change did not happen, the session knew it,
                // and it carried on - that is a completed apply with a hole in it, not an
                // interrupted one.
                case JournalOp.Applied:
                case JournalOp.Failed:
                    if (pending.TryGetValue(entry.SessionId, out var settled))
                    {
                        settled.Remove(key);
                    }

                    break;
            }
        }

        return OutstandingKeys(entries, restarted)
            .Values
            .SelectMany(k => k.Sessions)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(sessionId => new OutstandingSession
            {
                SessionId = sessionId,
                Owner = owners.TryGetValue(sessionId, out var owner) ? owner : null,
                MachineRestartedSince = restarted.Contains(sessionId),
                ApplyCompleted = !pending.TryGetValue(sessionId, out var unfinished)
                                 || unfinished.Count == 0,
            })
            .ToImmutableArray();
    }

    /// <summary>
    /// The keys the journal still shows as changed, each with the capture to restore and the
    /// sessions that touched it.
    ///
    /// <para>
    /// The FIRST capture for a key is the state to restore, not the most recent one. If two
    /// mutations touch the same key, the second captured a value GamerGod had already changed.
    /// Restoring that would leave the machine holding our own intermediate value and reporting
    /// success — the exact silent corruption this ledger exists to prevent. Once a key is
    /// fully reverted its record is dropped, so a later capture correctly becomes the new
    /// original.
    /// </para>
    /// </summary>
    private static Dictionary<string, OutstandingKey> OutstandingKeys(
        ImmutableArray<JournalEntry> entries, HashSet<string> restarted)
    {
        var captures = new Dictionary<string, OutstandingKey>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            switch (entry.Op)
            {
                case JournalOp.Capture:
                    // A change that cannot survive a reboot, from a session that predates the
                    // current boot, is already undone - Windows did it when the process died.
                    // Trying to "restore" it now would target process ids that belong to
                    // something else entirely.
                    if (!entry.IsBootPersistent && restarted.Contains(entry.SessionId))
                    {
                        break;
                    }

                    if (captures.TryGetValue(entry.Key, out var existing))
                    {
                        existing.Sessions.Add(entry.SessionId);
                    }
                    else
                    {
                        captures[entry.Key] = new OutstandingKey(
                            entry, new HashSet<string>(StringComparer.Ordinal) { entry.SessionId });
                    }

                    break;
                case JournalOp.Reverted:
                    captures.Remove(entry.Key);
                    break;
            }
        }

        return captures;
    }

    /// <summary>One outstanding key: the capture that restores it, and who captured it.</summary>
    private sealed record OutstandingKey(JournalEntry First, HashSet<string> Sessions);

    private void Remember(IMutation mutation)
    {
        lock (_liveGate)
        {
            if (!_live.TryGetValue(mutation.Key, out var instances))
            {
                _live[mutation.Key] = instances = [];
            }

            instances.Add(mutation);
        }
    }

    /// <summary>
    /// Everything this ledger applied for a key, newest first — if a key was applied twice
    /// without a revert between, the most recent object holds the resource currently in force,
    /// and unwinding in the order it was wound is what the operating system expects.
    /// </summary>
    private ImmutableArray<IMutation> LiveInstances(string key)
    {
        lock (_liveGate)
        {
            if (!_live.TryGetValue(key, out var instances))
            {
                return [];
            }

            return [.. Enumerable.Reverse(instances)];
        }
    }

    private void Forget(string key)
    {
        lock (_liveGate)
        {
            _live.Remove(key);
        }
    }

    /// <summary>
    /// Sessions that began before the machine's current boot.
    ///
    /// <para>
    /// Everything they changed about a process is already undone, because the processes
    /// themselves are gone. Recognising that is what keeps the interface honest after a
    /// restart: without it the journal survives the reboot, the ledger sees captures with no
    /// matching reverts, and GamerGod announces that Game Mode is still on while the machine
    /// is in fact untouched.
    /// </para>
    /// </summary>
    private static HashSet<string> RestartedSessions(ImmutableArray<JournalEntry> entries)
    {
        var now = MachineBoot.At();
        var restarted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Op == JournalOp.SessionBegin
                && MachineBoot.RestartedSince(entry.MachineBootedAtUtcTicks, now))
            {
                restarted.Add(entry.SessionId);
            }
        }

        return restarted;
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
