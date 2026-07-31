using System.Reflection;
using GamerGod.Core.Recovery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace GamerGod.Service.Tests;

/// <summary>
/// The service host itself, as opposed to the recovery logic it runs.
///
/// <para>
/// Two things here are constraints rather than preferences, and both are invisible in code
/// review. The service control manager gives a service 30 seconds to report started, and
/// reverting a session can take longer than that — a boot recovery pass that ran during
/// <c>StartAsync</c> would be killed by Windows part of the way through, which is worse than
/// not running at all. And the binary has to be called <c>gmsvc.exe</c>, because the
/// installer registers that name.
/// </para>
/// </summary>
public sealed class RecoveryServiceTests
{
    private static RecoveryService Build(IBootRecoveryPass pass) =>
        new(pass, NullLogger<RecoveryService>.Instance);

    [Fact]
    public async Task Recovery_does_not_hold_up_the_start_the_service_manager_is_waiting_for()
    {
        var gate = new TaskCompletionSource();
        var pass = new GatedRecoveryPass(gate.Task);
        var service = Build(pass);

        var start = service.StartAsync(CancellationToken.None);

        // Returned to the service control manager while the pass is still running. Anything
        // else and a slow revert becomes a service Windows kills for failing to start.
        Assert.True(start.IsCompleted, "StartAsync did not return until recovery had finished.");
        Assert.False(service.RecoveryCompleted.IsCompleted);

        gate.SetResult();

        var outcome = await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(outcome.HadOutstandingChanges);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_service_does_not_override_the_start_path_at_all()
    {
        // The structural half of the assertion above. Overriding StartAsync to "do the
        // recovery first" is the natural mistake, and it reintroduces the 30-second deadline.
        var start = typeof(RecoveryService).GetMethod(
            nameof(RecoveryService.StartAsync),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(start);
        Assert.Equal(typeof(BackgroundService), start!.DeclaringType);
    }

    [Fact]
    public async Task A_recovery_pass_that_throws_does_not_bring_the_host_down()
    {
        // The service is registered with restart-on-failure. An unhandled exception here is
        // not a crash, it is a crash loop.
        var service = Build(new ThrowingRecoveryPass());

        await service.StartAsync(CancellationToken.None);

        var execute = service.ExecuteTask;
        Assert.NotNull(execute);

        await service.StopAsync(CancellationToken.None);

        Assert.False(execute!.IsFaulted, execute.Exception?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task The_service_stays_up_after_recovery_so_it_can_still_be_armed()
    {
        var service = Build(new ImmediateRecoveryPass());

        await service.StartAsync(CancellationToken.None);
        await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(service.ExecuteTask!.IsCompleted, "the service exited as soon as recovery finished.");

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Stopping_the_service_finishes_promptly()
    {
        // The service control manager's stop timeout is the same 30 seconds. A stop that
        // hangs is a machine that will not shut down.
        var service = Build(new ImmediateRecoveryPass());

        await service.StartAsync(CancellationToken.None);
        await service.RecoveryCompleted.WaitAsync(TimeSpan.FromSeconds(10));

        var stopping = service.StopAsync(CancellationToken.None);

        Assert.Same(stopping, await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void The_binary_is_the_one_the_installer_registers()
    {
        // install/Install-GamerGod.ps1 looks for gmsvc.exe and skips service registration
        // when it is absent. Renaming the assembly silently un-installs the service.
        Assert.Equal("gmsvc", typeof(RecoveryService).Assembly.GetName().Name);
    }

    [Fact]
    public void The_service_refuses_to_be_built_without_a_recovery_pass()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new RecoveryService(null!, NullLogger<RecoveryService>.Instance));
    }

    private sealed class GatedRecoveryPass(Task gate) : IBootRecoveryPass
    {
        public async ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken)
        {
            await gate;
            return new RecoveryOutcome { HadOutstandingChanges = false };
        }
    }

    private sealed class ImmediateRecoveryPass : IBootRecoveryPass
    {
        public ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RecoveryOutcome { HadOutstandingChanges = false });
    }

    private sealed class ThrowingRecoveryPass : IBootRecoveryPass
    {
        public ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the state directory is gone");
    }
}
