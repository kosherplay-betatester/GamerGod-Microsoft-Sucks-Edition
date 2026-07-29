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
          summary      One-line topology summary
          help         Show this help

        OPTIONS
          --json       Emit machine-readable JSON instead of a diagram

        Every command available today is read-only. GodMode changes nothing until the
        engine ships, and when it does, every change is journaled before it is applied.

        """);
}
