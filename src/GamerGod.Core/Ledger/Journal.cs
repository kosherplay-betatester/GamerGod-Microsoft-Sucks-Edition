using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerGod.Core.Mutations;

namespace GamerGod.Core.Ledger;

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

    /// <summary>
    /// When the machine this session started on last booted, in UTC ticks. Recorded on
    /// <see cref="JournalOp.SessionBegin"/> and zero elsewhere.
    ///
    /// <para>
    /// This is what makes "just reboot" true in the interface as well as on the machine. Every
    /// change GamerGod makes to a process — efficiency mode, processor affinity — dies with
    /// that process, so a restart undoes all of it without any code running. But the journal
    /// is a file and files survive restarts, so without this the ledger reads captures with no
    /// matching reverts and reports Game Mode as still on. The machine is fine; the report is
    /// wrong, which is arguably worse, because it makes the guarantee look broken.
    /// </para>
    /// </summary>
    public long MachineBootedAtUtcTicks { get; init; }

    /// <summary>
    /// The process that armed this session. Recorded on <see cref="JournalOp.SessionBegin"/>
    /// and zero elsewhere — an owner belongs to a session, not to an individual change.
    ///
    /// <para>
    /// Boot recovery reverts anything the journal still shows as applied, which is right for a
    /// session whose program died and wrong for one that is still running. Without this, a
    /// service restart — an upgrade, a manual restart, a failed start followed by a retry —
    /// turned Game Mode off underneath somebody mid-match, and the service had no way to tell
    /// that apart from cleaning up after a bugcheck.
    /// </para>
    ///
    /// <para>
    /// Zero means no owner: an older journal, or a caller that applies and exits and therefore
    /// has nobody left to keep the session alive. Such a session is recovered, because an
    /// unclaimed session is one nobody can end and leaving it would strand the machine.
    /// </para>
    /// </summary>
    public int OwnerProcessId { get; init; }

    /// <summary>
    /// When the arming process started, in UTC ticks.
    ///
    /// <para>
    /// Windows reuses process ids aggressively, so the number alone is not an identity. An
    /// unrelated program that inherited the id would keep a stranded session looking alive at
    /// every boot from then on, which turns "leave live sessions alone" into "never recover
    /// anything". Together with <see cref="OwnerProcessId"/> this is a
    /// <see cref="Recovery.ProcessIdentity"/> — the same identity the watchdog matches on, for
    /// the same reason. Both halves or neither: a bare id is not trusted.
    /// </para>
    /// </summary>
    public long OwnerStartedAtUtcTicks { get; init; }
}

/// <summary>
/// When this machine last booted.
///
/// <para>
/// Derived from the system's uptime counter rather than a WMI query, so it needs no P/Invoke
/// and stays in Core where the ledger can be tested against a fake clock.
/// </para>
/// </summary>
public static class MachineBoot
{
    /// <summary>
    /// Sleep and hibernate perturb the uptime counter slightly, so two readings taken within
    /// the same boot can differ by seconds. A generous window avoids treating that drift as a
    /// restart, which would discard a live session's journal.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    public static DateTimeOffset At() =>
        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>
    /// True when the machine has restarted since a session began — in which case every change
    /// that could not survive a reboot is already gone.
    /// </summary>
    public static bool RestartedSince(long sessionBootTicks, DateTimeOffset now) =>
        sessionBootTicks > 0
        && now - new DateTimeOffset(sessionBootTicks, TimeSpan.Zero) > Tolerance;
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

