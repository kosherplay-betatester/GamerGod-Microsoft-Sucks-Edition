using GamerGod.Core.Recovery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GamerGod.Service;

/// <summary>
/// The work the service does once, when it starts. An interface so the host can be tested
/// without a journal, a machine, or administrator rights.
/// </summary>
public interface IBootRecoveryPass
{
    ValueTask<RecoveryOutcome> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The GamerGod background service.
///
/// <para>
/// It exists for the case where nothing else can help: the machine bugchecked, or lost
/// power, or the program holding a session was killed. The hotkey needs an agent and the
/// controller combo needs an agent; this starts before anybody signs in and needs nothing
/// but a file on disk.
/// </para>
///
/// <para>
/// It applies nothing of its own. Today it recovers what a previous session left behind and
/// then sits idle waiting to be armed, which nothing does yet — the channel a session uses to
/// hand over its owner is a separate piece of work. That is stated here rather than implied,
/// because a service whose description promises more than it does is worse than no service.
/// </para>
/// </summary>
public sealed class RecoveryService : BackgroundService
{
    private readonly IBootRecoveryPass _recovery;
    private readonly ILogger<RecoveryService> _logger;

    private readonly TaskCompletionSource<RecoveryOutcome> _recovered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecoveryService(IBootRecoveryPass recovery, ILogger<RecoveryService> logger)
    {
        _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Completes when the boot-recovery pass has finished, whatever it found. Exposed so the
    /// host can be driven deterministically in a test rather than by sleeping.
    /// </summary>
    public Task<RecoveryOutcome> RecoveryCompleted => _recovered.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Recovery runs here and not in StartAsync, and that is a constraint rather than a
        // preference. The service control manager allows 30 seconds to report started;
        // putting a machine back can take longer than that, and a service which misses the
        // deadline is killed - part of the way through the revert, which is the worst place
        // to be stopped. Yielding first guarantees the start path has returned to Windows
        // before any of this begins.
        await Task.Yield();

        RecoveryOutcome outcome;

        try
        {
            outcome = await _recovery.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The service is registered with restart-on-failure. An exception escaping here
            // is not a crash, it is a crash loop, on a LocalSystem service, with nobody
            // watching a console.
            outcome = new RecoveryOutcome { HadOutstandingChanges = false, Error = ex.Message };
        }

        if (outcome.IsClean)
        {
            _logger.LogInformation("GamerGod boot recovery: {Outcome}", outcome.Explain());
        }
        else
        {
            _logger.LogError("GamerGod boot recovery: {Outcome}", outcome.Explain());
        }

        _recovered.TrySetResult(outcome);

        // Stay up and idle. A service that exits on its own is reported to Windows as having
        // failed and restarted forever, and there is nothing here worth restarting for.
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The ordinary way this ends.
        }
    }
}
