using GamerGod.Core.Recovery;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GamerGod.Service;

/// <summary>
/// Charter Article X's third escape path: the machine comes back when the program holding a
/// session dies, rather than at the next reboot.
///
/// <para>
/// <b>There is no IPC channel, and that is the design rather than a shortcut.</b> The thing a
/// session would have to hand over is the identity of the process whose death ends it, and the
/// journal has recorded exactly that on every <c>SessionBegin</c> since ownership was added —
/// durably, before the first change is applied, in a file only administrators can write. A named
/// pipe into a LocalSystem service would be a second channel carrying the same fact, with an
/// authentication problem attached, and it would be the one that goes missing precisely when it
/// matters: a process that is being killed does not get to send a message.
/// </para>
///
/// <para>
/// So this polls. Every tick is the identical orphan-recovery pass the service already runs at
/// boot — read the journals, ask Windows which recorded owners are still alive, revert only the
/// ones that are not. Being the same code is the point: the rule about who counts as live was
/// hard to get right, it is now covered by tests and verified on real hardware, and a watchdog
/// with a second opinion about it would eventually disagree and end somebody's session.
/// </para>
///
/// <para>
/// It is asymmetric on purpose. Firing while the owner is alive ends a session mid-match for no
/// reason; failing to fire strands the machine until the next reboot. Only the second is a broken
/// promise, but the first is the one that loses trust — which is why every uncertain answer in
/// the pass below means "leave it alone".
/// </para>
/// </summary>
public sealed class WatchdogService : BackgroundService
{
    /// <summary>
    /// How often the owners are checked.
    ///
    /// <para>
    /// Five seconds is the trade between "the moment it happens" and being the contention this
    /// product exists to remove. A tick is a journal read and one process-handle open per
    /// outstanding session — microseconds of work — but it also takes the journal's exclusive
    /// lock, so it is not free to make this a spin loop.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IBootRecoveryPass _pass;
    private readonly RecoveryService _boot;
    private readonly TimeSpan _interval;
    private readonly ILogger<WatchdogService> _logger;

    public WatchdogService(
        IBootRecoveryPass pass,
        RecoveryService boot,
        ILogger<WatchdogService> logger,
        TimeSpan? interval = null)
    {
        _pass = pass ?? throw new ArgumentNullException(nameof(pass));
        _boot = boot ?? throw new ArgumentNullException(nameof(boot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var chosen = interval ?? DefaultInterval;

        // A zero interval turns the safety net into a spin loop on a core somebody wanted for
        // their game, which would make the watchdog the problem it exists to solve.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chosen, TimeSpan.Zero);
        _interval = chosen;
    }

    /// <summary>
    /// Number of ticks that have completed. Exposed so a test can wait for the watchdog to have
    /// actually looked, rather than sleeping and hoping.
    /// </summary>
    public int Ticks { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Boot recovery first, and waited for rather than raced. Both take the same journal
        // lock, so overlapping them would be safe but would run a full pass twice at every
        // start and write two accounts of one machine into the event log.
        try
        {
            await _boot.RecoveryCompleted.WaitAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation(
            "GamerGod watchdog: watching every {Seconds:0.#} seconds. A session whose program "
            + "stops will be undone without waiting for a restart.",
            _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TickAsync(stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "GamerGod watchdog: stopped. Any session still applied is undone by 'gamergod off' "
            + "or by restarting.");
    }

    /// <summary>
    /// One check. Never throws: this runs on a service registered with restart-on-failure, so
    /// an exception escaping is not a crash but a crash loop, on LocalSystem, with nobody
    /// watching a console.
    /// </summary>
    internal async ValueTask TickAsync(CancellationToken cancellationToken)
    {
        RecoveryOutcome outcome;

        try
        {
            outcome = await _pass.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            // Logged once per occurrence and then tried again on the next tick. A journal that
            // cannot be read right now is usually one another process is holding.
            _logger.LogWarning("GamerGod watchdog: could not check this time: {Error}", ex.Message);
            Ticks++;
            return;
        }

        Ticks++;

        // Silence is the normal case, and it has to stay silent. This runs every few seconds for
        // as long as the machine is on; a line per tick would bury the one line that matters
        // under thousands that do not, in the only record of what a LocalSystem service did.
        if (outcome.Report is not { } report)
        {
            return;
        }

        if (report.IsClean)
        {
            _logger.LogInformation("GamerGod watchdog: {Outcome}", outcome.Explain());
        }
        else
        {
            _logger.LogError("GamerGod watchdog: {Outcome}", outcome.Explain());
        }
    }
}