    /// <summary>
    /// Takes exclusive ownership of this journal until the returned scope is disposed.
    ///
    /// <para>
    /// Applying and reverting must never overlap. If a revert reads the journal while an
    /// apply is between its capture line and its actual change, the revert sees a capture,
    /// undoes nothing (the change has not happened yet), writes a <c>Reverted</c> line that
    /// permanently cancels that capture, and reports success. The apply then completes and
    /// changes the machine, with no surviving record that it did. The journal now says clean
    /// while the machine is modified, so boot recovery finds nothing to do — a silent
    /// violation of the reversibility guarantee, which is the one failure this whole design
    /// exists to prevent.
    /// </para>
    ///
    /// <para>
    /// Exclusion belongs to the journal rather than to a ledger instance, because the
    /// contenders are two ledgers sharing one journal: the session agent reverting while the
    /// service is still applying, or a boot-recovery pass racing a live session.
    /// </para>
    /// </summary>
    ValueTask<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken);
}

/// <summary>Releases a journal's exclusive scope.</summary>
internal sealed class SemaphoreScope(SemaphoreSlim semaphore) : IAsyncDisposable
{
    private int _released;

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            semaphore.Release();
        }

        return ValueTask.CompletedTask;
    }
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
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    public IReadOnlyList<JournalEntry> Entries => _entries;

    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        await _exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SemaphoreScope(_exclusive);
    }

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
    /// cold restart. Nothing is shared with the original, including its exclusive scope —
    /// a cold process would not inherit a lock either.
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
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    /// <summary>
    /// Exclusion spans processes as well as instances. The service, the session agent and a
    /// boot-recovery pass are separate processes that can all reach the same journal, so an
    /// in-process semaphore alone would not prevent a revert overlapping an apply.
    /// </summary>
    public async ValueTask<IAsyncDisposable> AcquireExclusiveAsync(CancellationToken cancellationToken)
    {
        await _exclusive.WaitAsync(cancellationToken).ConfigureAwait(false);

        Mutex? system = null;
        try
        {
            // Global\ so the scope holds across sessions - the service runs in session 0
            // while the agent runs in the user's session.
            system = new Mutex(initiallyOwned: false, $"Global\\GamerGod.Journal.{Fingerprint(_path)}");

            // Wait on a pool thread: WaitOne blocks, and thread affinity matters for a mutex.
            var acquired = await Task.Run(
                () =>
                {
                    try
                    {
                        return system.WaitOne(TimeSpan.FromSeconds(30));
                    }
                    catch (AbandonedMutexException)
                    {
                        // The previous owner died holding it. That is a crash, which is
                        // exactly the case recovery exists for: take ownership and continue.
                        return true;
                    }
                },
                cancellationToken).ConfigureAwait(false);

            if (!acquired)
            {
                throw new TimeoutException(
                    "Another GamerGod process has held the journal for over 30 seconds. "
                    + "Refusing to proceed, because applying or reverting concurrently could "
                    + "lose the record of a change.");
            }

            return new MutexScope(system, _exclusive);
        }
        catch
        {
            system?.Dispose();
            _exclusive.Release();
            throw;
        }
    }

    /// <summary>
    /// A mutex name may not contain a path separator, so the path is folded to a stable
    /// hex fingerprint. FNV-1a rather than <see cref="string.GetHashCode()"/>, which .NET
    /// randomises per process — two processes must agree on the name or the lock is useless.
    /// </summary>
    private static string Fingerprint(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value.ToUpperInvariant())
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash.ToString("x8");
    }

    private sealed class MutexScope(Mutex mutex, SemaphoreSlim local) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                try
                {
                    mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Not the owner - already released, or taken over after an abandon.
                }

                mutex.Dispose();
                local.Release();
            }

            return ValueTask.CompletedTask;
        }
    }

    public async ValueTask AppendAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // A bare filename has no directory component, and CreateDirectory("") throws.
            // The journal is the one component that must never fail for an incidental
            // reason, because everything recoverable depends on this write landing.
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

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
/// Reflection-based JSON is unavailable here. Every shipped binary is analysed for AOT
/// compatibility, and a journal that failed to deserialise after trimming would break
/// recovery precisely when it matters most — which is to say only in a shipped build, and
/// only after a crash. Enum values are written as strings so a journal left behind by one
/// can be read by a human with a text editor.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalEntry))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
