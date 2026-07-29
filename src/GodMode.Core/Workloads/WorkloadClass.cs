namespace GodMode.Core.Workloads;

/// <summary>
/// What kind of thing is being optimised. Routing heuristics and, crucially, the metric
/// that counts as "better" differ sharply between these.
/// </summary>
public enum WorkloadClass
{
    /// <summary>A native PC game. Success metric: 1% and 0.1% low frame times.</summary>
    NativeGame,

    /// <summary>
    /// A console emulator. Two things make this the most favourable workload GodMode has:
    /// emulators carry no anti-cheat, so every contact lever is available; and they are
    /// dominated by a small number of latency-critical threads that respond strongly to
    /// cache and to being left alone.
    ///
    /// <para>
    /// Success is measured differently. An emulator is not trying to go fast, it is trying
    /// to reproduce a fixed console refresh exactly. See
    /// <see cref="EmulatorProfile.NativeRefreshHz"/>.
    /// </para>
    /// </summary>
    Emulator,

    /// <summary>
    /// An Android or other virtualised guest, running on a hypervisor. The tunable surface
    /// is the host's virtual machine worker threads rather than the guest itself.
    /// </summary>
    VirtualizedGuest,

    /// <summary>
    /// A store or launcher — Steam, Epic, EA, Battle.net. Never the foreground workload;
    /// always a candidate for eviction to the ambient domain once a game is running.
    /// </summary>
    Launcher,

    /// <summary>Not recognised. Treated as <see cref="NativeGame"/> with no tuned profile.</summary>
    Unknown,
}

/// <summary>
/// How a workload's threads are shaped, which determines how many cores it can actually
/// use and therefore how much of the machine is worth reserving for it.
/// </summary>
public enum ThreadModel
{
    /// <summary>
    /// One dominant thread carries almost all the work. More cores do nothing; cache and
    /// per-core latency are everything. Classic for older emulators and accuracy-focused
    /// cores.
    /// </summary>
    SingleThreadBound,

    /// <summary>
    /// Two or three heavy threads, typically an emulated CPU thread and a graphics thread.
    /// The most common emulator shape.
    /// </summary>
    FewHeavyThreads,

    /// <summary>
    /// Genuinely parallel across many threads, e.g. an emulator reproducing a multi-core
    /// console. Benefits from a wide domain.
    /// </summary>
    ManyThreads,
}
