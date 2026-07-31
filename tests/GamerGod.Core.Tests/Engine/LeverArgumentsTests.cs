using GamerGod.Core.Engine;
using Xunit;

namespace GamerGod.Core.Tests.Engine;

/// <summary>
/// The levers the desktop application sends to the privileged half.
///
/// <para>
/// Written because they were not sent at all. The app cannot write the revert journal — that
/// directory is administrators-only on purpose — so it brokers arming through
/// <c>gamergod on</c>, and it launched that with the verb and nothing else. Every setting the
/// user had chosen was read, listed back to them in a confirmation dialog, and then discarded
/// as the command applied its own defaults.
/// </para>
///
/// <para>
/// The round trip is the test that matters. Anything that survives it cannot be silently
/// dropped between the two processes.
/// </para>
/// </summary>
public sealed class LeverArgumentsTests
{
    private static AmbientOptions Levers(bool confine, bool efficiency, bool power, bool services) => new()
    {
        ConfineToAmbientDomain = confine,
        DemoteToEfficiencyMode = efficiency,
        ManagePowerScheme = power,
        Services = services ? ["WSearch", "SysMain", "DiagTrack", "wuauserv", "BITS"] : [],
    };

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public void Every_combination_survives_the_round_trip(
        bool confine, bool efficiency, bool power, bool services)
    {
        var chosen = Levers(confine, efficiency, power, services);

        var parsed = LeverArguments.Parse(LeverArguments.Render(chosen));

        Assert.Equal(chosen.ConfineToAmbientDomain, parsed.ConfineToAmbientDomain);
        Assert.Equal(chosen.DemoteToEfficiencyMode, parsed.DemoteToEfficiencyMode);
        Assert.Equal(chosen.ManagePowerScheme, parsed.ManagePowerScheme);
        Assert.Equal(
            chosen.Services.IsDefaultOrEmpty,
            parsed.Services.IsDefaultOrEmpty);
    }

    [Fact]
    public void Turning_a_lever_off_is_stated_rather_than_left_to_absence()
    {
        // The whole defect was a default standing in for a choice. A caller that means "off"
        // has to say so, in both directions.
        var rendered = LeverArguments.Render(Levers(false, false, false, false));

        Assert.Contains("--no-confine", rendered);
        Assert.Contains("--no-efficiency", rendered);
        Assert.Contains("--no-power", rendered);
        Assert.Contains("--no-services", rendered);
    }

    [Fact]
    public void A_bare_command_line_still_gets_the_shipped_defaults()
    {
        // Somebody typing 'gamergod on' expects what it always did. Only an explicit flag
        // changes anything.
        var defaults = new AmbientOptions();
        var parsed = LeverArguments.Parse([]);

        Assert.Equal(defaults.ConfineToAmbientDomain, parsed.ConfineToAmbientDomain);
        Assert.Equal(defaults.DemoteToEfficiencyMode, parsed.DemoteToEfficiencyMode);
        Assert.Equal(defaults.ManagePowerScheme, parsed.ManagePowerScheme);
        Assert.Equal(defaults.Services.IsDefaultOrEmpty, parsed.Services.IsDefaultOrEmpty);
    }

    [Fact]
    public void An_unrelated_argument_changes_nothing()
    {
        var parsed = LeverArguments.Parse(["on", "--dry-run", "--json"]);
        var defaults = new AmbientOptions();

        Assert.Equal(defaults.ConfineToAmbientDomain, parsed.ConfineToAmbientDomain);
        Assert.Equal(defaults.ManagePowerScheme, parsed.ManagePowerScheme);
    }

    [Fact]
    public void The_negative_wins_when_a_caller_contradicts_itself()
    {
        // Should not happen — Render never emits both — but a hand-typed command line can.
        // Refusing to act is the safer reading of an ambiguous instruction.
        Assert.False(LeverArguments.Parse(["--no-confine", "--confine"]).ConfineToAmbientDomain);
    }

    [Fact]
    public void Flags_are_recognised_whatever_case_they_are_typed_in() =>
        Assert.True(LeverArguments.Parse(["--CONFINE"]).ConfineToAmbientDomain);

    [Fact]
    public void Rendering_states_all_four_levers_every_time()
    {
        // A lever that is sometimes omitted is a lever that can be silently defaulted, which
        // is the bug this file exists for.
        Assert.Equal(4, LeverArguments.Render(Levers(true, false, true, false)).Length);
        Assert.Equal(4, LeverArguments.Render(Levers(false, false, false, false)).Length);
    }
}
