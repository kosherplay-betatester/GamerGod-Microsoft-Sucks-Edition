using GamerGod.Core.Safety;
using Xunit;

namespace GamerGod.Core.Tests.Safety;

/// <summary>
/// Streaming software must survive Game Mode untouched.
///
/// <para>
/// Written after the question "does this work for streamers" was asked directly and the answer
/// was no. The list already protected Sunshine, GameStream, Steam Remote Play and ALVR, which
/// look like the same thing and are not: those carry a picture to a screen in the next room,
/// where a dropped frame costs one frame. Broadcast software carries a picture to an audience,
/// where a dropped frame is gone from the recording of a live event.
/// </para>
/// </summary>
public sealed class BroadcastSafetyTests
{
    [Theory]
    [InlineData("obs64.exe")]
    [InlineData("obs32.exe")]
    [InlineData("obs-ffmpeg-mux.exe")]
    [InlineData("Streamlabs OBS.exe")]
    [InlineData("Streamlabs Desktop.exe")]
    [InlineData("XSplit.Core.exe")]
    [InlineData("Twitch Studio.exe")]
    [InlineData("vMix64.exe")]
    [InlineData("NVIDIA Share.exe")]
    [InlineData("amdow.exe")]
    public void A_live_encoder_is_never_touched(string process)
    {
        // IsProtected gates all three levers — suspension, efficiency-mode demotion, and domain
        // confinement — so one assertion covers everything GamerGod could do to it.
        Assert.True(
            ProcessSafetyPolicy.IsProtected(process),
            $"{process} is a live encoder and GamerGod would have moved or demoted it");
    }

    [Fact]
    public void Broadcast_is_its_own_category_because_demotion_is_not_a_safe_fallback()
    {
        // The distinction this category exists to record. For every other entry, demotion is
        // the gentler option when suspension is refused. For an encoder it is not: missing a
        // deadline at 60 frames a second is the same failure, arrived at more politely.
        var obs = ProcessSafetyPolicy.Explain("obs64.exe");

        Assert.NotNull(obs);
        Assert.Equal(ProcessRisk.Broadcast, obs!.Risk);
    }

    [Fact]
    public void The_muxer_is_protected_and_not_only_the_encoder()
    {
        // OBS writes its outgoing stream through a separate process. Protecting the encoder
        // and demoting the thing that writes its output would produce a corrupted recording
        // from a session that looked fine.
        Assert.True(ProcessSafetyPolicy.IsProtected("obs-ffmpeg-mux.exe"));
    }

    [Fact]
    public void Every_broadcast_entry_explains_the_consequence_rather_than_naming_the_product()
    {
        // A list of names nobody can audit is how the hwinfo entry survived: it looked like
        // protection and provided none. An explanation forces the author to have a reason.
        foreach (var entry in ProcessSafetyPolicy.All.Where(p => p.Risk == ProcessRisk.Broadcast))
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Explanation), entry.Process);
            Assert.True(entry.Explanation.Length > 20, $"{entry.Process}: {entry.Explanation}");
        }
    }

    [Fact]
    public void Protection_survives_the_shapes_a_caller_actually_passes()
    {
        // Callers get process identity from several different APIs. A safety check that only
        // works when the caller normalised first is one that will eventually be skipped.
        foreach (var shape in new[]
                 {
                     "obs64",
                     "obs64.exe",
                     "OBS64.EXE",
                     @"C:\Program Files\obs-studio\bin\64bit\obs64.exe",
                     "\"C:\\Program Files\\obs-studio\\bin\\64bit\\obs64.exe\"",
                 })
        {
            Assert.True(ProcessSafetyPolicy.IsProtected(shape), shape);
        }
    }
}
