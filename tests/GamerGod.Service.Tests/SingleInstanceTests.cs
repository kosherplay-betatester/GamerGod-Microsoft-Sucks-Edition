using GamerGod.Windows;
using Xunit;

namespace GamerGod.Service.Tests;

/// <summary>
/// One GamerGod window per signed-in user.
///
/// <para>
/// Two copies is not a cosmetic problem. Each puts its own icon in the notification area — which
/// is how it was noticed — each holds its own elevated helper, so a permission prompt already
/// approved gets asked again by an identical-looking window, and both read and write the same
/// journal: one could arm while the other still believed the machine was untouched.
/// </para>
///
/// <para>
/// These run in one process, which is enough: the lock is a named mutex, and a second
/// <see cref="SingleInstance.TryAcquire"/> while the first is still held behaves the same way
/// whether the caller is another thread or another process.
/// </para>
///
/// <para>
/// Collection-disabled because the mutex is machine-wide by name. Two of these running at once
/// would each be the other's "already running" instance.
/// </para>
/// </summary>
[Collection(nameof(SingleInstanceTests))]
[CollectionDefinition(nameof(SingleInstanceTests), DisableParallelization = true)]
public sealed class SingleInstanceTests
{
    [Fact]
    public void The_first_caller_gets_the_slot_and_the_second_does_not()
    {
        using var first = SingleInstance.TryAcquire();

        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire());
    }

    [Fact]
    public void The_second_caller_asks_the_first_to_show_itself()
    {
        // The whole point. A user who presses GamerGod again wants the window they already have,
        // not a message telling them where it went.
        using var first = SingleInstance.TryAcquire();
        Assert.NotNull(first);

        using var asked = new ManualResetEventSlim(false);
        first!.ActivationRequested += () => asked.Set();

        Assert.Null(SingleInstance.TryAcquire());

        Assert.True(
            asked.Wait(TimeSpan.FromSeconds(10)),
            "the running instance was never told that another copy had been started.");
    }

    [Fact]
    public void Every_further_launch_asks_again_not_just_the_first()
    {
        // Registered with executeOnlyOnce: false, because somebody who presses the shortcut
        // three times expects the window three times — not once and then silence.
        using var first = SingleInstance.TryAcquire();
        Assert.NotNull(first);

        var count = 0;
        using var asked = new SemaphoreSlim(0);

        first!.ActivationRequested += () =>
        {
            Interlocked.Increment(ref count);
            asked.Release();
        };

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(SingleInstance.TryAcquire());
            Assert.True(asked.Wait(TimeSpan.FromSeconds(10)), $"launch {i + 1} was not noticed.");
        }

        Assert.Equal(3, Volatile.Read(ref count));
    }

    [Fact]
    public void The_slot_is_free_again_once_the_holder_has_gone()
    {
        // Closing GamerGod and opening it again must work, which means the lock has to be
        // released rather than held until the process dies.
        var first = SingleInstance.TryAcquire();
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryAcquire();
        Assert.NotNull(second);
    }

    [Fact]
    public void Disposing_twice_is_not_an_error()
    {
        // Exit runs it, and so does a finaliser-shaped path on an unusual shutdown.
        var instance = SingleInstance.TryAcquire();
        Assert.NotNull(instance);

        instance!.Dispose();
        instance.Dispose();

        using var next = SingleInstance.TryAcquire();
        Assert.NotNull(next);
    }
}
