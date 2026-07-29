using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using GamerGod.Core.Hardware;
using GamerGod.Core.Mutations;
using GamerGod.Core.Safety;

namespace GamerGod.Core.Engine;

/// <summary>
/// Demotes background processes to Windows' own efficiency mode.
///
/// <para>
/// This is the cleanest lever GamerGod has. It uses the mechanism Microsoft built for the
/// purpose and exposes in Task Manager, it works identically on AMD and Intel, it is exactly
/// reversible, and it is invisible to the game — nothing about the game process, its handles
/// or its view of the system changes. It simply stops competing with it.
/// </para>
///
/// <para>
/// Each process is captured individually, because some were already in efficiency mode
/// before GamerGod ran and must be left that way on exit. Restoring everything to "normal"
/// would be a change of its own, dressed up as a revert.
/// </para>
/// </summary>
public sealed class EfficiencyModeMutation(
    IAmbientOperations os,
    ImmutableArray<ProcessInfo> targets) : IMutation
{
    public string Key => "ecoqos:background";

    public MutationTier Tier => MutationTier.ProcessDemotion;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    /// <summary>How many processes this targets, for the session receipt.</summary>
    public int TargetCount => targets.Length;

    public string Describe() => $"demote {TargetCount} background process(es) to efficiency mode";

    public async ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        var captured = ImmutableArray.CreateBuilder<DemotedProcess>(targets.Length);

        foreach (var process in targets)
        {
            bool wasEfficient;
            try
            {
                wasEfficient = await os.IsEfficiencyModeAsync(process.Id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A process that vanished between listing and capture is not an error - it is
                // the normal churn of a running desktop. Skipping it is correct; failing the
                // whole session because Notepad closed would not be.
                continue;
            }

            captured.Add(new DemotedProcess(process.Id, process.Name, wasEfficient));
        }

        return JsonSerializer.SerializeToElement(
            new EfficiencyCapture(captured.ToImmutable()), AmbientJson.Default.EfficiencyCapture);
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        foreach (var process in targets)
        {
            // Belt and braces. The engine filters against the safety policy before building
            // this list, and the check is repeated here because this is the last point
            // before a real API call - a future caller that forgets to filter must still be
            // unable to suspend the machine's thermal management.
            if (ProcessSafetyPolicy.IsProtected(process.Name))
            {
                continue;
            }

            try
            {
                await os.SetEfficiencyModeAsync(process.Id, true, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Exited, or protected by the system. Neither is worth abandoning the rest.
            }
        }
    }

    public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        var state = capture.Deserialize(AmbientJson.Default.EfficiencyCapture);
        if (state is null)
        {
            return;
        }

        foreach (var process in state.Processes)
        {
            if (process.WasAlreadyEfficient)
            {
                // Left as found. Turning this off would be a change, not a revert.
                continue;
            }

            try
            {
                await os.SetEfficiencyModeAsync(process.Id, false, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process is gone, which is the outcome revert wanted anyway.
            }
        }
    }

    internal sealed record DemotedProcess(int Id, string Name, bool WasAlreadyEfficient);

    internal sealed record EfficiencyCapture(ImmutableArray<DemotedProcess> Processes);
}

/// <summary>
/// Confines background processes to the ambient performance domain, clearing the game's
/// cores of everything else.
///
/// <para>
/// This is the lever the whole project is named for, and on an asymmetric-cache part it is
/// worth more than everything else combined. It is also entirely ambient: the game is never
/// touched, never opened, never told anything. Its cores simply stop having other work on
/// them.
/// </para>
/// </summary>
public sealed class DomainConfinementMutation(
    IAmbientOperations os,
    ProcessorMask ambientMask,
    ImmutableArray<ProcessInfo> targets,
    CpuTopology topology) : IMutation
{
    private IConfinement? _confinement;

    public string Key => "confine:ambient-domain";

    public MutationTier Tier => MutationTier.ProcessDemotion;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    /// <summary>How many processes this targets, for the session receipt.</summary>
    public int TargetCount => targets.Length;

    public string Describe() =>
        $"confine {TargetCount} background process(es) to {ambientMask.Count} logical processor(s)";

    public ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        // Nothing durable to capture. A job object's limits exist only while a handle to it
        // is open, so the operating system reverses this whether or not GamerGod survives -
        // which is a stronger guarantee than anything the journal could offer.
        return ValueTask.FromResult(JsonSerializer.SerializeToElement(
            new ConfinementCapture(ambientMask.Group, ambientMask.Mask, targets.Length),
            AmbientJson.Default.ConfinementCapture));
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        var safety = RoutingSafety.Validate(topology.GameDomain.Processors, ambientMask, topology);
        if (!safety.IsSafe)
        {
            // Refused rather than clamped. A silently corrected plan hides the bug that
            // produced it, and this particular bug leaves Windows with nowhere to run.
            throw new InvalidOperationException(
                $"Refusing to confine background work: {safety.Explain()}");
        }

        var eligible = targets
            .Where(p => !ProcessSafetyPolicy.IsProtected(p.Name))
            .Select(p => p.Id)
            .ToArray();

        if (eligible.Length == 0)
        {
            return;
        }

        _confinement = await os
            .ConfineAsync("GamerGod.Ambient", ambientMask, eligible, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        if (_confinement is not null)
        {
            await _confinement.DisposeAsync().ConfigureAwait(false);
            _confinement = null;
        }

        // A cold recovery pass holds no handle, and needs none: the process that owned the
        // job object is gone, so Windows released the limits when it exited.
    }

    internal sealed record ConfinementCapture(ushort Group, ulong Mask, int ProcessCount);
}

/// <summary>
/// Confines background processes to the ambient domain by setting each one's processor
/// affinity, and restores every original mask on exit.
///
/// <para>
/// The alternative — a job object — is more elegant and reverts itself, but its limits exist
/// only while a handle to it is open. A command-line tool that applies and exits would
/// release the confinement microseconds later while reporting success, which is precisely
/// the kind of quiet dishonesty this project cannot afford. An affinity mask outlives the
/// process that set it, so it works for a short-lived caller at the cost of having to be
/// journalled and put back deliberately.
/// </para>
///
/// <para>
/// Both mechanisms have a place: this one for the command line, the job object for the
/// resident service, which can hold the handle for the life of a session and get automatic
/// reversal on a crash.
/// </para>
/// </summary>
public sealed class AffinityConfinementMutation(
    IAmbientOperations os,
    ProcessorMask ambientMask,
    ImmutableArray<ProcessInfo> targets,
    CpuTopology topology) : IMutation
{
    public string Key => "affinity:ambient-domain";

    public MutationTier Tier => MutationTier.CpuRouting;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    public int TargetCount => targets.Length;

    public string Describe() =>
        $"move {TargetCount} background process(es) onto {ambientMask.Count} logical processor(s)";

    public async ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        var captured = ImmutableArray.CreateBuilder<AffinityRecord>(targets.Length);

        foreach (var process in targets)
        {
            try
            {
                var mask = await os.GetAffinityAsync(process.Id, cancellationToken).ConfigureAwait(false);
                captured.Add(new AffinityRecord(process.Id, process.Name, mask.Group, mask.Mask));
            }
            catch (Exception)
            {
                // Exited between listing and capture. Skipping is correct; a process we
                // never changed needs no record.
            }
        }

        return JsonSerializer.SerializeToElement(
            new AffinityCapture(captured.ToImmutable()), AmbientJson.Default.AffinityCapture);
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        var safety = RoutingSafety.Validate(topology.GameDomain.Processors, ambientMask, topology);
        if (!safety.IsSafe)
        {
            throw new InvalidOperationException(
                $"Refusing to move background work: {safety.Explain()}");
        }

        foreach (var process in targets)
        {
            if (ProcessSafetyPolicy.IsProtected(process.Name))
            {
                continue;
            }

            try
            {
                await os.SetAffinityAsync(process.Id, ambientMask, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Protected by the system, or gone. Neither is worth abandoning the rest.
            }
        }
    }

    public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        var state = capture.Deserialize(AmbientJson.Default.AffinityCapture);
        if (state is null)
        {
            return;
        }

        foreach (var record in state.Processes)
        {
            try
            {
                await os.SetAffinityAsync(
                        record.Id,
                        new ProcessorMask(record.Group, record.Mask),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The process exited, which achieves the same thing: its affinity went with it.
            }
        }
    }

    internal sealed record AffinityRecord(int Id, string Name, ushort Group, ulong Mask);

    internal sealed record AffinityCapture(ImmutableArray<AffinityRecord> Processes);
}

/// <summary>
/// Stops a background service for the session and starts it again on exit.
///
/// <para>
/// Start type is never modified. Stopping a service is inherently undone by a reboot;
/// changing its start type would outlive one, and would quietly break the trigger-started
/// services Windows relies on.
/// </para>
/// </summary>
public sealed class ServiceSuspensionMutation(IAmbientOperations os, string serviceName) : IMutation
{
    public string Key => $"service:{serviceName}";

    public MutationTier Tier => MutationTier.Service;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    public string Describe() => $"stop the {serviceName} service for this session";

    public async ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        var info = await os.QueryServiceAsync(serviceName, cancellationToken).ConfigureAwait(false);

        return JsonSerializer.SerializeToElement(
            new ServiceCapture(serviceName, info?.IsRunning ?? false, info is not null),
            AmbientJson.Default.ServiceCapture);
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        // The list this comes from is already filtered, but a service is one of the few
        // things whose loss a user cannot work around mid-game, so the check is repeated
        // at the point of no return.
        if (ServiceSafetyPolicy.IsProtected(serviceName))
        {
            throw new InvalidOperationException(
                $"Refusing to stop '{serviceName}': {ServiceSafetyPolicy.Explain(serviceName)!.Explanation}");
        }

        await os.StopServiceAsync(serviceName, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        var state = capture.Deserialize(AmbientJson.Default.ServiceCapture);

        // Only restart what was running. Starting a service that was stopped before GamerGod
        // ran would be a change of its own.
        if (state is null || !state.Existed || !state.WasRunning)
        {
            return;
        }

        await os.StartServiceAsync(state.Name, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record ServiceCapture(string Name, bool WasRunning, bool Existed);
}

/// <summary>
/// Activates a GamerGod power scheme for the session.
///
/// <para>
/// The user's own plan is never edited. A duplicate is made, activated, and the original
/// reactivated on exit — so a revert that fails still leaves the user's settings intact,
/// and the worst case is an unused scheme in their list rather than a modified one.
/// </para>
/// </summary>
public sealed class PowerSchemeMutation(IAmbientOperations os, string schemeName) : IMutation
{
    public string Key => "power:scheme";

    public MutationTier Tier => MutationTier.Power;

    public MutationVisibility Visibility => MutationVisibility.Ambient;

    public bool IsBootPersistent => false;

    public string Describe() => $"activate the {schemeName} power scheme";

    public async ValueTask<JsonElement> CaptureAsync(CancellationToken cancellationToken)
    {
        var original = await os.GetActivePowerSchemeAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(
            new PowerCapture(original, null), AmbientJson.Default.PowerCapture);
    }

    public async ValueTask ApplyAsync(CancellationToken cancellationToken)
    {
        var original = await os.GetActivePowerSchemeAsync(cancellationToken).ConfigureAwait(false);
        var duplicate = await os
            .DuplicatePowerSchemeAsync(original, schemeName, cancellationToken)
            .ConfigureAwait(false);

        await os.SetActivePowerSchemeAsync(duplicate, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask RevertAsync(JsonElement capture, CancellationToken cancellationToken)
    {
        var state = capture.Deserialize(AmbientJson.Default.PowerCapture);
        if (state is null)
        {
            return;
        }

        await os.SetActivePowerSchemeAsync(state.OriginalScheme, cancellationToken).ConfigureAwait(false);
    }

    internal sealed record PowerCapture(Guid OriginalScheme, Guid? CreatedScheme);
}

/// <summary>
/// Source-generated serialisation for every ambient capture. Reflection-based JSON would
/// fail after trimming, which is to say it would fail in a shipped build during recovery.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(EfficiencyModeMutation.EfficiencyCapture))]
[JsonSerializable(typeof(DomainConfinementMutation.ConfinementCapture))]
[JsonSerializable(typeof(ServiceSuspensionMutation.ServiceCapture))]
[JsonSerializable(typeof(PowerSchemeMutation.PowerCapture))]
[JsonSerializable(typeof(AffinityConfinementMutation.AffinityCapture))]
internal sealed partial class AmbientJson : JsonSerializerContext;
