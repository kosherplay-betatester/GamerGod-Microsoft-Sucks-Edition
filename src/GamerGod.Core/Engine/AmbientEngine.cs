using System.Collections.Immutable;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Safety;

namespace GamerGod.Core.Engine;

/// <summary>What a session did, in the user's terms rather than the ledger's.</summary>
public sealed record SessionReceipt
{
    public required string SessionId { get; init; }

    public required ImmutableArray<string> Applied { get; init; }

    public required ImmutableArray<string> Refused { get; init; }

    public required ImmutableArray<(string Key, string Error)> Failed { get; init; }

    public required string IntegritySummary { get; init; }

    public int ProcessesDemoted { get; init; }

    public int ProcessesConfined { get; init; }

    public int ServicesStopped { get; init; }

    public bool Partitioned { get; init; }

    /// <summary>True when this describes a preview rather than something that happened.</summary>
    public bool WasDryRun { get; init; }

    /// <summary>
    /// Plain-language account of what happened. Charter Article VII: this states what was
    /// done, never what it gained — that number belongs to the measurement harness.
    ///
    /// <para>
    /// A dry run is written in the conditional. Telling somebody that 113 processes "were
    /// moved" when nothing was touched is the kind of small dishonesty that costs a tool its
    /// credibility, and credibility is the only thing this project actually has.
    /// </para>
    /// </summary>
    public string Explain()
    {
        var lines = new List<string>();

        if (Partitioned && ProcessesConfined > 0)
        {
            lines.Add(WasDryRun
                ? $"{Count(ProcessesConfined, "background process")} would move off your game's cores."
                : $"{Count(ProcessesConfined, "background process")} moved off your game's cores.");
        }

        if (ProcessesDemoted > 0)
        {
            lines.Add(WasDryRun
                ? $"{Count(ProcessesDemoted, "process")} would be set to efficiency mode."
                : $"{Count(ProcessesDemoted, "process")} set to efficiency mode.");
        }

        if (ServicesStopped > 0)
        {
            lines.Add(WasDryRun
                ? $"{Count(ServicesStopped, "service")} would stop for this session."
                : $"{Count(ServicesStopped, "service")} stopped for this session.");
        }

        if (lines.Count == 0)
        {
            lines.Add(WasDryRun
                ? "Nothing would change. Your machine is already out of your game's way."
                : "Nothing needed changing. Your machine was already out of your game's way.");
        }

        if (!Failed.IsEmpty)
        {
            lines.Add($"{Count(Failed.Length, "change")} could not be applied, and {(Failed.Length == 1 ? "was" : "were")} recorded.");
        }

        return string.Join(" ", lines);
    }

    /// <summary>
    /// "1 process", "12 processes". Real pluralisation rather than "process(es)", which
    /// reads as a placeholder somebody forgot to finish.
    /// </summary>
    private static string Count(int value, string noun)
    {
        if (value == 1)
        {
            return $"1 {noun}";
        }

        var plural = noun.EndsWith('s') || noun.EndsWith("ch", StringComparison.Ordinal)
            ? $"{noun}es"
            : $"{noun}s";

        return $"{value} {plural}";
    }
}

public sealed record AmbientOptions
{
    /// <summary>Confine background work to the ambient domain. The headline lever.</summary>
    public bool ConfineToAmbientDomain { get; init; } = true;

    /// <summary>Demote background processes to Windows' efficiency mode.</summary>
    public bool DemoteToEfficiencyMode { get; init; } = true;

    /// <summary>
    /// Services to stop for the session. Empty by default — Charter Article VII, since the
    /// measured effect of service suppression is close to zero on a healthy machine and the
    /// user should opt in rather than inherit it.
    /// </summary>
    public ImmutableArray<string> Services { get; init; } = [];

    /// <summary>Activate a GamerGod power scheme. Off by default; measured effect is small.</summary>
    public bool ManagePowerScheme { get; init; }

