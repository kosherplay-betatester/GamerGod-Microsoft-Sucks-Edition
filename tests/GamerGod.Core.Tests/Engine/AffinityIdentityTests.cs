using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Policy;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// Who a pid belongs to by the time a revert runs.
///
/// <para>
/// Capture and revert are not two halves of one moment. Revert is documented to run from a cold
/// process — after a crash, from the watchdog, from <c>gamergod off</c> in a shell opened an hour
/// later — and Windows recycles process ids aggressively in between. Every test here is about the
/// gap: the journal names pid 4242, and pid 4242 now belongs to somebody else.
/// </para>
///
/// <para>
/// The case that makes this more than tidiness is the game. It is excluded from the target list
/// at capture time and can never be journalled, but recycling puts it back within reach of a
/// record that names its pid — and writing an affinity mask onto a game is a Contact change to a
/// title that may be running kernel anti-cheat, reached through the one code path whose entire
/// job is to put the machine back.
/// </para>
/// </summary>
public sealed class AffinityIdentityTests
{
    private static CpuTopology Reference() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    /// <summary>Two background processes, confined and journalled, exactly as a session leaves them.</summary>
    private static async Task<(FakeAmbientOperations Os, CpuTopology Topology, System.Text.Json.JsonElement Capture)>
        ArmedSession()
    {
        var topology = Reference();
        var os = new FakeAmbientOperations()
            .AddProcess(4242, "chrome", 800)
            .AddProcess(4243, "msedge", 400);

        var mutation = new AffinityConfinementMutation(
            os, topology.AmbientMask, [.. os.Processes], topology);

        var capture = await mutation.CaptureAsync(default);
        await mutation.ApplyAsync(default);

        return (os, topology, capture);
    }

    [Fact]
    public async Task A_recycled_pid_now_held_by_the_game_is_never_written_to()
    {
        var (os, topology, capture) = await ArmedSession();

        // GamerGod died. Chrome exited, the machine went on running, and the pid came back
        // around — this time as the game, which is the one process that must never be touched.
        os.Processes.RemoveAll(p => p.Id == 4242);
        os.AddProcess(4242, "bf6", 6000);
        os.PlaceInGroup(4242, topology.GameDomain.Processors.Group, topology.GameDomain.Processors.Mask);

        await new AffinityConfinementMutation(os, topology.AmbientMask, [], topology)
            .RevertAsync(capture, default);

        // On the game cores, where it started. Not on the ambient mask the journal would have
        // written to a bare pid.
        Assert.Equal(topology.GameDomain.Processors, os.AffinityOf(4242));
        Assert.NotEqual(topology.AmbientMask, os.AffinityOf(4242));
    }

    [Fact]
    public async Task A_pid_that_belongs_to_nobody_is_left_alone()
    {
        var (os, topology, capture) = await ArmedSession();
        os.Processes.RemoveAll(p => p.Id == 4242);

        var report = await Revert(os, topology, capture);

        // Dead, so there was nothing to restore — and nothing that needed reporting as a
        // failure either. The other process still comes back.
        Assert.Null(report);
        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(4243).Mask);
    }

    [Fact]
    public async Task The_processes_that_are_still_themselves_are_restored()
    {
        // The guard must not be a blanket refusal. This is the ordinary case and it has to work.
        var (os, topology, capture) = await ArmedSession();

        Assert.Equal(topology.AmbientMask, os.AffinityOf(4242));

        var report = await Revert(os, topology, capture);

        Assert.Null(report);
        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(4242).Mask);
        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(4243).Mask);
    }

    [Fact]
    public async Task Reverting_twice_is_still_not_an_error()
    {
        var (os, topology, capture) = await ArmedSession();

        Assert.Null(await Revert(os, topology, capture));
        Assert.Null(await Revert(os, topology, capture));

        Assert.Equal(0xFFFFFFFFUL, os.AffinityOf(4242).Mask);
    }

    private static async Task<string?> Revert(
        FakeAmbientOperations os, CpuTopology topology, System.Text.Json.JsonElement capture)
    {
        try
        {
            await new AffinityConfinementMutation(os, topology.AmbientMask, [], topology)
                .RevertAsync(capture, default);

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
