using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GodMode.Core.Mutations;

namespace GodMode.Core.Ledger;

/// <summary>What a journal line records.</summary>
public enum JournalOp
{
    SessionBegin,

    /// <summary>
    /// State was read and is about to be changed. Written and flushed <em>before</em> the
    /// change is applied, so a crash between the two leaves a recoverable record rather
    /// than an orphaned change.
    /// </summary>
    Capture,

    Applied,

    /// <summary>Apply threw. Recorded, and the session continues with the remaining work.</summary>
    Failed,

    Reverted,

    /// <summary>
    /// Revert threw. The entry stays in the journal so the next boot can try again rather
    /// than the failure being silently forgotten.
    /// </summary>
    RevertFailed,

    SessionEnd,
}

/// <summary>One append-only line of the write-ahead journal.</summary>
public sealed record JournalEntry
{
    public required JournalOp Op { get; init; }

    public required string SessionId { get; init; }

    public string Key { get; init; } = string.Empty;

    /// <summary>
    /// Identifies which mutation implementation wrote this, so a cold process — one that
    /// never applied anything and holds no objects — can rebuild it to revert.
    /// </summary>
    public string MutationType { get; init; } = string.Empty;

    public MutationTier Tier { get; init; }

    public MutationVisibility Visibility { get; init; }

    public bool IsBootPersistent { get; init; }

    /// <summary>Serialised capture from <see cref="IMutation.CaptureAsync"/>.</summary>
    public string? State { get; init; }

    public string? Error { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Append-only durable log. The only thing that survives a crash, so every guarantee in
/// Charter Article VI ultimately rests on this interface being honest about durability.
/// </summary>
public interface IJournal
{
    /// <summary>
    /// Appends and durably flushes before returning. Buffering here would convert a crash
    /// into unrecoverable state, which is the exact failure this design exists to prevent.
    /// </summary>
    ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Reads every entry in write order.</summary>
    ValueTask<ImmutableArray<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Rebuilds a mutation from its journalled type identity so a cold process can revert work
/// it never performed.
/// </summary>
public interface IMutationResolver
{
    /// <summary>
    /// Returns null when the type is unknown — for example after a downgrade. The ledger
    /// records that the entry could not be reverted and leaves it in the journal rather
    /// than discarding it.
    /// </summary>
    IMutation? Resolve(string mutationType, string key);
}

/// <summary>In-memory journal for tests and for <c>--dry-run</c>.</summary>
public sealed class InMemoryJournal : IJournal
{
    private readonly List<JournalEntry> _entries = [];

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ImmutableArray<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(_entries.ToImmutableArray());

    /// <summary>
    /// A copy holding only what reached durable storage, used by the chaos tests to model a
    /// cold restart. Nothing is shared with the original.
    /// </summary>
    public InMemoryJournal ReopenCold()
    {
        var copy = new InMemoryJournal();
        copy._entries.AddRange(_entries);
        return copy;
    }
}

/// <summary>
/// Newline-delimited JSON on disk, flushed to the device after every line.
/// </summary>
public sealed class FileJournal(string path) : IJournal
{
    private readonly string _path = path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var line = JsonSerializer.Serialize(entry, JournalJsonContext.Default.JournalEntry) + "\n";

            // FileOptions.WriteThrough plus an explicit flush: the line must be on the
            // device before the caller is allowed to change anything.
            await using var stream = new FileStream(
                _path, FileMode.Append, FileAccess.Write, FileShare.Read,
                bufferSize: 4096, FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream);

            await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ImmutableArray<JournalEntry>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var entries = ImmutableArray.CreateBuilder<JournalEntry>();

        foreach (var line in await File.ReadAllLinesAsync(_path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JournalEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize(line, JournalJsonContext.Default.JournalEntry);
            }
            catch (JsonException)
            {
                // A torn final line means the machine died mid-write. Everything before it
                // is intact and recoverable, which is the whole point of append-only.
                continue;
            }

            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries.ToImmutable();
    }
}

/// <summary>
/// Source-generated serialisation for the journal.
///
/// <para>
/// Reflection-based JSON is unavailable here: the service and watchdog publish as NativeAOT
/// so they start fast and carry no runtime dependency, and a journal that failed to
/// deserialise after trimming would break recovery precisely when it matters most. Enum
/// values are written as strings so a journal left behind by a crash can be read by a human
/// with a text editor.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
