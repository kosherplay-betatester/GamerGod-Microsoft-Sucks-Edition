using System.Globalization;
using GamerGod.Core.Forensics;

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
    /// The columns read out of a row, by name.
    ///
    /// <para>
    /// PresentMon 1.x and <c>--v1_metrics</c> spell these in lower camel case
    /// (<c>msBetweenPresents</c>); 2.x uses upper (<c>MsBetweenPresents</c>). Matching without
    /// regard to case covers both, so no alias table is needed and none can fall out of date.
    /// </para>
    /// </summary>
    private static readonly string[] KnownColumns =
    [
        "MsBetweenPresents",
        "MsCPUBusy",
        "MsCPUWait",
        "MsGPUBusy",
        "MsGPUWait",
        "MsGPULatency",
        "MsFlipDelay",
        "MsInPresentAPI",
        "MsRenderPresentLatency",
        "PresentMode",
        "TimeInMs",
    ];

    /// <summary>
    /// Every frame in the capture, in order, with each named column read by name.
    ///
    /// <para>
    /// A column PresentMon wrote as <c>NA</c>, or did not write at all, arrives as null. Not
    /// zero: zero is a measurement, and a measurement gets used. Whole captures come back with
    /// the CPU and GPU breakdown absent — every 1.x capture does — and the honest downstream
    /// answer to that is "cannot attribute", which is only reachable if the absence survives
    /// parsing.
    /// </para>
    /// </summary>
    public static IEnumerable<FrameRecord> ParseFrames(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }

        ColumnMap? columns = null;
        var index = 0;

        foreach (var line in csv.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var fields = trimmed.Split(',');

            if (columns is null)
            {
                // Anything before the header - PresentMon writes warnings to the same stream -
                // is skipped rather than treated as data. Header detection requires a field to
                // equal a known column name outright, so a warning that merely mentions one
                // cannot be mistaken for it.
                columns = ColumnMap.From(fields);
                continue;
            }

            yield return columns.Read(fields, index++);
        }
    }

    /// <summary>
    /// Frame times in milliseconds, in capture order.
    ///
    /// <para>
    /// The column is located by header name, never by index. An index-based parser appears to
    /// work and then silently reads a different metric the first time a PresentMon version
    /// inserts a column — reporting, say, GPU busy time as frame time, with every number
    /// downstream confidently wrong and nothing to reveal it.
    /// </para>
    ///
    /// <para>
    /// A row whose value is "NA" or blank is skipped. PresentMon emits those for frames it
    /// could not fully account for, and inventing a value would be worse than having one fewer
    /// sample.
    /// </para>
    /// </summary>
    public static IEnumerable<double> ParseFrameTimes(string csv)
    {
        foreach (var frame in ParseFrames(csv))
        {
            if (frame.MsBetweenPresents is { } frameTime)
            {
                yield return frameTime;
            }
        }
    }

    /// <summary>Where each column landed in this particular capture's header.</summary>
    private sealed class ColumnMap
    {
        private readonly Dictionary<string, int> _positions;

        private ColumnMap(Dictionary<string, int> positions) => _positions = positions;

        /// <summary>Reads a header row, or returns null if this line is not one.</summary>
        public static ColumnMap? From(string[] fields)
        {
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < fields.Length; i++)
            {
                var name = fields[i].Trim();

                foreach (var known in KnownColumns)
                {
                    if (name.Equals(known, StringComparison.OrdinalIgnoreCase))
                    {
                        positions[known] = i;
                        break;
                    }
                }
            }

            return positions.Count == 0 ? null : new ColumnMap(positions);
        }

        public FrameRecord Read(string[] fields, int index) => new()
        {
            Index = index,
            TimeInMs = Number(fields, "TimeInMs"),
            MsBetweenPresents = Number(fields, "MsBetweenPresents"),
            MsCPUBusy = Number(fields, "MsCPUBusy"),
            MsCPUWait = Number(fields, "MsCPUWait"),
            MsGPUBusy = Number(fields, "MsGPUBusy"),
            MsGPUWait = Number(fields, "MsGPUWait"),
            MsGPULatency = Number(fields, "MsGPULatency"),
            MsFlipDelay = Number(fields, "MsFlipDelay"),
            MsInPresentAPI = Number(fields, "MsInPresentAPI"),
            MsRenderPresentLatency = Number(fields, "MsRenderPresentLatency"),
            PresentMode = Text(fields, "PresentMode"),
        };

        private double? Number(string[] fields, string column) =>
            Text(fields, column) is { } value
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;

        private string? Text(string[] fields, string column)
        {
            if (!_positions.TryGetValue(column, out var position) || position >= fields.Length)
            {
                return null;
            }

            var value = fields[position].Trim();

            // "NA" is PresentMon saying it has no value, and it stays a null all the way down.
            return value.Length == 0 || value.Equals("NA", StringComparison.OrdinalIgnoreCase)
                ? null
                : value;
        }
    }
}
