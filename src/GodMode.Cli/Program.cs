using System.Runtime.Versioning;
using GodMode.Cli;
using GodMode.Core.Hardware;
using GodMode.Windows;

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
            var hazards = GodMode.Core.Diagnostics.HazardScanner.Scan(snapshot);
            HazardRenderer.Render(hazards, Console.Out);

            // Non-zero only for findings that block a game from launching or point at
            // broken hardware, so this is usable as a health check in a script.
            return hazards.Any(x => x.Severity == GodMode.Core.Diagnostics.HazardSeverity.High) ? 3 : 0;
        }

        case "restore":
        {
            // The uninstaller runs this before removing anything, so it must be honest and
            // it must never fail merely because there was nothing to do. Exit code 0 means
            // "this machine has no outstanding GodMode changes" - which is exactly what the
            // uninstaller needs to know before it is safe to delete the journal.
            var journalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GodMode", "state");

            if (!Directory.Exists(journalPath)
                || Directory.GetFiles(journalPath, "*.journal").Length == 0)
            {
                Console.WriteLine("Nothing to restore. No GodMode changes are recorded as applied.");
                return 0;
            }

            // Applying and reverting real machine state arrives with the engine. Until then,
            // claiming a successful restore would be a lie the uninstaller would act on.
            Console.Error.WriteLine(
                "Journals are present but the restore engine is not in this build.");
            Console.Error.WriteLine(
                "Reboot to restore the machine - every GodMode change is undone at boot by design.");
            return 1;
        }

        case "help":
        case "--help":
        case "-h":
            PrintUsage();
            return 0;

        default:
            Console.Error.WriteLine($"godmode: unknown command '{command}'.");
            PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    // A topology read is pure observation - it changes nothing - so a failure here can
    // only ever be a reporting failure. Say so plainly rather than dumping a stack trace
    // that implies something was left in a bad state.
    Console.Error.WriteLine($"godmode: could not read this machine's topology.");
    Console.Error.WriteLine($"         {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine("         Nothing was changed. This command only reads.");
    return 1;
}

static void PrintUsage()
{
    Console.WriteLine("""

        godmode - GodMode: Microsoft Sucks Edition

        USAGE
          godmode [command] [options]

        COMMANDS
          topology     Show this machine's performance domains and the routing plan (default)
          scan         Check this machine for things that cost you frames or block launches
          summary      One-line topology summary
          restore      Undo every change GodMode has recorded, and report what remains
          help         Show this help

        OPTIONS
          --json       Emit machine-readable JSON instead of a diagram

        Every command available today is read-only. GodMode changes nothing until the
        engine ships, and when it does, every change is journaled before it is applied.

        """);
}
