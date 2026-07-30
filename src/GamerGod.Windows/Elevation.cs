using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace GamerGod.Windows;

/// <summary>What happened when a privileged command was asked for.</summary>
public enum ElevationOutcome
{
    Succeeded,

    /// <summary>The user closed the consent prompt. Nothing ran, nothing changed.</summary>
    Declined,

    /// <summary>It ran and reported a failure.</summary>
    Failed,

    /// <summary>The command-line tool could not be found next to the application.</summary>
    ToolMissing,
}

public sealed record ElevationResult
{
    public required ElevationOutcome Outcome { get; init; }

    public int ExitCode { get; init; }

    public string? Problem { get; init; }

    public bool Succeeded => Outcome == ElevationOutcome.Succeeded;
}

/// <summary>
/// Runs the privileged half of GamerGod, elevated, only at the moment it is needed.
///
/// <para>
/// The desktop application is a per-user program with no special rights, and everything it
/// mostly does — reading the processor layout, listing games, browsing the catalogue — needs
/// none. Arming Game Mode is different: it writes the revert journal, which lives in a directory
/// only administrators may write to, and it sets scheduling state on processes belonging to
/// other accounts. Both require rights the window does not have and should not hold all the
/// time.
/// </para>
///
/// <para>
/// The alternatives are worse. Marking the whole application as requiring administrator puts a
/// consent prompt in front of browsing a game library. Loosening the journal directory so any
/// user could write it would make the file that restores the machine editable by anyone — and
/// that file is the entire safety guarantee. So the prompt appears exactly once, at the moment
/// the machine is about to change, which is also the moment it is worth reading.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Elevation
{
    /// <summary>Windows' own code for "the user dismissed the consent prompt".</summary>
    private const int ErrorCancelled = 1223;

    /// <summary>True when this process already holds administrator rights.</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                // Unknown means "assume not", which routes through the consent prompt rather
                // than attempting something that would fail with a confusing error.
                return false;
            }
        }
    }

    /// <summary>
    /// The command-line tool, or null.
    ///
    /// <para>
    /// <b>Never the executable doing the asking.</b> The command is <c>gamergod.exe</c> and the
    /// desktop application is <c>GamerGod.exe</c> — the same filename on a case-insensitive
    /// filesystem, which is why the installer puts them in different folders. Searching the
    /// current directory first therefore finds the window's own binary, and elevating that
    /// opens a second copy of the interface instead of running the verb. Anything resolving to
    /// this process is skipped, so the collision cannot be re-introduced by a stray copy.
    /// </para>
    /// </summary>
    public static string? FindCommandLineTool()
    {
        try
        {
            var self = Environment.ProcessPath is { } path ? Path.GetFullPath(path) : null;
            var here = AppContext.BaseDirectory;

            foreach (var candidate in new[]
                     {
                         // The installed layout: the tool sits one level above the app folder.
                         Path.Combine(Directory.GetParent(here)?.FullName ?? here, "gamergod.exe"),
                         Path.Combine(here, "gamergod.exe"),
                     })
            {
                var full = Path.GetFullPath(candidate);

                if (!File.Exists(full))
                {
                    continue;
                }

                if (self is not null && full.Equals(self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return full;
            }
        }
        catch (Exception)
        {
            // Falls through to null, which the interface reports rather than hiding.
        }

        return null;
    }

    /// <summary>
    /// Runs a GamerGod verb with administrator rights and waits for it.
    ///
    /// <para>
    /// Output is not captured, because capturing it requires launching without the shell and
    /// the shell is what raises the consent prompt. The caller reads the resulting machine state
    /// instead — which is better evidence than parsed console text anyway.
    /// </para>
    /// </summary>
    public static async Task<ElevationResult> RunAsync(string verb, CancellationToken token)
    {
        if (FindCommandLineTool() is not { } tool)
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

        info.ArgumentList.Add(verb);

        try
        {
            using var process = Process.Start(info);

            if (process is null)
            {
                return new ElevationResult
                {
                    Outcome = ElevationOutcome.Failed,
                    Problem = "the elevated process did not start",
                };
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);

            return new ElevationResult
            {
                Outcome = process.ExitCode == 0 ? ElevationOutcome.Succeeded : ElevationOutcome.Failed,
                ExitCode = process.ExitCode,
                Problem = process.ExitCode == 0 ? null : $"gamergod {verb} exited with {process.ExitCode}",
            };
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // Declining is a legitimate answer, not an error. Nothing ran and nothing changed,
            // and the interface says exactly that rather than showing a failure.
            return new ElevationResult { Outcome = ElevationOutcome.Declined };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ElevationResult { Outcome = ElevationOutcome.Failed, Problem = ex.Message };
        }
    }
}
