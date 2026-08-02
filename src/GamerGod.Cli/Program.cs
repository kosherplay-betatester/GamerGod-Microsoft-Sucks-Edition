using System.Runtime.Versioning;
using GamerGod.Cli;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Windows;

[assembly: SupportedOSPlatform("windows")]

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "topology";
var json = args.Contains("--json", StringComparer.OrdinalIgnoreCase);

try
{
    switch (command)
    {
        case "topology":
        case "--json":
        {
            var topology = new WindowsTopologyProvider().Classify();

            if (json)
            {
                Console.WriteLine(TopologyJson.Write(topology));
            }
            else
            {
                TopologyRenderer.Render(topology, Console.Out);
            }

            return 0;
        }

        case "summary":
        {
            Console.WriteLine(new WindowsTopologyProvider().Classify().Summary());
            return 0;
        }

        case "scan":
        {
            var snapshot = new WindowsMachineProbe().Capture();
            var hazards = GamerGod.Core.Diagnostics.HazardScanner.Scan(snapshot);
            HazardRenderer.Render(hazards, Console.Out);

            // Non-zero only for findings that block a game from launching or point at
            // broken hardware, so this is usable as a health check in a script.
            return hazards.Any(x => x.Severity == GamerGod.Core.Diagnostics.HazardSeverity.High) ? 3 : 0;
        }

        case "on":
        {
            // --owner <pid>: the process whose death ends this session. Resolved to a full
            // identity here — pid plus start time — because Windows recycles pids and a bare
            // number would let an unrelated program inherit ownership of somebody's session.
            //
            // Resolved through the same WindowsProcessLiveness the watchdog probes with, so the
            // identity written to the journal and the identity checked against it are produced
            // by one piece of code and cannot disagree about what a process is.
            GamerGod.Core.Recovery.ProcessIdentity? owner = null;

            if (OwnerArguments.Find(args) is { } requested)
            {
                owner = await new WindowsProcessLiveness().IdentifyAsync(requested, default);

                if (owner is null)
                {
                    Console.Error.WriteLine(
                        $"  There is no process {requested} to hand this session to. "
                        + "Nothing was changed.");
                    return 2;
                }
            }

            return await SessionCommands.OnAsync(
                dryRun: args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase),
                options: LeverArguments.Parse(args),
                owner: owner,
                ownerExecutable: OwnerArguments.FindExecutable(args));
        }

        case "claim":
        {
            // Spawned by 'gamergod on --owner-exe', not typed. It waits for a game to start and
            // then records it as the session's owner, so the watchdog can end the session when
            // the game does.
            var session = ValueAfter(args, "--session");
            var executable = OwnerArguments.FindExecutable(args);

            if (session is null || executable is null)
            {
                Console.Error.WriteLine(
                    "  Usage: gamergod claim --session <id> --owner-exe <program>");
                return 2;
            }

            return await SessionCommands.ClaimAsync(session, executable);
        }

        case "off":
            return await SessionCommands.OffAsync();

        case "status":
            return await SessionCommands.StatusAsync();

        case "autotune":
            return await AutotuneCommand.RunAsync(args);

        case "bench":
            return await BenchCommand.RunAsync(args);

        case "restore":
        {
            // The uninstaller runs this before removing anything, so it must be honest and
            // it must never fail merely because there was nothing to do. Exit code 0 means
            // "this machine has no outstanding GamerGod changes" - which is exactly what the
            // uninstaller needs to know before it is safe to delete the journal.
            var journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GamerGod", "state");

            if (!Directory.Exists(journalPath)
                || Directory.GetFiles(journalPath, "*.journal").Length == 0)
            {
                Console.WriteLine("Nothing to restore. No GamerGod changes are recorded as applied.");
                return 0;
            }

            // This used to report that "the restore engine is not in this build" and exit 1.
            // It was true when written and stopped being true the moment OffAsync landed —
            // the engine is the same one, a few lines below in this file. The uninstaller runs
            // this first and refuses to continue when it fails, so the effect of the stale
            // message was that uninstalling with Game Mode on told the user their machine
            // could not be restored, by a build that could restore it.
            return await SessionCommands.OffAsync();
        }

        case "help":
        case "--help":
        case "-h":
            PrintUsage();
            return 0;

        default:
            Console.Error.WriteLine($"gamergod: unknown command '{command}'.");
            PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    // A topology read is pure observation - it changes nothing - so a failure here can
    // only ever be a reporting failure. Say so plainly rather than dumping a stack trace
    // that implies something was left in a bad state.
    Console.Error.WriteLine($"gamergod: could not read this machine's topology.");
    Console.Error.WriteLine($"         {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("         Nothing was changed. This command only reads.");
    return 1;
}

/// <summary>The value following a flag, or null when the flag is absent or ends the list.</summary>
static string? ValueAfter(string[] arguments, string flag)
{
    for (var i = 0; i < arguments.Length - 1; i++)
    {
        if (string.Equals(arguments[i], flag, StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(arguments[i + 1]) ? null : arguments[i + 1];
        }
    }

    return null;
}

static void PrintUsage()
{
    Console.WriteLine("""

        gamergod - GamerGod: Microsoft Sucks Edition

        USAGE
          gamergod [command] [options]

        COMMANDS
          on           Move everything else out of your games' way
          off          Put it all back, and print a receipt
          status       Show whether Game Mode is on, and what it changed

          bench        Measure whether any of it actually helps, on your hardware
          autotune     Measure every setting and keep only what measurably helped

          scan         Check this machine for things that cost you frames or block launches
          topology     Show this machine's performance domains and the routing plan
          summary      One-line topology summary
          restore      Undo every change GamerGod has recorded, and report what remains
          help         Show this help

        OPTIONS
          --dry-run    With 'on': show exactly what would change, and change nothing
          --owner N    With 'on': end this session when process N exits. Without it the
                       session outlives whoever armed it, which is the usual behaviour.
          --owner-exe P  With 'on': wait for program P to start, then end the session when
                       it exits. For a game a store launcher starts, whose id nobody
                       can know in advance.

          With 'on', each lever can be stated either way. Anything not given keeps
          its default, so 'gamergod on' alone behaves exactly as it always has:
            --confine / --no-confine          move background apps off your game's cores
            --efficiency / --no-efficiency    set background apps to efficiency mode
            --power / --no-power              use a high-performance power plan
            --services / --no-services        pause the search indexer and update checks
          --json       With 'topology': emit machine-readable JSON
          --seconds N  With 'bench' and 'autotune': seconds per run (default 30)
          --runs N     With 'bench': runs per arm (default 5)
                       With 'autotune': runs per arm (minimum 7, and it refuses fewer)
          --pid N      With 'bench' and 'autotune': measure a specific process

        'scan', 'topology' and 'status' only read - they change nothing.

        Every change 'on' makes is written down before it is applied and undone by 'off'.
        If anything goes wrong, rebooting undoes all of it: that is guaranteed by design
        and does not depend on GamerGod still working.

        """);
}
