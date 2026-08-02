using GamerGod.Core.Ledger;
using GamerGod.Core.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamerGod.Service.Tests;

/// <summary>
/// Charter Article X's third escape path, now that something arms it.
///
/// <para>
/// The watchdog was written, tested and shipped with nothing constructing it, so a crash was
/// undone at the next boot rather than at the moment it happened. This is the piece that runs it.
/// </para>
///
/// <para>
/// It has no channel of its own and needs none: the journal has recorded who owns each session
/// since ownership existed, durably, before the first change is applied. A named pipe into a
/// LocalSystem service would be a second channel carrying the same fact, with an authentication
/// problem attached, and it would be missing exactly when it mattered — a process being killed
/// does not get to send a message.
/// </para>
/// </summary>
public sealed class WatchdogServiceTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);

    private static (RecoveryService Boot, WatchdogService Watchdog) Build(IBootRecoveryPass pass)
    {
        var boot = new RecoveryService(pass, NullLogger<RecoveryService>.Instance);
        return (boot, new WatchdogService(pass, boot, NullLogger<WatchdogService>.Instance, Fast));
    }

    [Fact]
    public async Task It_keeps_checking_after_the_boot_pass_has_finished()
    {
        // The whole point. Before this, the service ran one pass at start and then idled for the
        // life of the machine.
        var pass = new CountingPass();
        var (boot, watchdog) = Build(pass);

        await boot.StartAsync(CancellationToken.None);
        await watchdog.StartAsync(CancellationToken.None);
        await boot.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        await WaitForTicksAsync(watchdog, 3);

        await watchdog.StopAsync(CancellationToken.None);
        await boot.StopAsync(CancellationToken.None);

        Assert.True(pass.Runs > 3, $"the watchdog ran the pass {pass.Runs} times.");
    }

    [Fact]
    public async Task It_waits_for_boot_recovery_rather_than_racing_it()
    {
        // Both take the same journal lock, so overlapping would be safe — but it would run a
        // full pass twice at every start and write two accounts of one machine into the event
        // log, which is the only record anybody has of what a LocalSystem service did.
        var gate = new TaskCompletionSource();
        var pass = new CountingPass(gate.Task);
        var (boot, watchdog) = Build(pass);

        await boot.StartAsync(CancellationToken.None);
        await watchdog.StartAsync(CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.Equal(0, watchdog.Ticks);

        gate.SetResult();
        await boot.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTicksAsync(watchdog, 1);

        await watchdog.StopAsync(CancellationToken.None);
        await boot.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_pass_that_throws_does_not_bring_the_host_down()
    {
        // Registered with restart-on-failure, so an exception escaping is not a crash but a
        // crash loop, on LocalSystem, with nobody watching a console. It must keep ticking.
        var (boot, watchdog) = Build(new ThrowingPass());

        await boot.StartAsync(CancellationToken.None);
        await watchdog.StartAsync(CancellationToken.None);
        await boot.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        await WaitForTicksAsync(watchdog, 3);

        var execute = watchdog.ExecuteTask;
        await watchdog.StopAsync(CancellationToken.None);
        await boot.StopAsync(CancellationToken.None);

        Assert.False(execute!.IsFaulted, execute.Exception?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Stopping_finishes_promptly()
    {
        // The service control manager's stop timeout is 30 seconds. A stop that hangs is a
        // machine that will not shut down.
        var (boot, watchdog) = Build(new CountingPass());

        await boot.StartAsync(CancellationToken.None);
        await watchdog.StartAsync(CancellationToken.None);
        await boot.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForTicksAsync(watchdog, 2);

        var stopping = watchdog.StopAsync(CancellationToken.None);
        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10))));

        await boot.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task It_stops_without_ever_ticking_when_the_boot_pass_never_finishes()
    {
        // A machine being shut down while a long revert is in flight. Waiting on
        // RecoveryCompleted must observe the stop rather than hang on it for ever.
        var (boot, watchdog) = Build(new CountingPass(new TaskCompletionSource().Task));

        await boot.StartAsync(CancellationToken.None);
        await watchdog.StartAsync(CancellationToken.None);

        var stopping = watchdog.StopAsync(CancellationToken.None);
        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10))));

        Assert.Equal(0, watchdog.Ticks);
        await boot.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void A_zero_interval_is_refused()
    {
        // The safety net must not become the contention it exists to remove.
        var boot = new RecoveryService(new CountingPass(), NullLogger<RecoveryService>.Instance);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WatchdogService(
                new CountingPass(), boot, NullLogger<WatchdogService>.Instance, TimeSpan.Zero));
    }

    [Fact]
    public void The_default_interval_is_fast_enough_to_be_worth_having()
    {
        // "At the moment it happens" rather than "at the next boot" is the entire claim.
        Assert.InRange(WatchdogService.DefaultInterval, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    private static async Task WaitForTicksAsync(WatchdogService watchdog, int ticks)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (watchdog.Ticks < ticks && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(watchdog.Ticks >= ticks, $"only {watchdog.Ticks} ticks in 10 seconds.");
    }

    private sealed class CountingPass(Task? gate = null) : IBootRecoveryPass
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public async ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken)
        {
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken);
            }

            Interlocked.Increment(ref _runs);
            return new RecoveryOutcome { HadOutstandingChanges = false };
        }
    }

    private sealed class ThrowingPass : IBootRecoveryPass
    {
        public ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken) =>
            throw new IOException("the journal is unreadable right now");
    }
}
