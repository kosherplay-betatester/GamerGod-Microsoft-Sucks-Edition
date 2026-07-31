using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using Xunit;

namespace GamerGod.Core.Tests.Ledger;

/// <summary>
/// Regression for a defect reported from real use: after restarting the machine, GamerGod
/// still said Game Mode was on.
///
/// <para>
/// The machine itself was correct — every affected process showed an unrestricted affinity
/// mask, because efficiency mode and processor affinity die with the process that carried
/// them and a restart discards all of it without any code running. The journal, however, is a
/// file, and files survive restarts. The ledger read captures with no matching reverts and
/// concluded the changes were still applied.
/// </para>
///
/// <para>
/// A guarantee that holds but reports itself as broken is barely better than one that does
/// not hold, because a user has no way to tell the difference. Worse, "on" blocks a fresh
/// session and a revert would target process ids that now belong to something else entirely.
/// </para>
/// </summary>
public sealed class RebootRecoveryTests
{
    private static MutationPermit Permit() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>A session stamped as having begun before the machine's current boot.</summary>
    private static JournalEntry BeforeReboot(string key, bool bootPersistent = false) => new()
    {
        Op = JournalOp.Capture,
        SessionId = "old",
        Key = key,
        MutationType = "Stub",
        Tier = MutationTier.ProcessDemotion,
        IsBootPersistent = bootPersistent,
        State = """{"Previous":"original"}""",
    };

    private static JournalEntry SessionFromPreviousBoot() => new()
    {
        Op = JournalOp.SessionBegin,
        SessionId = "old",

        // A boot far enough in the past that the machine has certainly restarted since.
        MachineBootedAtUtcTicks = DateTimeOffset.UtcNow.AddDays(-3).UtcTicks,
    };

    [Fact]
    public async Task After_a_restart_the_ledger_reports_nothing_outstanding()
    {
        var journal = new InMemoryJournal();
        await journal.AppendAsync(SessionFromPreviousBoot(), default);
        await journal.AppendAsync(BeforeReboot("ecoqos:background"), default);
        await journal.AppendAsync(BeforeReboot("affinity:ambient-domain"), default);

        var machine = new FakeMachine();
        var ledger = new MutationLedger(journal, new FakeResolver(machine));

        // Windows already undid all of it when those processes ended.
        Assert.False(await ledger.HasOutstandingChangesAsync());
    }

    [Fact]
    public async Task After_a_restart_revert_does_not_touch_recycled_process_ids()
    {
        // The dangerous half. Process ids are reused, so "restoring" a captured affinity after
        // a reboot would apply it to whatever process now holds that id.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(SessionFromPreviousBoot(), default);
        await journal.AppendAsync(BeforeReboot("affinity:ambient-domain"), default);

        var machine = new FakeMachine();
        machine["affinity:ambient-domain"] = "untouched";

        var report = await new MutationLedger(journal, new FakeResolver(machine)).RevertAsync();

        Assert.True(report.IsClean);
        Assert.Empty(report.Reverted);
        Assert.Equal("untouched", machine["affinity:ambient-domain"]);
    }

    [Fact]
    public async Task A_change_that_does_survive_a_reboot_is_still_reverted()
    {
        // The distinction that makes this safe. A registry value or a device policy outlives a
        // restart, so a reboot does not undo it and the ledger must still put it back.
        var journal = new InMemoryJournal();
        await journal.AppendAsync(SessionFromPreviousBoot(), default);
        await journal.AppendAsync(BeforeReboot("registry:irq-policy", bootPersistent: true), default);

        var machine = new FakeMachine();
        machine["registry:irq-policy"] = "changed";

        var ledger = new MutationLedger(journal, new FakeResolver(machine));

        Assert.True(await ledger.HasOutstandingChangesAsync());

        var report = await ledger.RevertAsync();

        Assert.True(report.IsClean);
        Assert.Contains("registry:irq-policy", report.Reverted);
        Assert.Equal("original", machine["registry:irq-policy"]);
    }

    [Fact]
    public async Task A_session_on_the_current_boot_is_untouched_by_any_of_this()
    {
        // The ordinary case must not regress: a live session's changes are still outstanding
        // and still revertible.
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";
        var before = machine.Snapshot();

        var journal = new InMemoryJournal();
        var resolver = new FakeResolver(machine);
        var mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(mutation);

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("live", [mutation], Permit());

        Assert.True(await ledger.HasOutstandingChangesAsync());

        var report = await ledger.RevertAsync();

        Assert.True(report.IsClean);
        Assert.True(machine.Matches(before));
    }

