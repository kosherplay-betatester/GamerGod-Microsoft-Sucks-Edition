using System.Collections.Immutable;
using GamerGod.Core.Engine;
using GamerGod.Core.Hardware;
using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using GamerGod.Core.Tests.Hardware;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// The list a user reads on the dashboard: which processes were touched, and what happened to
/// each.
///
/// <para>
/// Built from the journal rather than from a receipt, because the desktop app usually is not the
/// process that applied anything — writing the journal needs administrator rights it does not
/// have, so it shells out and gets back an exit code. The journal is the one account both paths
/// share, and it is the same file the service recovers from. If the list a user reads and the
/// list a crash is recovered from could disagree, the interface would be describing a machine
/// nobody has.
/// </para>
/// </summary>
public sealed class SessionAccountTests
{
    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    private static CpuTopology Reference() =>
        PerformanceDomainClassifier.Classify(KnownCpus.Ryzen7950X3D());

    /// <summary>A real arm, through the real engine, against a simulated desktop.</summary>
    private static async Task<ImmutableArray<JournalEntry>> ArmedJournalAsync(
        bool alreadyEfficient = false)
    {
        var os = new FakeAmbientOperations()
            .AddProcess(1000, "chrome", 800, efficient: alreadyEfficient)
            .AddProcess(1001, "msedge", 400)
            .AddService("WSearch", running: true);

        var topology = Reference();
        var journal = new InMemoryJournal();
        var ledger = new MutationLedger(journal, new AmbientMutationResolver(os, topology));

        await ledger.ApplyAsync(
            "s1",
            [
                new EfficiencyModeMutation(os, [.. os.Processes]),
                new AffinityConfinementMutation(os, topology.AmbientMask, [.. os.Processes], topology),
                new ServiceSuspensionMutation(os, "WSearch"),
            ],
            AmbientOnly());

        return await journal.ReadAllAsync(default);
    }

    [Fact]
    public async Task Every_process_the_session_touched_is_listed_once()
    {
        var account = SessionAccount.ReadLatest(await ArmedJournalAsync(), ChangeDirection.Applied);

        Assert.Equal(2, account.Processes.Length);
        Assert.Equal(["chrome", "msedge"], account.Processes.Select(p => p.Name));

        // One line per process, not one per lever. The same process is usually both moved and
        // demoted, and a user wants to read about it once.
        var chrome = account.Processes.First(p => p.Name == "chrome");
        Assert.Equal(1000, chrome.ProcessId);
        Assert.Equal(2, chrome.Changes.Length);
    }

    [Fact]
    public async Task Applying_and_restoring_describe_opposite_things()
    {
        var entries = await ArmedJournalAsync();

        var applied = SessionAccount.ReadLatest(entries, ChangeDirection.Applied);
        var restored = SessionAccount.ReadLatest(entries, ChangeDirection.Restored);

        Assert.Contains("moved off your game's cores", applied.Processes[0].Describe());
        Assert.Contains("set to efficiency mode", applied.Processes[0].Describe());

        Assert.Contains("put back", restored.Processes[0].Describe());
        Assert.Contains("taken out of efficiency mode", restored.Processes[0].Describe());
    }

    [Fact]
    public async Task A_process_that_was_already_efficient_is_not_claimed_as_a_change()
    {
        // Machines that have been on for a while have plenty of these. Counting them would
        // inflate every list, and the whole point of this card is that it is an account rather
        // than a boast.
        var account = SessionAccount.ReadLatest(
            await ArmedJournalAsync(alreadyEfficient: true), ChangeDirection.Applied);

        var chrome = account.Processes.First(p => p.Name == "chrome");

        Assert.Contains("already in efficiency mode", chrome.Describe());
        Assert.DoesNotContain("set to efficiency mode", chrome.Describe());
    }

    [Fact]
    public async Task Machine_wide_changes_are_kept_apart_from_processes()
    {
        // A stopped service is a different kind of thing from a moved process, and somebody
        // scanning for their browser should not have to read past it.
        var account = SessionAccount.ReadLatest(await ArmedJournalAsync(), ChangeDirection.Applied);

        Assert.Contains(account.Machine, m => m.Contains("WSearch", StringComparison.Ordinal));
        Assert.DoesNotContain(account.Processes, p => p.Name.Contains("WSearch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restoring_says_a_service_was_started_again()
    {
        var account = SessionAccount.ReadLatest(await ArmedJournalAsync(), ChangeDirection.Restored);

        Assert.Contains(account.Machine, m => m.Contains("started again", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_journal_produces_an_empty_account_rather_than_throwing()
    {
        Assert.True(SessionAccount.ReadLatest([], ChangeDirection.Applied).IsEmpty);
        Assert.True(SessionAccount.Empty.IsEmpty);
    }

    [Fact]
    public async Task The_latest_session_is_the_one_described()
    {
        // Two sessions in one journal is the ordinary state of a machine that has been armed
        // twice. Showing the older one would describe changes that are no longer applied.
        var first = await ArmedJournalAsync();

        var os = new FakeAmbientOperations().AddProcess(2000, "discord", 300);
        var topology = Reference();
        var journal = new InMemoryJournal();

        foreach (var entry in first)
        {
            await journal.AppendAsync(entry, default);
        }

        await new MutationLedger(journal, new AmbientMutationResolver(os, topology)).ApplyAsync(
            "s2", [new EfficiencyModeMutation(os, [.. os.Processes])], AmbientOnly());

        var account = SessionAccount.ReadLatest(
            await journal.ReadAllAsync(default), ChangeDirection.Applied);

        Assert.Equal("s2", account.SessionId);
        Assert.Equal("discord", Assert.Single(account.Processes).Name);
    }

    [Fact]
    public async Task An_unreadable_capture_does_not_lose_the_rest_of_the_account()
    {
        // An older journal, or a mutation type since changed. The rest is still worth showing.
        var entries = await ArmedJournalAsync();

        var damaged = entries
            .Select(e => e.Op == JournalOp.Capture && e.Key == "ecoqos:background"
                ? e with { State = "{ this is not json" }
                : e)
            .ToImmutableArray();

        var account = SessionAccount.ReadLatest(damaged, ChangeDirection.Applied);

        Assert.Equal(2, account.Processes.Length);
        Assert.All(account.Processes, p => Assert.Contains("moved off", p.Describe()));
    }
}
