using GamerGod.Core.Ledger;
using GamerGod.Core.Mutations;
using GamerGod.Core.Policy;
using Xunit;

namespace GamerGod.Core.Tests.Ledger;

/// <summary>
/// The journal is append-only, and a machine that stays on for months eventually makes that a
/// problem rather than a virtue.
///
/// <para>
/// Every armed session, every benchmark and every autotune arm appends. Nothing removed a line,
/// so the file grew for the life of the install — and it is the file <c>gmsvc</c> parses as
/// LocalSystem at every boot, before anyone signs in. The cost of leaving it lands on startup and
/// keeps getting worse.
/// </para>
///
/// <para>
/// The risk of the fix is the one that matters here: compaction that drops a line still
/// describing an applied change makes that change permanently unrecoverable — no other record of
/// it exists anywhere. Most of this file is about what has to survive.
/// </para>
/// </summary>
public sealed class JournalCompactionTests
{
    /// <summary>Comfortably past the threshold, reached the way a real journal reaches it.</summary>
    private const int ManySessions = 500;

    private static MutationPermit AmbientOnly() =>
        GameIntegrityPolicy.Evaluate(
            "session",
            new AntiCheatAssessment { Tier = AntiCheatTier.Unknown, Findings = [] });

    /// <summary>Arms and disarms repeatedly, exactly as a run of benchmarks leaves a journal.</summary>
    private static async Task<(InMemoryJournal Journal, FakeMachine Machine, FakeResolver Resolver)> Worn()
    {
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();

        for (var i = 0; i < ManySessions; i++)
        {
            var mutation = new FakeMutation(machine, $"key{i}", MutationTier.ProcessDemotion, "changed");
            resolver.Remember(mutation);

            var ledger = new MutationLedger(journal, resolver);
            await ledger.ApplyAsync($"s{i}", [mutation], AmbientOnly());
            await ledger.RevertAsync();
        }

        return (journal, machine, resolver);
    }

    [Fact]
    public async Task A_journal_of_finished_sessions_does_not_grow_for_ever()
    {
        var (journal, machine, resolver) = await Worn();
        var afterFirstRun = journal.Entries.Count;

        // Bounded, not merely smaller. Each cycle writes five lines, so five hundred of them
        // left 2500 before and the ceiling is what matters — doing it again must not double it.
        Assert.True(afterFirstRun < 2000, $"{afterFirstRun} lines after {ManySessions} sessions");

        for (var i = 0; i < ManySessions; i++)
        {
            var mutation = new FakeMutation(machine, $"again{i}", MutationTier.ProcessDemotion, "changed");
            resolver.Remember(mutation);

            var ledger = new MutationLedger(journal, resolver);
            await ledger.ApplyAsync($"u{i}", [mutation], AmbientOnly());
            await ledger.RevertAsync();
        }

        Assert.True(
            journal.Entries.Count < 2000,
            $"{journal.Entries.Count} lines after {ManySessions * 2} sessions "
            + $"(was {afterFirstRun} after {ManySessions})");

        Assert.False(await new MutationLedger(journal, resolver).HasOutstandingChangesAsync());
        Assert.Equal("<absent>", machine["key0"]);
    }

    [Fact]
    public async Task An_applied_session_survives_compaction_of_everything_around_it()
    {
        // The test this whole feature has to pass. A live session's capture is the only record
        // anywhere of what the machine looked like before GamerGod touched it.
        var (journal, machine, resolver) = await Worn();

        machine["live-key"] = "original";
        var live = new FakeMutation(machine, "live-key", MutationTier.ProcessDemotion, "changed");
        resolver.Remember(live);

        await new MutationLedger(journal, resolver).ApplyAsync("live", [live], AmbientOnly());

        // Another five hundred cycles of churn around it, every one of them compacting.
        var retained = new HashSet<string>(StringComparer.Ordinal) { "live" };

        for (var i = 0; i < ManySessions; i++)
        {
            var mutation = new FakeMutation(machine, $"other{i}", MutationTier.ProcessDemotion, "changed");
            resolver.Remember(mutation);

            var ledger = new MutationLedger(journal, resolver);
            await ledger.ApplyAsync($"t{i}", [mutation], AmbientOnly());
            await ledger.RevertExceptAsync(retained);
        }

        Assert.Contains(journal.Entries, e => e.Op == JournalOp.Capture && e.Key == "live-key");

        // And it is still revertible from a cold process, holding no objects, reading only what
        // survived — which is the situation boot recovery is always in.
        var cold = new MutationLedger(journal.ReopenCold(), resolver);
        Assert.Contains(await cold.OutstandingSessionsAsync(), s => s.SessionId == "live");

        Assert.True((await cold.RevertAsync()).IsClean);
        Assert.Equal("original", machine["live-key"]);
    }

