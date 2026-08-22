using NovaSharp.Async;
using Xunit;

namespace NovaSharp.Tests;

/// <summary>Interlocked helpers the concurrency tests need but the BCL does not provide directly.</summary>
internal static class InterlockedExtensions
{
    public static void Max(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}

public sealed class BoundedWorkQueueTests
{
    [Fact]
    public async Task EnqueueAsync_RunsWorkAndReturnsItsResult()
    {
        await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);

        Assert.Equal(21, await queue.EnqueueAsync(_ => Task.FromResult(21), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_DoesNotRunWorkOnTheCallersStack()
    {
        await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
        var release = new TaskCompletionSource();

        var pending = queue.EnqueueAsync(async _ =>
        {
            await release.Task;
            return 5;
        }, TestContext.Current.CancellationToken);

        // The caller returned while the work was still blocked, which is the property the UI thread depends on.
        Assert.False(pending.IsCompleted);

        release.SetResult();
        Assert.Equal(5, await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_NeverExceedsTheConfiguredWorkerCount()
    {
        await using var queue = new BoundedWorkQueue(capacity: 8, workerCount: 1);
        var concurrent = 0;
        var peak = 0;
        var release = new TaskCompletionSource();

        var items = Enumerable.Range(0, 4).Select(_ => queue.EnqueueAsync(async _ =>
        {
            var running = Interlocked.Increment(ref concurrent);
            InterlockedExtensions.Max(ref peak, running);
            await release.Task;
            Interlocked.Decrement(ref concurrent);
            return 0;
        }, TestContext.Current.CancellationToken)).ToArray();

        // Give the single worker time to pick up whatever it is going to pick up before releasing them.
        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        release.SetResult();
        await Task.WhenAll(items).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref peak));
    }

    [Fact]
    public async Task EnqueueAsync_SurfacesFailuresToTheCaller()
    {
        await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => queue.EnqueueAsync<int>(_ => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnqueueAsync_AppliesBackpressureOnceTheQueueIsFull()
    {
        await using var queue = new BoundedWorkQueue(capacity: 1, workerCount: 1);
        var release = new TaskCompletionSource();
        var occupied = new TaskCompletionSource();

        var running = queue.EnqueueAsync(async _ =>
        {
            occupied.SetResult();
            await release.Task;
            return 0;
        }, TestContext.Current.CancellationToken);

        await occupied.Task;

        // One worker is busy and the single queue slot is what the next item must wait for. A third item cannot be
        // accepted until space frees up, which is the backpressure this queue exists to provide.
        var queued = queue.EnqueueAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken);
        var blocked = queue.EnqueueAsync(_ => Task.FromResult(2), TestContext.Current.CancellationToken);

        var settledEarly = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
        Assert.NotSame(blocked, settledEarly);

        release.SetResult();

        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(1, await queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, await blocked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ForegroundLane_RunsBeforeQueuedBackgroundWork()
    {
        await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var running = queue.EnqueueAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
            return 0;
        }, TestContext.Current.CancellationToken);
        await started.Task;

        var background = queue.EnqueueAsync(_ =>
        {
            order.Add("background");
            return Task.FromResult(0);
        }, TestContext.Current.CancellationToken);
        var foreground = queue.EnqueueForegroundAsync(_ =>
        {
            order.Add("foreground");
            return Task.FromResult(0);
        }, TestContext.Current.CancellationToken);

        release.SetResult();
        await Task.WhenAll(running, background, foreground);
        Assert.Equal(["foreground", "background"], order);
    }

    [Fact]
    public async Task EnqueueAsync_CancelsQueuedWorkWhenTheCallerCancels()
    {
        await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
        using var cancellation = new CancellationTokenSource();

        var observed = new TaskCompletionSource();
        var pending = queue.EnqueueAsync(async token =>
        {
            observed.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, cancellation.Token);

        await observed.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisposeAsync_CancelsWorkThatWasStillQueued()
    {
        var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
        var occupied = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var running = queue.EnqueueAsync(async _ =>
        {
            occupied.SetResult();
            await release.Task;
            return 0;
        }, TestContext.Current.CancellationToken);

        await occupied.Task;
        var queued = queue.EnqueueAsync(_ => Task.FromResult(1), TestContext.Current.CancellationToken);

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        release.SetResult();

        // The item that never got a worker is completed as cancelled, not left awaiting forever.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => queued.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The item that did get one settles either way. Awaiting it with a deadline is the assertion: sampling its
        // flags instead would pass or fail on how quickly the worker happened to be scheduled.
        try
        {
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task Capacity_IsObservable()
    {
        await using var queue = new BoundedWorkQueue(capacity: 9, workerCount: 1);

        Assert.Equal(9, queue.Capacity);
        Assert.Equal(18, queue.TotalCapacity);
    }

    [Fact]
    public void Constructor_RejectsUnboundedConfiguration()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedWorkQueue(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedWorkQueue(capacity: 1, workerCount: 0));
    }

    [Fact]
    public async Task DisposeAsync_CompletesEvenWhileWorkIsRunning()
    {
        var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
        var started = new TaskCompletionSource();

        _ = queue.EnqueueAsync(async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }, TestContext.Current.CancellationToken);

        await started.Task;

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await queue.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => queue.EnqueueAsync(_ => Task.FromResult(0), TestContext.Current.CancellationToken));
    }
}
