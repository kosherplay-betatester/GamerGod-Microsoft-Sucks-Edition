using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GamerGod.Core.Ledger;

namespace GamerGod.Core.Engine;

/// <summary>Whether an account describes changes going on or coming back off.</summary>
public enum ChangeDirection
{
    Applied,
    Restored,
}

/// <summary>
/// One thing that happened, and how many programs it happened to.
/// </summary>
public sealed record ChangeGroup
{
    /// <summary>What was done, in the words the user reads.</summary>
    public required string Action { get; init; }

    public required int Count { get; init; }

    /// <summary>A few names, so the number means something concrete.</summary>
    public required ImmutableArray<string> Examples { get; init; }

    /// <summary>"64 programs" / "1 program".</summary>
    public string Amount => Count == 1 ? "1 program" : $"{Count} programs";

    /// <summary>
    /// "Chrome, Discord, Slack and 61 more" — enough to recognise the list as yours without
    /// printing it.
    /// </summary>
    public string Describe()
    {
        if (Examples.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        var shown = string.Join(", ", Examples);
        var rest = Count - Examples.Length;

        return rest > 0 ? $"{shown} and {rest} more" : shown;
    }
}

/// <summary>One process and everything that happened to it.</summary>
public sealed record ProcessChange
{
    public required int ProcessId { get; init; }

    public required string Name { get; init; }

    /// <summary>What was done, in the order the tiers were applied.</summary>
    public required ImmutableArray<string> Changes { get; init; }

    public string Describe() =>
        $"{Name} ({ProcessId.ToString(CultureInfo.InvariantCulture)}) — {string.Join(", ", Changes)}";

    /// <summary>
    /// The program's name as a person would say it, without the process id.
    ///
    /// <para>
    /// Process ids belong in a log, not on a dashboard. Nobody reading "was my browser moved?"
    /// is helped by <c>(12044)</c>, and sixty lines each carrying one is what made this card
    /// unreadable.
    /// </para>
    /// </summary>
    public string FriendlyName => Name;
}

/// <summary>
/// What a session actually did to this machine, process by process, read back from the journal.
///
/// <para>
/// <b>Read from the journal rather than from a receipt, and that is the point.</b> A receipt only
/// exists in the process that applied — and the desktop app usually is not that process, because
/// writing the journal needs administrator rights it does not have, so it shells out. It gets
/// back an exit code. The journal is the one account both paths share, it is the same file the
/// service recovers from, and it is written before anything is changed rather than after.
/// </para>
///
/// <para>
/// So the list a user reads is the list a crash would be recovered from. If those two could
/// disagree, the interface would be describing a machine nobody has.
/// </para>
/// </summary>
public sealed record SessionAccount
{
    public required string SessionId { get; init; }

    public required ChangeDirection Direction { get; init; }

    /// <summary>Every process this session touched, ordered by name.</summary>
    public required ImmutableArray<ProcessChange> Processes { get; init; }

    /// <summary>
    /// Changes that are not about a process — a stopped service, the power plan. Kept apart
    /// because they are a different kind of thing and a user scanning for their browser should
    /// not have to read past them.
    /// </summary>
    public required ImmutableArray<string> Machine { get; init; }

    public bool IsEmpty => Processes.IsEmpty && Machine.IsEmpty;

    /// <summary>
    /// One sentence a person can read at a glance.
    ///
    /// <para>
    /// The card used to open with a list. On a real desktop that is sixty lines of
    /// <c>name (1234) — moved off your game's cores, set to efficiency mode</c>, which answers
    /// "what happened to my machine" only if you are willing to read all sixty. This answers it
    /// in one line, and the detail stays available underneath for anyone who wants it.
    /// </para>
    /// </summary>
    public string Headline
    {
        get
        {
            if (Processes.IsEmpty)
            {
                return Machine.IsEmpty
                    ? "Nothing needed changing."
                    : Direction == ChangeDirection.Applied
                        ? "Your machine was already out of your games' way."
                        : "Your machine is exactly as it was.";
            }

            var n = Processes.Length;
            var programs = n == 1 ? "1 background program" : $"{n} background programs";

            return Direction == ChangeDirection.Applied
                ? $"{programs} moved out of your games' way."
                : $"{programs} put back exactly as they were.";
        }
    }

