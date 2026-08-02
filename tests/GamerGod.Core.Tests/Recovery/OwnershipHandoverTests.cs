using GamerGod.Core.Engine;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Recovery;
using GamerGod.Core.Tests.Ledger;
using Xunit;

namespace GamerGod.Core.Tests.Recovery;

/// <summary>
/// Handing an already-armed session to the program that should end it.
///
/// <para>
/// Arming and knowing the owner are separated by real time, and not by an oversight. A game
/// started through a <c>steam://</c> URI is launched by Steam, so nothing GamerGod calls ever
/// returns its process id — and the machine has to be quiet <em>before</em> the game starts,
/// which is the whole point of arming first. The identity only exists once the game does.
/// </para>
///
/// <para>
/// This is what makes the watchdog able to do anything at all. Without an owner every session is
/// one nothing may end early, which is correct and also means the third escape path never fires.
/// </para>
/// </summary>
public sealed class OwnershipHandoverTests
{
    private static readonly ProcessIdentity Game = new(7314, StartedAtUtcTicks: 638_400_000_000_000_000L);

    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    private static (InMemoryJournal Journal, FakeMachine Machine, FakeResolver Resolver, MutationLedger Ledger)
        Armed(out FakeMutation mutation)
    {
        var machine = new FakeMachine();
        machine["service:WSearch"] = "Running";

        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();
        var ledger = new MutationLedger(journal, resolver);

        mutation = new FakeMutation(machine, "service:WSearch", MutationTier.Service, "Stopped");
        resolver.Remember(mutation);

        return (journal, machine, resolver, ledger);
    }

    [Fact]
    public async Task A_claimed_session_reports_the_new_owner()
    {
        var (journal, _, resolver, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());

        // Armed with nobody to watch — the ordinary case for a tool that applies and exits.
        var before = Assert.Single(await ledger.OutstandingSessionsAsync());
        Assert.Null(before.Owner);

        Assert.True(await ledger.ClaimOwnershipAsync("armed", Game));

        // And now the game owns it, read back through the same path boot recovery uses.
        var after = Assert.Single(await new MutationLedger(journal, resolver).OutstandingSessionsAsync());
        Assert.Equal(Game, after.Owner);
    }

    [Fact]
    public async Task Claiming_appends_and_never_edits_what_is_already_written()
    {
        // Append-only is the property the whole crash guarantee rests on. Ownership is read from
        // the LAST SessionBegin a session has, so a second one supersedes the first by a rule
        // that was already there — nothing about how an owner is interpreted changes, and a
        // journal written by an older build still reads exactly as it did.
        var (journal, _, _, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());

        var before = journal.Entries.Count;
        var firstBegin = journal.Entries.First(e => e.Op == JournalOp.SessionBegin);

        await ledger.ClaimOwnershipAsync("armed", Game);

        Assert.Equal(before + 1, journal.Entries.Count);

        // The original line is untouched, ownerless, exactly as it was flushed.
        Assert.Same(firstBegin, journal.Entries.First(e => e.Op == JournalOp.SessionBegin));
        Assert.Equal(0, firstBegin.OwnerProcessId);
    }

    [Fact]
    public async Task A_session_that_is_no_longer_applied_cannot_be_claimed()
    {
        // Reviving a finished session by naming it would hand the watchdog a session with no
        // changes left to undo, and the next thing it did would be to revert somebody else's.
        var (_, _, _, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());
        await ledger.RevertAsync();

        Assert.False(await ledger.ClaimOwnershipAsync("armed", Game));
        Assert.Empty(await ledger.OutstandingSessionsAsync());
    }

    [Fact]
    public async Task A_session_that_never_existed_cannot_be_claimed()
    {
        var (_, _, _, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());

        Assert.False(await ledger.ClaimOwnershipAsync("never-armed", Game));
    }

    [Fact]
    public async Task The_claimed_session_is_reverted_once_the_owner_is_gone()
    {
        // End to end through boot recovery, which is the same pass the watchdog runs on a timer.
        var (journal, machine, resolver, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());
        await ledger.ClaimOwnershipAsync("armed", Game);

        Assert.Equal("Stopped", machine["service:WSearch"]);

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, resolver), new NobodyIsRunning(), default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.True(outcome.IsClean, outcome.Explain());
        Assert.Contains("service:WSearch", outcome.Report!.Reverted);
        Assert.Equal("Running", machine["service:WSearch"]);
    }

    [Fact]
    public async Task The_claimed_session_is_left_alone_while_the_owner_lives()
    {
        // The asymmetry that matters. Firing while the game is running ends somebody's session
        // mid-match; failing to fire only delays the restore until they ask for it.
        var (journal, machine, resolver, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());
        await ledger.ClaimOwnershipAsync("armed", Game);

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, resolver), new StillRunning(Game), default);

        Assert.True(outcome.HadOutstandingChanges);
        Assert.Null(outcome.Report);
        Assert.Contains("armed", outcome.LeftToTheirOwner);
        Assert.Equal("Stopped", machine["service:WSearch"]);
    }

    [Fact]
    public async Task An_owner_whose_pid_was_recycled_does_not_hold_the_session_open()
    {
        // Same id, different start time: the game is gone and something else inherited its
        // number. Without the start time a recycled id would keep a machine partitioned for ever.
        var (journal, machine, resolver, ledger) = Armed(out var mutation);
        await ledger.ApplyAsync("armed", [mutation], AmbientOnly());
        await ledger.ClaimOwnershipAsync("armed", Game);

        var impostor = Game with { StartedAtUtcTicks = Game.StartedAtUtcTicks + 10_000_000L };

        var outcome = await BootRecovery.RunAsync(
            new MutationLedger(journal, resolver), new StillRunning(impostor), default);

        Assert.Contains("service:WSearch", outcome.Report!.Reverted);
        Assert.Equal("Running", machine["service:WSearch"]);
    }

    private sealed class NobodyIsRunning : IProcessLiveness
    {
        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(null);
    }

    private sealed class StillRunning(ProcessIdentity identity) : IProcessLiveness
    {
        public ValueTask<ProcessIdentity?> IdentifyAsync(int processId, CancellationToken cancellationToken) =>
            ValueTask.FromResult<ProcessIdentity?>(
                processId == identity.ProcessId ? identity : null);
    }
}
