using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using GamerGod.Core.Engine;

namespace GamerGod.Windows;

/// <summary>
/// Asks for administrator rights once, and keeps them for as long as the window is open.
///
/// <para>
/// Every arm and every disarm used to be its own <c>runas</c>, and therefore its own Windows
/// consent prompt. Switch on: a prompt. Switch off: another. Arm on launch: one before the window
/// was even usable, which is the worst of them — it arrives before there is any context for what
/// it is for. A prompt that frequent stops being read, which costs exactly the protection it
/// exists to give.
/// </para>
///
/// <para>
/// So the first privileged action starts <c>gamergod broker</c> elevated — one prompt — and every
/// action after it travels down a pipe to that already-elevated process. The helper accepts two
/// verbs and a status query, is reachable only by the account that started it, and exits when
/// this object is disposed.
/// </para>
///
/// <para>
/// <b>Honest about the trade.</b> Between the prompt and the window closing, code already running
/// as this user could reach that pipe and ask for on or off. What it could ask for is what the
/// switch does — an Ambient, journalled, reversible change — and never an arbitrary command; the
/// helper has no way to run one. That is the cost of not asking a person to approve the same
/// thing six times an evening, and it is a smaller cost than a prompt nobody reads any more.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedBroker : IAsyncDisposable
{
    /// <summary>How long to wait for the helper to come up and accept a connection.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(90);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _pipeName = "gamergod-" + Guid.NewGuid().ToString("N");

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _helper;
    private bool _disposed;

    /// <summary>True once a helper is running and answering.</summary>
    public bool IsConnected => _pipe is { IsConnected: true };

    /// <summary>
    /// Runs a verb, starting the elevated helper first if this is the first one.
    ///
    /// <para>
    /// The consent prompt happens inside here, and only on the call that needs it.
    /// </para>
    /// </summary>
    public async Task<ElevationResult> RunAsync(string verb, params string[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verb);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected)
            {
                if (await StartAsync().ConfigureAwait(false) is { } problem)
                {
                    return problem;
                }
            }

            return await SendAsync(verb, arguments).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts the helper and connects. Returns a result on failure, null on success.</summary>
    private async Task<ElevationResult?> StartAsync()
    {
        if (Elevation.FindCommandLineTool() is not { } tool)
        {
            return new ElevationResult
            {
                Outcome = ElevationOutcome.ToolMissing,
                Problem = "gamergod.exe was not found next to the application.",
            };
        }

        var info = new ProcessStartInfo(tool)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(tool) ?? string.Empty,
        };

        info.ArgumentList.Add("broker");
        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(_pipeName);

        try
        {
            _helper = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Declined. A legitimate answer, and the caller says so rather than showing an error.
            return new ElevationResult { Outcome = ElevationOutcome.Declined };
        }
        catch (Exception ex)
        {
            return new ElevationResult { Outcome = ElevationOutcome.Failed, Problem = ex.Message };
        }

        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            // Generous: the helper starts a whole process, and on a cold machine that is not
            // instant. Short of the consent prompt's own patience either way.
            using var timeout = new CancellationTokenSource(StartTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);

            return new ElevationResult
            {
                Outcome = ElevationOutcome.Failed,
                Problem = _helper is { HasExited: true }
                    ? "the elevated helper exited before it could be reached"
                    : "the elevated helper did not answer",
            };
        }

        _pipe = pipe;
        _reader = new StreamReader(pipe, leaveOpen: true);
        _writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };

        return null;
    }

    private async Task<ElevationResult> SendAsync(string verb, string[] arguments)
    {
        if (_writer is null || _reader is null)
        {
            return new ElevationResult { Outcome = ElevationOutcome.Failed, Problem = "not connected" };
        }

        var line = arguments is { Length: > 0 }
            ? verb + " " + string.Join(' ', arguments)
            : verb;

        try
        {
            await _writer.WriteLineAsync(line).ConfigureAwait(false);
            var reply = await _reader.ReadLineAsync().ConfigureAwait(false);

            if (BrokerProtocol.ReadExitCode(reply) is not { } exitCode)
            {
                return new ElevationResult
                {
                    Outcome = ElevationOutcome.Failed,
                    Problem = reply?.StartsWith(BrokerProtocol.Error, StringComparison.Ordinal) == true
                        ? reply[BrokerProtocol.Error.Length..]
                        : "the elevated helper gave no answer",
                };
            }

            return new ElevationResult
            {
                Outcome = exitCode == 0 ? ElevationOutcome.Succeeded : ElevationOutcome.Failed,
                ExitCode = exitCode,
                Problem = exitCode == 0 ? null : $"gamergod {verb} exited with {exitCode}",
            };
        }
        catch (Exception ex)
        {
            // The helper died — killed in Task Manager, or the machine is shutting down. Dropped
            // so the next call starts a fresh one rather than talking to a closed pipe forever.
            await DropAsync().ConfigureAwait(false);

            return new ElevationResult { Outcome = ElevationOutcome.Failed, Problem = ex.Message };
        }
    }

    private async Task DropAsync()
    {
        try { _writer?.Dispose(); } catch (Exception) { /* already gone */ }
        try { _reader?.Dispose(); } catch (Exception) { /* already gone */ }

        if (_pipe is { } pipe)
        {
            try { await pipe.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* already gone */ }
        }

        _writer = null;
        _reader = null;
        _pipe = null;
    }

    /// <summary>
    /// Stops the helper. An elevated process must not outlive the window that asked for it.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (IsConnected && _writer is { } writer)
        {
            // Asked politely first: the helper exits on its own, and on the pipe closing anyway.
            try { await writer.WriteLineAsync(BrokerProtocol.Quit).ConfigureAwait(false); }
            catch (Exception) { /* it is going away regardless */ }
        }

        await DropAsync().ConfigureAwait(false);

        try
        {
            if (_helper is { HasExited: false })
            {
                // The pipe closing is the helper's own exit signal; this is the backstop for one
                // that is wedged. Leaving an elevated process running would be the worse bug.
                if (!_helper.WaitForExit(3000))
                {
                    _helper.Kill();
                }
            }
        }
        catch (Exception)
        {
            // Already gone, or never started.
        }

        _helper?.Dispose();
        _gate.Dispose();
    }
}