    [Fact]
    public void An_unstamped_session_is_never_assumed_to_predate_the_boot()
    {
        // Journals written before this field existed carry zero. Treating that as "restarted"
        // would silently discard a live session's record, so the conservative reading wins.
        Assert.False(MachineBoot.RestartedSince(0, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Uptime_drift_from_sleep_is_not_mistaken_for_a_restart()
    {
        // Sleep and hibernate perturb the uptime counter, so two readings within one boot can
        // differ by seconds. Only a difference beyond the tolerance counts.
        var now = DateTimeOffset.UtcNow;
        var justNow = now.AddSeconds(-20).UtcTicks;

        Assert.False(MachineBoot.RestartedSince(justNow, now));
        Assert.True(MachineBoot.RestartedSince(now.AddHours(-5).UtcTicks, now));
    }

    // ------------------------------------------------------------------
    // The clock is not a fixed point, and the boot instant is derived from it.
    // ------------------------------------------------------------------

    [Fact]
    public void Correcting_the_clock_forwards_does_not_look_like_a_restart()
    {
        // The failure this replaces. MachineBootedAtUtcTicks is UtcNow minus the uptime counter,
        // so stepping the clock forward moves the derived boot instant with it while the machine
        // has plainly not rebooted — and the ledger then treats a live session's captures as
        // already undone and drops them. The machine stays confined with nothing left that knows
        // how to put it back.
        //
        // Hours, not seconds: this is an NTP correction after a machine has been off for a
        // while, or a dual boot where Linux writes local time to the RTC and Windows reads it as
        // UTC. Squarely in this project's audience.
        var uptime = TimeSpan.FromHours(3);
        var armedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        var recordedBoot = (armedAt - uptime).UtcTicks;

        // Six hours are added to the wall clock. The uptime counter does not move.
        var derivedBootNow = armedAt.AddHours(6) - uptime;

        Assert.False(MachineBoot.RestartedSince(
            recordedBoot,
            (long)uptime.TotalMilliseconds,
            derivedBootNow,
            (long)(uptime + TimeSpan.FromMinutes(5)).TotalMilliseconds));

        // And the clock-only reading, which is what this used to do, gets it wrong — so the
        // test above is measuring the fix rather than restating the old behaviour.
        Assert.True(MachineBoot.RestartedSince(recordedBoot, derivedBootNow));
    }

    [Fact]
    public void A_restart_is_still_recognised_from_the_uptime_alone()
    {
        // Uptime running backwards is proof, and needs no clock at all. This is the ordinary
        // case: boot recovery runs as the service starts, seconds into a boot, against a session
        // that had been up for hours.
        var wasUp = (long)TimeSpan.FromHours(21).TotalMilliseconds;
        var nowUp = (long)TimeSpan.FromSeconds(8).TotalMilliseconds;

        Assert.True(MachineBoot.RestartedSince(
            DateTimeOffset.UtcNow.AddHours(-21).UtcTicks, wasUp, DateTimeOffset.UtcNow, nowUp));
    }

    [Fact]
    public void Two_readings_moments_apart_are_never_a_restart()
    {
        // A session begun microseconds ago must never read as having outlived a reboot, however
        // the two uptime readings happen to land.
        var uptime = (long)TimeSpan.FromHours(3).TotalMilliseconds;

        Assert.False(MachineBoot.RestartedSince(
            DateTimeOffset.UtcNow.AddHours(-3).UtcTicks, uptime, DateTimeOffset.UtcNow, uptime - 40));
    }

    [Fact]
    public async Task A_session_from_a_previous_boot_is_recognised_by_its_uptime()
    {
        // End to end through the ledger, with the stamp a session actually writes now.
        var journal = new InMemoryJournal();

        await journal.AppendAsync(
            new JournalEntry
            {
                Op = JournalOp.SessionBegin,
                SessionId = "old",
                MachineBootedAtUtcTicks = DateTimeOffset.UtcNow.AddDays(-3).UtcTicks,

                // Up for a fortnight when it was armed. Nothing running now can beat that.
                MachineUptimeMs = (long)TimeSpan.FromDays(14).TotalMilliseconds,
            },
            default);

        await journal.AppendAsync(BeforeReboot("affinity:ambient-domain"), default);

        var ledger = new MutationLedger(journal, new FakeResolver(new FakeMachine()));

        Assert.False(await ledger.HasOutstandingChangesAsync());
    }

    [Fact]
    public void The_reported_boot_time_is_plausible()
    {
        var bootedAt = MachineBoot.At();

        Assert.True(bootedAt <= DateTimeOffset.UtcNow, "the machine cannot have booted in the future");
        Assert.True(
            bootedAt > DateTimeOffset.UtcNow.AddYears(-1),
            "an uptime over a year means the counter was misread");
    }
}
