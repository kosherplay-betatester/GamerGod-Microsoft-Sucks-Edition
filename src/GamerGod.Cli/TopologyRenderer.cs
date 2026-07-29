using System.Text;
using GamerGod.Core.Hardware;

namespace GamerGod.Cli;

/// <summary>
/// Renders a classified topology as a terminal diagram.
///
/// <para>
/// This exists because "your game is on the right cores" is a claim users have no practical
/// way to verify, and unverifiable claims are how this whole product category lost its
/// credibility. Showing the machine's actual shape, and which part of it is about to be
/// handed to the game, is the smallest honest version of the Prove pillar.
/// </para>
/// </summary>
internal static class TopologyRenderer
{
    private const int Width = 72;

    public static void Render(CpuTopology topology, TextWriter output)
    {
        output.WriteLine();
        RenderHeading(output, topology);

        foreach (var domain in topology.Domains)
        {
            var isGame = domain.Id == topology.GameDomain.Id;
            RenderDomain(output, domain, isGame);
        }

        RenderPlan(output, topology);
        output.WriteLine();
    }

    private static void RenderHeading(TextWriter output, CpuTopology topology)
    {
        WriteAccent(output, "  GamerGod", ConsoleColor.Cyan);
        output.WriteLine("  ·  Microsoft Sucks Edition");
        output.WriteLine();

        WriteAccent(output, $"  {topology.ProcessorName}", ConsoleColor.White);
        output.WriteLine();

        var frequency = topology.MaxFrequencyMhz > 0 ? $"{topology.MaxFrequencyMhz} MHz  ·  " : "";
        output.WriteLine(
            $"  {topology.PhysicalCoreCount} cores / {topology.LogicalProcessorCount} threads  ·  " +
            $"{frequency}{Describe(topology.Kind)}");
        output.WriteLine();
    }

    private static void RenderDomain(TextWriter output, PerformanceDomain domain, bool isGame)
    {
        var role = isGame ? "GAME" : "AMBIENT";
        var title = $" D{domain.Id} ── {domain.Class.ToString().ToUpperInvariant()} ";
        var tail = $" {role} ";
        var fill = Math.Max(1, Width - title.Length - tail.Length);

        output.WriteLine($"  ╭─{title}{new string('─', fill)}{tail}╮");

        var cache = domain.LastLevelCacheBytes > 0 ? $"{Bytes(domain.LastLevelCacheBytes)} L3" : "no L3";
        var perCore = domain.LastLevelCacheBytes > 0
            ? $"   {Bytes(domain.CacheBytesPerPhysicalCore)} per core"
            : string.Empty;
        var smt = domain.IsSimultaneousMultiThreaded ? "SMT" : "no SMT";

        Row(output, $"{cache}   {domain.PhysicalCoreCount} cores / " +
                    $"{domain.LogicalProcessorCount} threads   {smt}{perCore}");
        Row(output, string.Empty);

        // One cell per logical processor, wrapped so wide domains stay readable.
        const int PerLine = 16;
        var glyph = isGame ? "██" : "▒▒";
        var colour = isGame ? ConsoleColor.Green : ConsoleColor.DarkGray;

        for (var start = 0; start < domain.LogicalProcessors.Length; start += PerLine)
        {
            var slice = domain.LogicalProcessors.Skip(start).Take(PerLine).ToArray();
            Row(output, string.Join(" ", slice.Select(lp => lp.ToString("00"))));
            Row(output, string.Join(" ", slice.Select(_ => glyph)), colour);
        }

        output.WriteLine($"  ╰{new string('─', Width + 1)}╯");
        output.WriteLine();
    }

