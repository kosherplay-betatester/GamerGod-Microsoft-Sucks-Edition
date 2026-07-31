namespace GamerGod.Core.Forensics;

/// <summary>
/// What the data showed was occupying the frame when it arrived late.
///
/// <para>
/// These name a stage of the present pipeline, not a fault. "The GPU was busy for 36 ms of a
/// 40 ms frame" is a fact about a capture; "your GPU is the bottleneck" is a claim about a
/// machine, a game and a scene that nothing here measured. Article VII is the difference, and
/// the wording throughout this namespace stays on the first side of it.
/// </para>
/// </summary>
public enum StallCause
{
    /// <summary>
    /// The capture does not contain an answer for this frame.
    ///
    /// <para>
    /// Deliberately the zero value: a default-constructed classification cannot accidentally
    /// claim a cause. This is a real result and it is counted alongside the others — a report
    /// that quietly dropped the frames it could not explain would be entirely confident about
    /// whatever fraction it happened to understand, and there would be nothing on screen to
    /// suggest otherwise.
    /// </para>
    /// </summary>
    Unattributable,

    /// <summary>The game's own CPU work occupied at least half the frame.</summary>
    GameCpuBound,

    /// <summary>
    /// The game's CPU thread was blocked for at least half the frame while the GPU was mostly
    /// idle — so whatever it was waiting for, it was not the GPU.
    /// </summary>
    CpuBlocked,

    /// <summary>The GPU was executing work for at least half the frame.</summary>
    GpuBound,

    /// <summary>
    /// Presentation left independent flip on this frame: the previous frame was an independent
    /// flip and this one is not. The transition itself costs a frame.
    ///
    /// <para>
    /// Named for the transition, not for a dropped frame — an earlier name, "dropped from
    /// independent flip", read as "a dropped frame that came from an independent flip", which
    /// is a different measurement the captured columns cannot answer at all.
    /// </para>
    ///
    /// <para>
    /// This one is load-bearing beyond forensics. Leaving independent flip is exactly what a
    /// layered overlay window can cause, so it is the signal by which GamerGod's own overlay
    /// will detect that it is costing the frame it is drawing on.
    /// </para>
    /// </summary>
    LeftIndependentFlip,

    /// <summary>The desktop compositor was in this frame's presentation path.</summary>
    Compositor,
}

/// <summary>Wording for each cause, in one place so no two of them can drift into agreeing.</summary>
public static class StallCauses
{
    /// <summary>A short description of what the data showed. Never advice.</summary>
    public static string Label(this StallCause cause) => cause switch
    {
        StallCause.GameCpuBound => "The game's own CPU work filled the frame",
        StallCause.CpuBlocked => "The CPU waited while the GPU was idle",
        StallCause.GpuBound => "The GPU was busy for most of the frame",
        StallCause.LeftIndependentFlip => "Presentation left independent flip",
        StallCause.Compositor => "The frame went through the desktop compositor",
        _ => "Not attributable from the columns captured",
    };
}
