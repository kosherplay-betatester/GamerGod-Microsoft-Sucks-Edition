using GamerGod.Core.Engine;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// The receipt is the only thing most users will read, and it is the feature whose entire
/// job is being believable. Its wording is therefore load-bearing and gets its own tests.
/// </summary>
public sealed class ReceiptLanguageTests
{
    private static SessionReceipt Receipt(
        int confined = 0,
        int demoted = 0,
        int services = 0,
        bool dryRun = false,
        bool partitioned = false) => new()
        {
            SessionId = "s1",
            Applied = [],
            Refused = [],
            Failed = [],
            IntegritySummary = "Ambient only",
            ProcessesConfined = confined,
            ProcessesDemoted = demoted,
            ServicesStopped = services,
            Partitioned = partitioned,
            WasDryRun = dryRun,
        };

    [Fact]
    public void A_dry_run_is_written_in_the_conditional()
    {
        // Saying 113 processes "were moved" when nothing was touched is a small dishonesty,
        // and small dishonesties are what cost tools in this category their credibility.
        var text = Receipt(confined: 113, demoted: 113, dryRun: true, partitioned: true).Explain();

        Assert.Contains("would move", text, StringComparison.Ordinal);
        Assert.Contains("would be set", text, StringComparison.Ordinal);
        Assert.DoesNotContain("moved off", text, StringComparison.Ordinal);
        Assert.DoesNotContain("were set", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_session_is_written_in_the_past_tense()
    {
        var text = Receipt(confined: 113, demoted: 113, partitioned: true).Explain();

        Assert.Contains("moved off your game's cores", text, StringComparison.Ordinal);
        Assert.Contains("set to efficiency mode", text, StringComparison.Ordinal);
        Assert.DoesNotContain("would", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "1 process ")]
    [InlineData(2, "2 processes ")]
    [InlineData(113, "113 processes ")]
    public void Counts_are_pluralised_properly(int count, string expected)
    {
        // "process(es)" reads as a placeholder somebody forgot to finish.
        var text = Receipt(demoted: count).Explain();

        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain("(s)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("(es)", text, StringComparison.Ordinal);
    }

    [Fact]
    public void One_service_reads_as_one_service()
    {
        Assert.Contains("1 service stopped", Receipt(services: 1).Explain(), StringComparison.Ordinal);
        Assert.Contains("3 services stopped", Receipt(services: 3).Explain(), StringComparison.Ordinal);
    }

    [Fact]
    public void No_receipt_ever_contains_a_placeholder_or_a_performance_claim()
    {
        var receipts = new[]
        {
            Receipt(),
            Receipt(dryRun: true),
            Receipt(confined: 1, demoted: 1, services: 1, partitioned: true),
            Receipt(confined: 50, demoted: 50, services: 4, partitioned: true, dryRun: true),
        };

        foreach (var receipt in receipts)
        {
            var text = receipt.Explain();

            foreach (var slop in new[] { "(s)", "(es)", "TODO", "TBD", "N/A", "  " })
            {
                Assert.DoesNotContain(slop, text, StringComparison.Ordinal);
            }

            // Charter Article VII: the receipt says what was done, never what it gained.
            foreach (var claim in new[] { "faster", "fps", "%", "boost", "improve", "gain", "smoother", "optimi" })
            {
                Assert.DoesNotContain(claim, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.EndsWith(".", text.TrimEnd(), StringComparison.Ordinal);
            Assert.False(text.StartsWith(' '));
        }
    }

    [Fact]
    public void A_partition_with_nothing_to_move_does_not_claim_to_have_moved_nothing()
    {
        // "0 background processes moved off your game's cores" is technically true and
        // completely useless. Say the honest thing instead.
        var text = Receipt(partitioned: true, confined: 0).Explain();

        Assert.DoesNotContain("0 background", text, StringComparison.Ordinal);
        Assert.Contains("Nothing needed changing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failure_is_reported_with_correct_agreement()
    {
        var one = new SessionReceipt
        {
            SessionId = "s1",
            Applied = [],
            Refused = [],
            Failed = [("service:WSearch", "access denied")],
            IntegritySummary = "Ambient only",
        };

        var many = one with
        {
            Failed = [("service:WSearch", "denied"), ("service:SysMain", "denied")],
        };

        Assert.Contains("1 change could not be applied, and was recorded.", one.Explain(), StringComparison.Ordinal);
        Assert.Contains("2 changes could not be applied, and were recorded.", many.Explain(), StringComparison.Ordinal);
    }
}
