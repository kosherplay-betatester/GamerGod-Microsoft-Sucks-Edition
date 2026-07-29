namespace GamerGod.Core.Hardware;

/// <summary>
/// Supplies the raw processor layout. Implemented by <c>GamerGod.Windows</c> against
/// <c>GetLogicalProcessorInformationEx</c>, and by test doubles against fixtures.
///
/// <para>
/// This interface exists so that domain classification — the single highest-consequence
/// piece of logic in GamerGod — is testable without a machine, without admin rights, and
/// against hardware nobody on the project owns.
/// </para>
/// </summary>
public interface ITopologyProvider
{
    /// <summary>Reads the current processor layout.</summary>
    ProcessorSnapshot Capture();
}

/// <summary>
/// Convenience wrapper that captures and classifies in one step.
/// </summary>
public static class TopologyProviderExtensions
{
    public static CpuTopology Classify(this ITopologyProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return PerformanceDomainClassifier.Classify(provider.Capture());
    }
}