    private static void RenderPlan(TextWriter output, CpuTopology topology)
    {
        WriteAccent(output, "  Routing plan", ConsoleColor.White);
        output.WriteLine();

        output.WriteLine(
            $"    Game     → {topology.GameDomain.LogicalProcessorCount,2} threads on D{topology.GameDomain.Id}" +
            $"   mask 0x{topology.GameDomain.Processors.Mask:X16}");

        if (topology.CanPartition)
        {
            var ids = string.Join("+", topology.AmbientDomains.Select(d => $"D{d.Id}"));
            var count = topology.AmbientDomains.Sum(d => d.LogicalProcessorCount);
            output.WriteLine(
                $"    Ambient  → {count,2} threads on {ids}   mask 0x{topology.AmbientMask.Mask:X16}");
        }
        else
        {
            output.WriteLine("    Ambient  →  n/a — single domain, nothing to evict to");
        }

        output.WriteLine();

        if (topology.CanPartition)
        {
            var detail = topology.Kind switch
            {
                TopologyKind.AsymmetricCache =>
                    $"give your game the {Bytes(topology.GameDomain.LastLevelCacheBytes)} cache domain" +
                    " and evict every other process to the smaller one",
                TopologyKind.Hybrid =>
                    "give your game the performance cores and evict every other process to the" +
                    " efficiency cores",
                _ =>
                    "keep your game on one domain and every other process on the other, avoiding" +
                    " cross-domain latency",
            };

            foreach (var line in Wrap($"This machine can be partitioned. GamerGod will {detail}.", Width))
            {
                output.WriteLine($"  {line}");
            }
        }
        else
        {
            output.WriteLine("  This machine has a single performance domain, so there is nowhere to");
            output.WriteLine("  evict background work to. GamerGod will apply every other ambient lever");
            output.WriteLine("  but will not pretend to partition a CPU that cannot be partitioned.");
        }
    }

    private static void Row(TextWriter output, string content, ConsoleColor? colour = null)
    {
        var padding = Math.Max(0, Width - 1 - content.Length);
        output.Write("  │  ");

        if (colour is { } c && !Console.IsOutputRedirected)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = c;
            output.Write(content);
            Console.ForegroundColor = previous;
        }
        else
        {
            output.Write(content);
        }

        output.WriteLine($"{new string(' ', padding)}│");
    }

    private static void WriteAccent(TextWriter output, string text, ConsoleColor colour)
    {
        if (Console.IsOutputRedirected)
        {
            output.Write(text);
            return;
        }

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = colour;
        output.Write(text);
        Console.ForegroundColor = previous;
    }

    /// <summary>Greedy word wrap, so explanatory prose never overruns the diagram.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string Describe(TopologyKind kind) => kind switch
    {
        TopologyKind.AsymmetricCache => "asymmetric cache (X3D-class)",
        TopologyKind.Hybrid => "hybrid performance/efficiency cores",
        TopologyKind.SymmetricMultiDomain => "symmetric multi-domain",
        _ => "uniform",
    };

    private static string Bytes(long value)
    {
        const long Mb = 1024L * 1024L;
        if (value >= Mb)
        {
            var mb = value / (double)Mb;
            return mb >= 10 ? $"{mb:0} MB" : $"{mb:0.0} MB";
        }

        return $"{value / 1024} KB";
    }
}

/// <summary>Machine-readable projection, for scripting and community profile submissions.</summary>
internal static class TopologyJson
{
    public static string Write(CpuTopology topology)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"  \"processor\": {Quote(topology.ProcessorName)},");
        sb.AppendLine($"  \"kind\": {Quote(topology.Kind.ToString())},");
        sb.AppendLine($"  \"physicalCores\": {topology.PhysicalCoreCount},");
        sb.AppendLine($"  \"logicalProcessors\": {topology.LogicalProcessorCount},");
        sb.AppendLine($"  \"maxFrequencyMhz\": {topology.MaxFrequencyMhz},");
        sb.AppendLine($"  \"canPartition\": {(topology.CanPartition ? "true" : "false")},");
        sb.AppendLine($"  \"gameDomainId\": {topology.GameDomain.Id},");
        sb.AppendLine("  \"domains\": [");

        for (var i = 0; i < topology.Domains.Length; i++)
        {
            var d = topology.Domains[i];
            var comma = i == topology.Domains.Length - 1 ? string.Empty : ",";
            sb.AppendLine("    {");
            sb.AppendLine($"      \"id\": {d.Id},");
            sb.AppendLine($"      \"class\": {Quote(d.Class.ToString())},");
            sb.AppendLine($"      \"efficiencyClass\": {d.EfficiencyClass},");
            sb.AppendLine($"      \"lastLevelCacheBytes\": {d.LastLevelCacheBytes},");
            sb.AppendLine($"      \"physicalCores\": {d.PhysicalCoreCount},");
            sb.AppendLine($"      \"mask\": \"0x{d.Processors.Mask:X16}\",");
            sb.AppendLine($"      \"logicalProcessors\": [{string.Join(", ", d.LogicalProcessors)}]");
            sb.AppendLine($"    }}{comma}");
        }

        sb.AppendLine("  ]");
        sb.Append('}');
        return sb.ToString();
    }

    private static string Quote(string s) =>
        $"\"{s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