    /// <summary>
    /// What happened, grouped by the thing that happened rather than by which program it
    /// happened to — because "58 programs were set to efficiency mode" is the fact, and the
    /// names are the footnote.
    /// </summary>
    public ImmutableArray<ChangeGroup> Groups =>
    [
        .. Processes
            .SelectMany(p => p.Changes.Select(change => (change, p.Name)))
            .GroupBy(x => x.change, StringComparer.Ordinal)
            .Select(g => new ChangeGroup
            {
                Action = g.Key,
                Count = g.Count(),
                Examples = [.. g.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Take(6)],
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Action, StringComparer.Ordinal),
    ];

    public static SessionAccount Empty { get; } = new()
    {
        SessionId = string.Empty,
        Direction = ChangeDirection.Applied,
        Processes = [],
        Machine = [],
    };

    /// <summary>
    /// The most recent session in a journal, described.
    ///
    /// <para>
    /// Most recent by position, because the journal is append-only and written in order. A
    /// session id is a GUID, so sorting them would order by nothing at all.
    /// </para>
    /// </summary>
    public static SessionAccount ReadLatest(
        ImmutableArray<JournalEntry> entries, ChangeDirection direction)
    {
        if (entries.IsDefaultOrEmpty)
        {
            return Empty;
        }

        var sessionId = entries
            .Where(e => e.Op == JournalOp.SessionBegin)
            .Select(e => e.SessionId)
            .LastOrDefault();

        return sessionId is null ? Empty : Read(entries, sessionId, direction);
    }

    /// <summary>One named session, described.</summary>
    public static SessionAccount Read(
        ImmutableArray<JournalEntry> entries, string sessionId, ChangeDirection direction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (entries.IsDefaultOrEmpty)
        {
            return Empty;
        }

        // Keyed by pid, because the two process levers overlap almost entirely: the same process
        // is usually both moved and demoted, and a user wants one line about it rather than two.
        var byProcess = new Dictionary<int, (string Name, List<string> Changes)>();
        var machine = ImmutableArray.CreateBuilder<string>();

        foreach (var entry in entries)
        {
            if (entry.Op != JournalOp.Capture
                || !string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal)
                || entry.State is not { Length: > 0 } state)
            {
                continue;
            }

            try
            {
                Describe(entry.Key, state, direction, byProcess, machine);
            }
            catch (JsonException)
            {
                // A capture this build cannot read — an older journal, a mutation type since
                // changed. The rest of the account is still worth showing, and the ledger's own
                // handling of an unreadable capture is the thing that matters for recovery.
            }
        }

        return new SessionAccount
        {
            SessionId = sessionId,
            Direction = direction,
            Machine = machine.ToImmutable(),
            Processes =
            [
                .. byProcess
                    .Select(p => new ProcessChange
                    {
                        ProcessId = p.Key,
                        Name = p.Value.Name,
                        Changes = [.. p.Value.Changes],
                    })
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.ProcessId),
            ],
        };
    }

    private static void Describe(
        string key,
        string state,
        ChangeDirection direction,
        Dictionary<int, (string Name, List<string> Changes)> byProcess,
        ImmutableArray<string>.Builder machine)
    {
        switch (key)
        {
            case "affinity:ambient-domain":
            {
                var capture = JsonSerializer.Deserialize(state, AmbientJson.Default.AffinityCapture);

                foreach (var record in capture?.Processes ?? [])
                {
                    Add(byProcess, record.Id, record.Name, direction == ChangeDirection.Applied
                        ? "moved off your game's cores"
                        : $"put back on its original cores (0x{record.Mask:X})");
                }

                break;
            }

            case "ecoqos:background":
            {
                var capture = JsonSerializer.Deserialize(state, AmbientJson.Default.EfficiencyCapture);

                foreach (var record in capture?.Processes ?? [])
                {
                    // A process that was already in efficiency mode before GamerGod ran is
                    // reported as untouched, because it was. Claiming credit for it would
                    // inflate every list on a machine that had been running for a while.
                    if (record.WasAlreadyEfficient)
                    {
                        Add(byProcess, record.Id, record.Name,
                            "already in efficiency mode — left alone");
                        continue;
                    }

                    Add(byProcess, record.Id, record.Name, direction == ChangeDirection.Applied
                        ? "set to efficiency mode"
                        : "taken out of efficiency mode");
                }

                break;
            }

            case "confine:ambient-domain":
            {
                var capture = JsonSerializer.Deserialize(state, AmbientJson.Default.ConfinementCapture);

                if (capture is not null)
                {
                    machine.Add(direction == ChangeDirection.Applied
                        ? $"{capture.ProcessCount} processes confined by job object to 0x{capture.Mask:X}"
                        : $"job object released — {capture.ProcessCount} processes unconfined");
                }

                break;
            }

            case "power:scheme":
            {
                var capture = JsonSerializer.Deserialize(state, AmbientJson.Default.PowerCapture);

                if (capture is not null)
                {
                    machine.Add(direction == ChangeDirection.Applied
                        ? "power plan switched to a GamerGod copy of your own"
                        : "your original power plan restored, and the copy deleted");
                }

                break;
            }

            default:
            {
                if (key.StartsWith("service:", StringComparison.Ordinal))
                {
                    var capture = JsonSerializer.Deserialize(state, AmbientJson.Default.ServiceCapture);

                    if (capture is not null)
                    {
                        machine.Add(direction == ChangeDirection.Applied
                            ? $"{capture.Name} stopped for this session"
                            : capture.WasRunning
                                ? $"{capture.Name} started again"
                                : $"{capture.Name} left stopped, because it was not running before");
                    }
                }

                break;
            }
        }
    }

    private static void Add(
        Dictionary<int, (string Name, List<string> Changes)> byProcess,
        int id,
        string name,
        string change)
    {
        if (!byProcess.TryGetValue(id, out var existing))
        {
            byProcess[id] = existing = (name, []);
        }

        existing.Changes.Add(change);
    }
}
