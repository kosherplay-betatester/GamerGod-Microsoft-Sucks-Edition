using GamerGod.Core.Engine;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// The sentences that cross a privilege boundary.
///
/// <para>
/// The desktop window has no rights of its own; the helper on the other end of this has
/// administrator rights the user consented to once. What can be said across that gap is
/// therefore a security boundary, not a convenience, and the allow-list is the whole of it.
/// </para>
/// </summary>
public sealed class BrokerProtocolTests
{
    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("status")]
    [InlineData("ping")]
    [InlineData("quit")]
    public void The_allowed_verbs_are_allowed(string verb) =>
        Assert.True(BrokerProtocol.IsAllowedVerb(verb));

    [Theory]
    [InlineData("bench")]          // real, and must not be reachable from an unprivileged window
    [InlineData("autotune")]
    [InlineData("broker")]         // starting a second helper from inside the first
    [InlineData("claim")]
    [InlineData("scan")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ON")]             // the server lower-cases before asking; this is belt and braces
    [InlineData("on;off")]
    [InlineData("on && shutdown")]
    public void Everything_else_is_refused(string? verb) =>
        Assert.False(BrokerProtocol.IsAllowedVerb(verb));

    [Fact]
    public void A_verb_is_separated_from_its_arguments()
    {
        var (verb, arguments) = BrokerProtocol.Parse("on --confine --no-power");

        Assert.Equal("on", verb);
        Assert.Equal(["--confine", "--no-power"], arguments);
    }

    [Fact]
    public void A_verb_arrives_lower_cased_however_it_was_sent()
    {
        // The allow-list compares exactly, so the normalising has to happen before it — not
        // inside it, where a future caller could skip it.
        Assert.Equal("off", BrokerProtocol.Parse("OFF").Verb);
        Assert.True(BrokerProtocol.IsAllowedVerb(BrokerProtocol.Parse("OFF").Verb));
    }

    [Fact]
    public void An_empty_line_parses_to_nothing_rather_than_throwing()
    {
        var (verb, arguments) = BrokerProtocol.Parse("   ");

        Assert.Equal(string.Empty, verb);
        Assert.Empty(arguments);
        Assert.False(BrokerProtocol.IsAllowedVerb(verb));
    }

    [Fact]
    public void An_exit_code_survives_the_round_trip()
    {
        Assert.Equal(0, BrokerProtocol.ReadExitCode(BrokerProtocol.Reply(0)));
        Assert.Equal(4, BrokerProtocol.ReadExitCode(BrokerProtocol.Reply(4)));
        Assert.Equal(10, BrokerProtocol.ReadExitCode(BrokerProtocol.Reply(10)));
    }

    [Theory]
    [InlineData("error something went wrong")]
    [InlineData("ok")]
    [InlineData("ok notanumber")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_that_is_not_an_ok_line_is_not_a_zero(string? reply)
    {
        // The failure mode this rules out: a garbled or hostile reply reading as exit code 0,
        // which the window would show as "Game Mode is on" over a machine nothing touched.
        Assert.Null(BrokerProtocol.ReadExitCode(reply));
    }

    [Fact]
    public void Lever_arguments_survive_the_round_trip_through_the_wire_format()
    {
        // The window renders levers, they cross as text, and the helper re-parses them. A
        // disagreement anywhere along that path silently applies the wrong set — which is the
        // exact bug that once made unticking a lever do nothing.
        var options = new AmbientOptions
        {
            ConfineToAmbientDomain = false,
            DemoteToEfficiencyMode = true,
            ManagePowerScheme = true,
            Services = [],
        };

        var line = "on " + string.Join(' ', LeverArguments.Render(options));
        var (verb, arguments) = BrokerProtocol.Parse(line);
        var parsed = LeverArguments.Parse(arguments);

        Assert.Equal("on", verb);
        Assert.False(parsed.ConfineToAmbientDomain);
        Assert.True(parsed.DemoteToEfficiencyMode);
        Assert.True(parsed.ManagePowerScheme);
    }
}