    /// <summary>Report what would happen and change nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>
    /// True when the caller lives for the whole session — the background service — and false
    /// for a command that applies and exits.
    ///
    /// <para>
    /// This picks the confinement mechanism. A resident caller can hold a job object, whose
    /// limits Windows lifts automatically if the process dies. A caller that exits cannot:
    /// the handle would close immediately and the confinement would evaporate while the
    /// receipt claimed otherwise, so it uses journalled per-process affinity instead.
    /// </para>
    /// </summary>
    public bool CallerStaysResident { get; init; }
}

/// <summary>
/// Turns a machine and a policy into a set of reversible changes, and runs them through the
/// ledger.
///
/// <para>
/// Everything this engine produces is <see cref="MutationVisibility.Ambient"/>. It never
/// builds a mutation that touches the game, which is why it needs no special handling for
/// protected titles: a permit that refuses contact refuses nothing this engine emits.
/// </para>
/// </summary>
public sealed class AmbientEngine(IAmbientOperations os, MutationLedger ledger)
{
    /// <summary>
    /// Builds the change set for a session. Public and side-effect-free so the CLI can show
    /// exactly what would happen before anything is done.
    /// </summary>
    public async ValueTask<ImmutableArray<IMutation>> PlanAsync(
        CpuTopology topology,
        AmbientOptions options,
        int? gameProcessId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(options);

        var mutations = ImmutableArray.CreateBuilder<IMutation>();

        if (options.ManagePowerScheme)
        {
            mutations.Add(new PowerSchemeMutation(os, "GamerGod"));
        }

        foreach (var service in options.Services)
        {
            // Filtered here so a protected name in a profile is dropped with an explanation
            // rather than reaching a mutation that would throw at apply time.
            if (!ServiceSafetyPolicy.IsProtected(service))
            {
                mutations.Add(new ServiceSuspensionMutation(os, service));
            }
        }

        var background = await BackgroundProcessesAsync(gameProcessId, cancellationToken)
            .ConfigureAwait(false);

        if (options.DemoteToEfficiencyMode && !background.IsEmpty)
        {
            mutations.Add(new EfficiencyModeMutation(os, background));
        }

        if (options.ConfineToAmbientDomain && topology.CanPartition && !background.IsEmpty)
        {
            // Affinity for a caller that exits, a job object for one that stays resident.
            //
            // A job object reverts itself when the last handle closes, which is the better
            // guarantee - but it also means a command-line tool that applies and exits
            // releases the confinement microseconds later while reporting success. Choosing
            // by caller lifetime rather than by elegance is what keeps the receipt honest.
            mutations.Add(options.CallerStaysResident
                ? new DomainConfinementMutation(os, topology.AmbientMask, background, topology)
                : new AffinityConfinementMutation(os, topology.AmbientMask, background, topology));
        }

        return mutations.ToImmutable();
    }

