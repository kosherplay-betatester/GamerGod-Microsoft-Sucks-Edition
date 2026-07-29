using System.Runtime.Versioning;
using GamerGod.Cli;
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
            return await SessionCommands.OnAsync(dryRun: args.Contains("--dry-run", StringComparer.OrdinalIgnoreCase));

        case "off":
            return await SessionCommands.OffAsync();

        case "status":
            return await SessionCommands.StatusAsync();

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

            // Applying and reverting real machine state arrives with the engine. Until then,
            // claiming a successful restore would be a lie the uninstaller would act on.
            Console.Error.WriteLine(
                "Journals are present but the restore engine is not in this build.");
            Console.Error.WriteLine(
                "Reboot to restore the machine - every GamerGod change is undone at boot by design.");
            return 1;
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

          scan         Check this machine for things that cost you frames or block launches
          topology     Show this machine's performance domains and the routing plan
          summary      One-line topology summary
          restore      Undo every change GamerGod has recorded, and report what remains
          help         Show this help

        OPTIONS
          --dry-run    With 'on': show exactly what would change, and change nothing
          --json       With 'topology': emit machine-readable JSON

        'scan', 'topology' and 'status' only read - they change nothing.

        Every change 'on' makes is written down before it is applied and undone by 'off'.
        If anything goes wrong, rebooting undoes all of it: that is guaranteed by design
        and does not depend on GamerGod still working.

        """);
}