    [Fact]
    public async Task A_retained_session_keeps_the_boot_stamp_that_dates_it()
    {
        // SessionBegin carries the boot timestamp, and the rule that a non-boot-persistent
        // capture from a previous boot is already undone reads it. Compacting that line away
        // would make a pre-reboot session look current — reversing the rule compaction itself
        // relies on to decide what to drop.
        var (journal, machine, resolver) = await Worn();

        var live = new FakeMutation(machine, "live-key", MutationTier.ProcessDemotion, "changed");
        resolver.Remember(live);
        await new MutationLedger(journal, resolver).ApplyAsync("live", [live], AmbientOnly());

        var retained = new HashSet<string>(StringComparer.Ordinal) { "live" };

        for (var i = 0; i < ManySessions; i++)
        {
            var mutation = new FakeMutation(machine, $"other{i}", MutationTier.ProcessDemotion, "changed");
            resolver.Remember(mutation);

            var ledger = new MutationLedger(journal, resolver);
            await ledger.ApplyAsync($"t{i}", [mutation], AmbientOnly());
            await ledger.RevertExceptAsync(retained);
        }

        var begin = Assert.Single(
            journal.Entries, e => e.Op == JournalOp.SessionBegin && e.SessionId == "live");

        Assert.NotEqual(0, begin.MachineBootedAtUtcTicks);
    }

    [Fact]
    public async Task A_short_journal_is_left_exactly_as_it_is()
    {
        // Compaction is for the pathological case. An ordinary machine's journal stays a
        // readable account of its recent sessions, which is what anybody opens it for.
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();
        var mutation = new FakeMutation(machine, "k", MutationTier.ProcessDemotion, "changed");
        resolver.Remember(mutation);

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("s1", [mutation], AmbientOnly());
        await ledger.RevertAsync();

        Assert.Contains(journal.Entries, e => e.Op == JournalOp.Capture);
        Assert.Contains(journal.Entries, e => e.Op == JournalOp.Reverted);
        Assert.Contains(journal.Entries, e => e.Op == JournalOp.SessionEnd);
    }

    [Fact]
    public async Task Every_reverted_session_gets_its_own_ending_not_just_the_first()
    {
        // A revert routinely spans more than one session: a crashed session and the one that
        // replaced it are both outstanding, and both come back in the same pass. This wrote a
        // single SessionEnd naming whichever sorted first, so every other session stayed
        // unterminated in the record for ever.
        var machine = new FakeMachine();
        var resolver = new FakeResolver(machine);
        var journal = new InMemoryJournal();

        var a = new FakeMutation(machine, "a", MutationTier.ProcessDemotion, "changed");
        var b = new FakeMutation(machine, "b", MutationTier.ProcessDemotion, "changed");
        resolver.Remember(a);
        resolver.Remember(b);

        var ledger = new MutationLedger(journal, resolver);
        await ledger.ApplyAsync("crashed", [a], AmbientOnly());
        await ledger.ApplyAsync("replacement", [b], AmbientOnly());

        Assert.True((await ledger.RevertAsync()).IsClean);

        var ended = journal.Entries
            .Where(e => e.Op == JournalOp.SessionEnd)
            .Select(e => e.SessionId)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["crashed", "replacement"], ended);
    }
}
