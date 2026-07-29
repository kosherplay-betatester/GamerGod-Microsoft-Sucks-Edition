using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// A simulated machine for the ambient levers.
///
/// <para>
/// These mutations are the first code in the project that changes a real system, so being
/// able to drive every one of them — including their failure paths — without administrator
/// rights and without anything real to break is what makes them safe to develop.
/// </para>
/// </summary>
internal sealed class FakeAmbientOperations : IAmbientOperations
{
    private readonly Dictionary<int, bool> _efficiency = new();
    private readonly Dictionary<int, ProcessorMask> _affinity = new();
    private readonly Dictionary<string, ServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _schemes = [];

    public List<ProcessInfo> Processes { get; } = [];

    public Guid ActiveScheme { get; private set; } = Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e");

    public List<string> Log { get; } = [];

    /// <summary>Process ids whose efficiency-mode call should throw, modelling a vanished process.</summary>
    public HashSet<int> FailEfficiencyFor { get; } = [];

    /// <summary>Service names whose stop should throw.</summary>
    public HashSet<string> FailStopFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeConfinement? ActiveConfinement { get; private set; }

    public FakeAmbientOperations AddProcess(int id, string name, long megabytes, bool efficient = false)
    {
        Processes.Add(new ProcessInfo(id, name, megabytes * 1024 * 1024));
        _efficiency[id] = efficient;
        return this;
    }

    public FakeAmbientOperations AddService(string name, bool running, string startType = "Automatic")
    {
        _services[name] = new ServiceInfo(name, running, startType);
        return this;
    }

    public bool IsEfficient(int id) => _efficiency.TryGetValue(id, out var v) && v;

    public bool IsServiceRunning(string name) =>
        _services.TryGetValue(name, out var s) && s.IsRunning;

    public int SchemeCount => _schemes.Count;

    // ---- IAmbientOperations ------------------------------------------

    public ValueTask<ImmutableArray<ProcessInfo>> ListProcessesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(Processes.ToImmutableArray());

    public ValueTask<ServiceInfo?> QueryServiceAsync(string name, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_services.TryGetValue(name, out var s) ? s : null);

    public ValueTask<bool> IsEfficiencyModeAsync(int processId, CancellationToken cancellationToken)
    {
        if (FailEfficiencyFor.Contains(processId))
        {
            throw new InvalidOperationException($"process {processId} has exited");
        }

        return ValueTask.FromResult(IsEfficient(processId));
    }

    public ValueTask SetEfficiencyModeAsync(int processId, bool enabled, CancellationToken cancellationToken)
    {
        if (FailEfficiencyFor.Contains(processId))
        {
            throw new InvalidOperationException($"process {processId} has exited");
        }

        _efficiency[processId] = enabled;
        Log.Add($"efficiency:{processId}={enabled}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<IConfinement> ConfineAsync(
        string name,
        ProcessorMask mask,
        IEnumerable<int> processIds,
        CancellationToken cancellationToken)
    {
        var ids = processIds.ToArray();
        ActiveConfinement = new FakeConfinement(name, mask, ids, () => ActiveConfinement = null);
        Log.Add($"confine:{name}={ids.Length}@0x{mask.Mask:X}");
        return ValueTask.FromResult<IConfinement>(ActiveConfinement);
    }

    /// <summary>Affinity as the simulated machine sees it. Full mask until something sets it.</summary>
    public ProcessorMask AffinityOf(int id) =>
        _affinity.TryGetValue(id, out var m) ? m : new ProcessorMask(0, 0xFFFFFFFFUL);

    public ValueTask<ProcessorMask> GetAffinityAsync(int processId, CancellationToken cancellationToken)
    {
        if (FailEfficiencyFor.Contains(processId))
        {
            throw new InvalidOperationException($"process {processId} has exited");
        }

        return ValueTask.FromResult(AffinityOf(processId));
    }

    public ValueTask SetAffinityAsync(
        int processId, ProcessorMask mask, CancellationToken cancellationToken)
    {
        if (FailEfficiencyFor.Contains(processId))
        {
            throw new InvalidOperationException($"process {processId} has exited");
        }

        if (mask.IsEmpty)
        {
            throw new ArgumentException("empty affinity would make the process unschedulable");
        }

        _affinity[processId] = mask;
        Log.Add($"affinity:{processId}=0x{mask.Mask:X}");
        return ValueTask.CompletedTask;
    }

    public ValueTask StopServiceAsync(string name, CancellationToken cancellationToken)
    {
        if (FailStopFor.Contains(name))
        {
            throw new InvalidOperationException($"{name} refused to stop");
        }

        if (_services.TryGetValue(name, out var s))
        {
            _services[name] = s with { IsRunning = false };
        }

        Log.Add($"stop:{name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask StartServiceAsync(string name, CancellationToken cancellationToken)
    {
        if (_services.TryGetValue(name, out var s))
        {
            _services[name] = s with { IsRunning = true };
        }

        Log.Add($"start:{name}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> GetActivePowerSchemeAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(ActiveScheme);

    public ValueTask SetActivePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken)
    {
        ActiveScheme = scheme;
        Log.Add($"power:{scheme}");
        return ValueTask.CompletedTask;
    }

    public ValueTask<Guid> DuplicatePowerSchemeAsync(
        Guid source, string name, CancellationToken cancellationToken)
    {
        var created = Guid.NewGuid();
        _schemes.Add(created);
        Log.Add($"duplicate:{name}");
        return ValueTask.FromResult(created);
    }

    public ValueTask DeletePowerSchemeAsync(Guid scheme, CancellationToken cancellationToken)
    {
        _schemes.Remove(scheme);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeConfinement(
    string name,
    ProcessorMask mask,
    int[] processIds,
    Action onDispose) : IConfinement
{
    public string Name => name;

    public int ProcessCount => processIds.Length;

    public ProcessorMask Mask => mask;

    public IReadOnlyList<int> ProcessIds => processIds;

    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        onDispose();
        return ValueTask.CompletedTask;
    }
}
