using System.Collections.Immutable;
using GamerGod.Core.Diagnostics;

namespace GamerGod.Cli;

internal static class HazardRenderer
{
    private const int Width = 74;

    public static void Render(ImmutableArray<Hazard> hazards, TextWriter output)
    {
        output.WriteLine();
        Accent(output, "  Environment scan", ConsoleColor.Cyan);
        output.WriteLine();
        output.WriteLine("  Read-only. Nothing on this machine was changed.");
        output.WriteLine();

        var actionable = hazards.Where(h => h.Severity >= HazardSeverity.Low).ToArray();

        if (actionable.Length == 0)
        {
            Accent(output, "  ✓ Nothing to report.", ConsoleColor.Green);
            output.WriteLine();
            output.WriteLine("  No broken drivers, no conflicting software, no missing platform");
            output.WriteLine("  security. This machine is in good shape.");
            output.WriteLine();
        }

        foreach (var hazard in hazards)
        {
            var (label, colour) = hazard.Severity switch
            {
                HazardSeverity.High => ("HIGH", ConsoleColor.Red),
                HazardSeverity.Medium => ("MED ", ConsoleColor.Yellow),
                HazardSeverity.Low => ("LOW ", ConsoleColor.DarkYellow),
                _ => ("INFO", ConsoleColor.DarkGray),
            };

            output.Write("  ");
            Accent(output, $"[{label}]", colour);
            output.WriteLine($"  {hazard.Title}");

            foreach (var line in Wrap(hazard.Detail, Width - 12))
            {
                output.WriteLine($"          {line}");
            }

            if (hazard.Remedy is { } remedy)
            {
                output.WriteLine();
                foreach (var line in Wrap($"→ {remedy}", Width - 12))
                {
                    output.WriteLine($"          {line}");
                }
            }

            output.WriteLine();
        }

        var high = hazards.Count(h => h.Severity == HazardSeverity.High);
        var medium = hazards.Count(h => h.Severity == HazardSeverity.Medium);

        output.WriteLine($"  {hazards.Length} finding(s) — {high} high, {medium} medium.");

        if (high > 0)
        {
            output.WriteLine();
            output.WriteLine("  Fix the high findings before tuning anything. A broken driver or a");
            output.WriteLine("  missing launch requirement costs far more than any setting GamerGod");
            output.WriteLine("  could change.");
        }

        output.WriteLine();
    }

    private static void Accent(TextWriter output, string text, ConsoleColor colour)
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

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

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
}
