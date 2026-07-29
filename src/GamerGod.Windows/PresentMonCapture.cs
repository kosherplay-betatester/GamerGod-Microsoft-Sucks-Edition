using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using GamerGod.Core.Measurement;

namespace GamerGod.Windows;

/// <summary>
/// Frame capture via Intel PresentMon's console application.
///
/// <para>
/// PresentMon reads present events from ETW, out of process. Nothing is injected into the
/// game, nothing is hooked, and no handle to it is opened — which is why measurement works on
/// titles with kernel anti-cheat, where an injecting overlay could not go.
/// </para>
///
/// <para>
/// The console application is used rather than the SDK deliberately: it is a documented,
/// stable interface, it needs no native interop, and if it is absent the failure is a clean
/// "not installed" instead of a missing-DLL crash. It is also the form a user can verify by
/// running it themselves.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PresentMonCapture : IFrameCapture
{
    private readonly string? _executable;

    public PresentMonCapture()
    {
        _executable = Locate();

        Source = _executable is not null
            ? new CaptureSource { Name = "Intel PresentMon", IsAvailable = true }
            : new CaptureSource
            {
                Name = "none",
                IsAvailable = false,
                Unavailable = "Intel PresentMon is not installed, so frame times cannot be measured.",
                Remedy = "Install it with:  winget install --id Intel.PresentMon.Console",
            };
    }

    public CaptureSource Source { get; }

    public async ValueTask<FrameSeries> CaptureAsync(
        int? processId, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (_executable is null)
        {
            // Empty, never invented. A plausible-looking series would be indistinguishable
            // from a real measurement to everything downstream.
            return FrameSeries.FromFrameTimes([]);
        }

        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));

        // Verified against PresentMon 2.5.1 rather than assumed. An earlier draft passed
        // "--no_top", which does not exist in 2.x - the process exited with a usage error and
        // the capture silently produced nothing.
        var arguments = new List<string>
        {
            "--output_stdout",
            "--no_console_stats",
            "--terminate_after_timed",
            "--timed", seconds.ToString(CultureInfo.InvariantCulture),

            // A private session name, and stop a stale one of ours if it is still running.
            // Without this a leftover session from an interrupted capture fails every
            // subsequent attempt, and the name keeps GamerGod from disturbing a session the
            // user started themselves.
            "--session_name", "GamerGodBench",
            "--stop_existing_session",
        };

        if (processId is { } pid)
        {
            arguments.Add("--process_id");
            arguments.Add(pid.ToString(CultureInfo.InvariantCulture));
        }

        var startInfo = new ProcessStartInfo(_executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return FrameSeries.FromFrameTimes([]);
            }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken)
                .ConfigureAwait(false);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            // Discard the first two seconds: shader compilation, first-time file reads and
            // clock ramp make the opening of any capture unrepresentative.
            return FrameSeries.FromFrameTimes(PresentMonCsv.ParseFrameTimes(output), discardFirstSeconds: 2.0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A capture that failed produced no data. Saying so is correct; guessing is not.
            return FrameSeries.FromFrameTimes([]);
        }
    }

    private static string? Locate()
    {
        // PATH first, so a user who installed it their own way is found.
        foreach (var name in new[] { "PresentMon.exe", "presentmon.exe" })
        {
            var onPath = Environment.GetEnvironmentVariable("PATH")
                ?.Split(Path.PathSeparator)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory =>
                {
                    try
                    {
                        return Path.Combine(directory, name);
                    }
                    catch (ArgumentException)
                    {
                        // A malformed PATH entry is not worth failing over.
                        return null;
                    }
                })
                .FirstOrDefault(candidate => candidate is not null && File.Exists(candidate));

            if (onPath is not null)
            {
                return onPath;
            }
        }

        foreach (var directory in new[]
                 {
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Intel", "PresentMon"),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Intel", "PresentMon"),
                 })
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                var found = Directory
                    .EnumerateFiles(directory, "PresentMon*.exe", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                {
                    return found;
                }
            }
            catch (Exception)
            {
                // Unreadable directory. Keep looking rather than failing the whole probe.
            }
        }

        return null;
    }
}
