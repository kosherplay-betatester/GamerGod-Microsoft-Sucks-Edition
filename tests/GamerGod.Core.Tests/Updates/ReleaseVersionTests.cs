using GamerGod.Core.Updates;
using Xunit;

namespace GamerGod.Core.Tests.Updates;

public sealed class ReleaseVersionTests
{
    private static ReleaseVersion Parse(string value)
    {
        Assert.True(ReleaseVersion.TryParse(value, out var version), value);
        return version;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("2", 2, 0, 0)]
    [InlineData("1.1.0+67603ed", 1, 1, 0)]
    [InlineData("1.1.0.0", 1, 1, 0)]
    public void The_shapes_both_sides_actually_produce_all_parse(
        string value, int major, int minor, int patch)
    {
        var version = Parse(value);

        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Fact]
    public void A_tag_and_the_assembly_version_of_the_same_release_compare_equal()
    {
        // The whole reason this type exists. GitHub says "v1.1.0"; the running build says
        // "1.1.0+<commit>". Compared as strings these differ, and the app would offer an
        // update to the version already installed on every launch.
        Assert.Equal(Parse("v1.1.0"), Parse("1.1.0+67603ed2c1"));
    }

    [Theory]
    [InlineData("1.1.1", "1.1.0")]
    [InlineData("1.2.0", "1.1.9")]
    [InlineData("2.0.0", "1.99.99")]
    [InlineData("1.10.0", "1.9.0")]
    public void Ordering_is_numeric_rather_than_lexical(string newer, string older)
    {
        // "1.10.0" sorts before "1.9.0" as text, which would hide a release.
        Assert.True(Parse(newer) > Parse(older));
        Assert.True(Parse(older) < Parse(newer));
    }

    [Fact]
    public void A_pre_release_ranks_below_the_release_it_precedes()
    {
        Assert.True(Parse("1.2.0") > Parse("1.2.0-beta.1"));
        Assert.True(Parse("1.2.0-beta.1") > Parse("1.1.9"));
        Assert.True(Parse("1.2.0-beta.1").IsPreRelease);
        Assert.False(Parse("1.2.0").IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("nightly")]
    [InlineData("v")]
    [InlineData("1.x.0")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.3.4.5")]
    public void Anything_unparseable_is_refused_rather_than_guessed(string? value) =>
        Assert.False(ReleaseVersion.TryParse(value, out _));

    [Fact]
    public void Round_tripping_keeps_the_version_readable()
    {
        Assert.Equal("1.2.3", Parse("v1.2.3").ToString());
        Assert.Equal("1.2.0-beta.1", Parse("v1.2.0-beta.1").ToString());
        Assert.Equal("1.1.0", Parse("1.1.0+abcdef").ToString());
    }
}
