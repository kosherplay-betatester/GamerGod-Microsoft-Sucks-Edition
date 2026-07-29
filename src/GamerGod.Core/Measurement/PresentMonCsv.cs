using System.Globalization;

namespace GamerGod.Core.Measurement;

/// <summary>
/// Parses Intel PresentMon's CSV output.
///
/// <para>
/// This lives in Core rather than beside the process-launching code because it is pure string
/// handling with no dependency on Windows at all — which means it can be tested exhaustively
/// against real captured output, in the same test project as everything else, with no admin
/// rights and no PresentMon installed. The part that actually needs an operating system is
/// only the process launch.
/// </para>
/// </summary>
public static class PresentMonCsv
{
    /// <summary>
    /// Frame times in milliseconds, in capture order.
    ///
    /// <para>
    /// The column is located by header name, never by index. An index-based parser appears to
    /// work and then silently reads a different metric the first time a PresentMon version
    /// inserts a column — reporting, say, GPU busy time as frame time, with every number
    /// downstream confidently wrong and nothing to reveal it.
    /// </para>
    /// </summary>
    public static IEnumerable<double> ParseFrameTimes(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }

        var column = -1;

        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split(',');

            if (column < 0)
            {
                for (var i = 0; i < fields.Length; i++)
                {
                    // MsBetweenPresents in PresentMon 2.x, msBetweenPresents in 1.x and under
                    // --v1_metrics. Both are accepted; a machine may have either installed.
                    if (fields[i].Trim().Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase))
                    {
                        column = i;
                        break;
                    }
                }

                // Anything before the header - PresentMon writes warnings to the same stream -
                // is skipped rather than treated as data.
                continue;
            }

            if (column < fields.Length
                && double.TryParse(
                    fields[column].Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var frameTime))
            {
                yield return frameTime;
            }

            // A row whose value is "NA" or blank is skipped. PresentMon emits those for frames
            // it could not fully account for, and inventing a value would be worse than
            // having one fewer sample.
        }
    }
}
