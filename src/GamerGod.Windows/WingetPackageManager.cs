using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace GamerGod.Windows;

/// <summary>
/// Installs and removes software through the Windows Package Manager.
///
/// <para>
/// GamerGod never downloads an installer itself, and this is the reason it does not have to.
/// winget ships with Windows 11, resolves packages from a manifest repository Microsoft
/// operates, verifies the publisher and the SHA-256 of every file it fetches, and can uninstall
/// what it installed. Reimplementing any of that would mean taking on responsibility for the
/// integrity of somebody else's binary with no way to discharge it — Charter Article IX, and
/// the one place where the conductor-not-instrument rule is also the security argument.
/// </para>
///
/// <para>
/// Nothing here runs unprompted. Every method is reached from a button press, which is what
/// Article IV requires of an outbound connection.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class WingetPackageManager
{
    /// <summary>
    /// Resolved once. Absent on Windows 10 installs without App Installer and on some managed
    /// images, and the interface has to say so rather than offering buttons that do nothing.
    /// </summary>
    public static string? ExecutablePath { get; } = Resolve();

    public static bool IsAvailable => ExecutablePath is not null;

    /// <summary>
    /// The winget package ids currently installed on this machine.
    ///
    /// <para>
    /// <c>winget list --source winget</c> rather than <c>winget export</c>: export enumerates
    /// every installed program and takes about twenty seconds on a real machine, while this
    /// returns only what maps to the winget repository and comes back in about one. The
    /// interface waits on this before it can label anything, so the difference is the
    /// difference between a page that appears and a page that hangs.
    /// </para>
    /// </summary>
    public static async Task<ImmutableHashSet<string>> InstalledIdsAsync(
        ImmutableArray<string> ofInterest,
        CancellationToken token)
    {
        if (!IsAvailable || ofInterest.IsDefaultOrEmpty)
        {
            return [];
        }

        var result = await RunAsync(
            ["list", "--source", "winget", "--disable-interactivity", "--accept-source-agreements"],
            progress: null,
            token).ConfigureAwait(false);

        if (result.Output.Length == 0)
        {
            return [];
        }

        // Matched by substring rather than by parsing the table. The columns are padded to the
        // console width, truncated with ellipses when a name is long, and localised — three
        // separate ways a positional parse breaks on somebody else's machine. Package ids
        // contain no spaces and are distinctive enough that containment is unambiguous.
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in ofInterest)
        {
            if (result.Output.Contains(id, StringComparison.OrdinalIgnoreCase))
            {
                builder.Add(id);
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Installs a package. Progress lines are reported as winget emits them.
    ///
    /// <para>
    /// Machine-scope packages need elevation, and winget raises that prompt itself rather than
    /// GamerGod asking for rights it does not otherwise hold. Declining it fails the install
    /// and changes nothing.
    /// </para>
    /// </summary>
    public static Task<PackageResult> InstallAsync(
        string wingetId, IProgress<string>? progress, CancellationToken token) =>
        RunAsync(
            [
                "install", "--id", wingetId, "--exact", "--source", "winget",
                "--accept-package-agreements", "--accept-source-agreements",
                "--disable-interactivity",
            ],
            progress,
            token);

    public static Task<PackageResult> UninstallAsync(
        string wingetId, IProgress<string>? progress, CancellationToken token) =>
        RunAsync(
            ["uninstall", "--id", wingetId, "--exact", "--disable-interactivity"],
            progress,
            token);

    /// <summary>The exact command line, so the interface can show it before running it.</summary>
    public static string DescribeInstall(string wingetId) =>
        $"winget install --id {wingetId} --exact --source winget";

    private static async Task<PackageResult> RunAsync(
        string[] arguments, IProgress<string>? progress, CancellationToken token)
    {
        if (ExecutablePath is not { } exe)
        {
            return new PackageResult { ExitCode = -1, Output = "The Windows Package Manager is not available on this machine." };
        }

        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            // winget draws progress bars with box-drawing characters and writes package names
            // that are frequently not ASCII. Without this they arrive as mojibake in the log
            // the user is shown when something fails.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        var captured = new StringBuilder();

        try
        {
            using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

            void Capture(object _, DataReceivedEventArgs e)
            {
                if (string.IsNullOrWhiteSpace(e.Data))
                {
                    return;
                }

                lock (captured)
                {
                    captured.AppendLine(e.Data);
                }

                // Progress-bar redraws are noise in a status line; only lines with something
                // to say are reported.
                var line = e.Data.Trim();
                if (line.Length > 3 && !line.All(c => c is '-' or '\\' or '|' or '/' or '█' or '▒' or ' '))
                {
                    progress?.Report(line);
                }
            }

            process.OutputDataReceived += Capture;
            process.ErrorDataReceived += Capture;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(token).ConfigureAwait(false);

            return new PackageResult { ExitCode = process.ExitCode, Output = captured.ToString() };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PackageResult { ExitCode = -1, Output = ex.Message };
        }
    }

    /// <summary>
    /// Finds winget.exe.
    ///
    /// <para>
    /// PATH first, because that is where it normally is. The WindowsApps fallback covers the
    /// case where App Installer is present but the current process inherited a PATH without it,
    /// which happens often enough in services and elevated shells to be worth handling.
    /// </para>
    /// </summary>
    private static string? Resolve()
    {
        try
        {
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim(), "winget.exe");

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var apps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WindowsApps", "winget.exe");

            return File.Exists(apps) ? apps : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed record PackageResult
{
    public required int ExitCode { get; init; }

    public required string Output { get; init; }

    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The part of winget's output worth showing when something failed. The whole log is
    /// hundreds of lines of progress redraws; the last few carry the actual reason.
    /// </summary>
    public string Tail(int lines = 6)
    {
        var kept = Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 3)
            .TakeLast(lines);

        return string.Join(Environment.NewLine, kept);
    }
}