    /// <summary>
    /// Applies a session. Returns a receipt describing what was done — and, when the safety
    /// gate refuses, what was not.
    ///
    /// <para>
    /// <paramref name="owner"/> is the process whose death means this session has been
    /// orphaned, and it belongs with <see cref="AmbientOptions.CallerStaysResident"/>: a
    /// caller that lives for the whole session names itself, so a boot-recovery pass can leave
    /// the session alone instead of ending it when the service restarts. A caller that applies
    /// and exits names nobody, because there would be nobody left to claim it.
    /// </para>
    /// </summary>
    public async ValueTask<SessionReceipt> EnterAsync(
        string sessionId,
        CpuTopology topology,
        MutationPermit permit,
        AmbientOptions options,
        RestoreStatus restoreStatus,
        int? gameProcessId = null,
        ProcessIdentity? owner = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(permit);

        var mutations = await PlanAsync(topology, options, gameProcessId, cancellationToken)
            .ConfigureAwait(false);

        var safety = SafetyGate.Evaluate(mutations, restoreStatus);
        if (!safety.MayProceed)
        {
            return new SessionReceipt
            {
                SessionId = sessionId,
                Applied = [],
                Refused = [.. mutations.Select(m => m.Key)],
                Failed = [],
                IntegritySummary = safety.Explanation,
            };
        }

        if (options.DryRun)
        {
            // A dry run has to report what would happen, in the same shape as a real one.
            // Reporting zeroes would make the preview useless for the one question it exists
            // to answer: is this about to do something reasonable to my machine?
            var demotion = mutations.OfType<EfficiencyModeMutation>().FirstOrDefault();
            var jobConfinement = mutations.OfType<DomainConfinementMutation>().FirstOrDefault();
            var affinityConfinement = mutations.OfType<AffinityConfinementMutation>().FirstOrDefault();
            var confinedCount = jobConfinement?.TargetCount ?? affinityConfinement?.TargetCount ?? 0;

            return new SessionReceipt
            {
                SessionId = sessionId,
                Applied = [],
                Refused = [],
                Failed = [],
                IntegritySummary = $"Dry run. Nothing was changed.",
                WasDryRun = true,
                ProcessesDemoted = demotion?.TargetCount ?? 0,
                ProcessesConfined = confinedCount,
                ServicesStopped = mutations.Count(m => m.Key.StartsWith("service:", StringComparison.Ordinal)),
                Partitioned = confinedCount > 0,
            };
        }

        var report = await ledger
            .ApplyAsync(sessionId, mutations, permit, owner, cancellationToken)
            .ConfigureAwait(false);

        return new SessionReceipt
        {
            SessionId = sessionId,
            Applied = report.Applied,
            Refused = report.Refused,
            Failed = report.Failed,
            IntegritySummary = permit.Explain(),
            ProcessesDemoted = AppliedCount(
                mutations.OfType<EfficiencyModeMutation>().FirstOrDefault(),
                m => m.Key,
                m => m.TargetCount,
                report),
            ProcessesConfined =
                AppliedCount(mutations.OfType<DomainConfinementMutation>().FirstOrDefault(), m => m.Key, m => m.TargetCount, report)
                + AppliedCount(mutations.OfType<AffinityConfinementMutation>().FirstOrDefault(), m => m.Key, m => m.TargetCount, report),
            ServicesStopped = report.Applied.Count(k => k.StartsWith("service:", StringComparison.Ordinal)),
            Partitioned = report.Applied.Contains("confine:ambient-domain") || report.Applied.Contains("affinity:ambient-domain"),
        };
    }

    /// <summary>Reverses everything the journal records. Safe to call at any time.</summary>
    public ValueTask<RevertReport> ExitAsync(CancellationToken cancellationToken = default) =>
        ledger.RevertAsync(cancellationToken);

    /// <summary>
    /// Processes eligible to be moved out of the game's way.
    ///
    /// <para>
    /// Three exclusions, in order of importance: the safety policy, which is absolute; the
    /// game itself, because moving it would be a contact change; and anything too small to
    /// be worth the call, since demoting a 4 MB helper costs a syscall and gains nothing.
    /// </para>
    /// </summary>
    private async ValueTask<ImmutableArray<ProcessInfo>> BackgroundProcessesAsync(
        int? gameProcessId,
        CancellationToken cancellationToken)
    {
        var all = await os.ListProcessesAsync(cancellationToken).ConfigureAwait(false);

        return all
            .Where(p => p.Id != gameProcessId)
            .Where(p => p.Id > 4)
            .Where(p => !ProcessSafetyPolicy.IsProtected(p.Name))
            .Where(p => p.IsWorthDemoting)
            .ToImmutableArray();
    }

    /// <summary>
    /// How many things a mutation affected, or zero if it never applied. Reads the count
    /// from the mutation itself rather than parsing it back out of its description — a
    /// receipt that lied because a wording change broke a parser would undermine the one
    /// feature whose entire job is being believable.
    /// </summary>
    private static int AppliedCount<T>(
        T? mutation,
        Func<T, string> key,
        Func<T, int> count,
        ApplyReport report)
        where T : class =>
        mutation is not null && report.Applied.Contains(key(mutation)) ? count(mutation) : 0;
}
