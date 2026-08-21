using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentReplicationPumpTests
{
    private static TextEditBatch Batch(long baseSequence, long result, params TextEdit[] edits) =>
        new("file:///widget.cs", baseSequence, result, result, EditOrigins.User, edits);

    [Fact]
    public async Task Enqueue_AppliesBatchesInOrder()
    {
        var replica = new DocumentReplica(string.Empty, 1, 1);
        await using var pump = new DocumentReplicationPump(replica, _ => throw new UnreachableException());

        for (var i = 0; i < 10; i++)
        {
            Assert.True(pump.TryEnqueue(Batch(i + 1, i + 2, new TextEdit(i, i, i.ToString()))));
        }

        await replica.WaitForSequenceAsync(11, TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("0123456789", replica.Snapshot().Text);
    }

    [Fact]
    public async Task Enqueue_NeverWaitsOnTheCaller()
    {
        // The producer is a JavaScript callback on the typing path. A full queue must be answered immediately rather
        // than by applying backpressure to a keystroke, so the consumer is held inside a recovery while the queue is
        // deliberately overrun.
        var replica = new DocumentReplica(string.Empty, 1, 1);
        var release = new TaskCompletionSource();
        var blocked = new TaskCompletionSource();

        await using var pump = new DocumentReplicationPump(
            replica,
            async _ =>
            {
                blocked.TrySetResult();
                await release.Task;
                return new DocumentSnapshot("recovered", 500, 500);
            },
            capacity: 4);

        pump.RequestResync();
        await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        var dropped = false;
        for (var i = 0; i < 200; i++)
        {
            dropped |= !pump.TryEnqueue(Batch(i + 1, i + 2, new TextEdit(0, 0, "x")));
        }

        Assert.True(dropped, "A queue that is full must drop rather than make the caller wait.");
        Assert.True(pump.DroppedBatchCount > 0);

        release.SetResult();
        await WaitForAsync(() => replica.Snapshot().Text == "recovered");
    }

    [Fact]
    public async Task Enqueue_RecoversFromASequenceGapWithOneSnapshot()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        var snapshots = 0;

        await using var pump = new DocumentReplicationPump(replica, _ =>
        {
            Interlocked.Increment(ref snapshots);
            return Task.FromResult(new DocumentSnapshot("recovered", 90, 90));
        });

        // A batch that does not continue from where the shadow is. Nothing about it can be applied safely.
        pump.TryEnqueue(Batch(50, 51, new TextEdit(0, 0, "x")));

        await WaitForAsync(() => replica.ResyncCount == 1);
        Assert.Equal("recovered", replica.Snapshot().Text);
        Assert.Equal(1, Volatile.Read(ref snapshots));
    }

    [Fact]
    public async Task Enqueue_DiscardsBatchesTheSnapshotAlreadyContains()
    {
        // After recovering to sequence 90, the batches still queued describe states before it. Applying them would
        // fail and ask for another snapshot each time, turning one recovery into one per stale batch.
        var replica = new DocumentReplica("abc", 1, 1);
        var snapshots = 0;

        await using var pump = new DocumentReplicationPump(replica, _ =>
        {
            Interlocked.Increment(ref snapshots);
            return Task.FromResult(new DocumentSnapshot("recovered", 90, 90));
        });

        pump.TryEnqueue(Batch(50, 51, new TextEdit(0, 0, "x")));
        for (var i = 0; i < 20; i++)
        {
            pump.TryEnqueue(Batch(51 + i, 52 + i, new TextEdit(0, 0, "y")));
        }

        await WaitForAsync(() => replica.ResyncCount >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref snapshots));
        Assert.Equal("recovered", replica.Snapshot().Text);
    }

    [Fact]
    public async Task RequestResync_WakesAnIdlePump()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        await using var pump = new DocumentReplicationPump(
            replica,
            _ => Task.FromResult(new DocumentSnapshot("after an end-of-line change", 7, 7)));

        pump.RequestResync();

        await WaitForAsync(() => replica.ResyncCount == 1);
        Assert.Equal("after an end-of-line change", replica.Snapshot().Text);
    }

    [Fact]
    public async Task ResyncFailure_IsReportedRatherThanLeavingASilentlyWrongShadow()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        var reported = new TaskCompletionSource<Exception>();

        await using var pump = new DocumentReplicationPump(
            replica,
            _ => Task.FromException<DocumentSnapshot>(new InvalidOperationException("the page is gone")));

        pump.ResyncFailed += exception => reported.TrySetResult(exception);
        pump.RequestResync();

        var failure = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("the page is gone", failure.Message);
    }

    [Fact]
    public async Task Dispose_DrainsWhatWasAlreadyAccepted()
    {
        var replica = new DocumentReplica(string.Empty, 1, 1);
        var pump = new DocumentReplicationPump(replica, _ => throw new UnreachableException());

        for (var i = 0; i < 20; i++)
        {
            pump.TryEnqueue(Batch(i + 1, i + 2, new TextEdit(i, i, "x")));
        }

        await pump.DisposeAsync();

        Assert.Equal(new string('x', 20), replica.Snapshot().Text);
        Assert.False(pump.TryEnqueue(Batch(21, 22, new TextEdit(0, 0, "y"))));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The pump did not reach the expected state within its deadline.");
    }

    private sealed class UnreachableException() : InvalidOperationException("No resynchronization should have been needed.");
}
