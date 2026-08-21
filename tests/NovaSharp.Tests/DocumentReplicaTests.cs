using NovaSharp.Editing;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentReplicaTests
{
    private static TextEditBatch Batch(long baseSequence, long result, params TextEdit[] edits) =>
        new("file:///widget.cs", baseSequence, result, result, EditOrigins.User, edits);

    [Fact]
    public void Apply_AppliesEveryEditInOneBatchAgainstTheTextBeforeIt()
    {
        var replica = new DocumentReplica("0123456789", sequence: 1, alternativeSequence: 1);

        // Both offsets refer to the original text. Applying the first would move the second if they were not applied
        // from the end backwards, which is the bug this asserts against.
        var outcome = replica.Apply(Batch(1, 2, new TextEdit(0, 2, "AB"), new TextEdit(8, 10, "YZ")));

        Assert.Equal(ReplicaApplyOutcome.Applied, outcome);
        Assert.Equal("AB234567YZ", replica.Snapshot().Text);
        Assert.Equal(2, replica.Sequence);
    }

    [Fact]
    public void Apply_HandlesInsertionsDeletionsAndReplacementsOfDifferentLengths()
    {
        var replica = new DocumentReplica("hello world", 1, 1);

        replica.Apply(Batch(1, 2, new TextEdit(5, 6, ", cruel "), new TextEdit(11, 11, "!")));

        Assert.Equal("hello, cruel world!", replica.Snapshot().Text);
    }

    [Fact]
    public void Apply_KeepsSurrogatePairsIntact()
    {
        // Offsets are UTF-16 code units, Monaco's own unit. A pair is two units, so replacing one of them would be a
        // request Monaco never makes and text no encoding can write.
        var replica = new DocumentReplica("a𝄞b", 1, 1);

        replica.Apply(Batch(1, 2, new TextEdit(1, 3, "𝅘𝅥")));

        Assert.Equal("a𝅘𝅥b", replica.Snapshot().Text);
    }

    [Fact]
    public void Apply_KeepsCombiningMarksWithTheirBaseCharacter()
    {
        var replica = new DocumentReplica("éx", 1, 1);

        replica.Apply(Batch(1, 2, new TextEdit(2, 3, "y")));

        Assert.Equal("éy", replica.Snapshot().Text);
    }

    [Fact]
    public void Apply_TreatsBidirectionalTextAsOrdinaryCodeUnits()
    {
        var replica = new DocumentReplica("abc אבג def", 1, 1);

        replica.Apply(Batch(1, 2, new TextEdit(4, 7, "ד")));

        Assert.Equal("abc ד def", replica.Snapshot().Text);
    }

    [Fact]
    public void Apply_AsksForAResyncRatherThanGuessingAtAGap()
    {
        var replica = new DocumentReplica("abc", 1, 1);

        Assert.Equal(ReplicaApplyOutcome.NeedsResync, replica.Apply(Batch(7, 8, new TextEdit(0, 0, "x"))));
        Assert.Equal(TextEditBatchProblem.SequenceGap, replica.LastProblem);
        Assert.Equal("abc", replica.Snapshot().Text);
        Assert.Equal(1, replica.Sequence);
    }

    [Fact]
    public void Apply_RejectsABatchWholeRatherThanPartially()
    {
        var replica = new DocumentReplica("abc", 1, 1);

        // The second edit is out of range. Applying the first anyway would leave the shadow in a state neither side
        // believes in, which is worse than rejecting both and resynchronizing.
        Assert.Equal(
            ReplicaApplyOutcome.NeedsResync,
            replica.Apply(Batch(1, 2, new TextEdit(0, 1, "X"), new TextEdit(5, 9, "Y"))));

        Assert.Equal("abc", replica.Snapshot().Text);
    }

    [Fact]
    public void Snapshot_IsStableWhileTheReplicaMovesOn()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        var before = replica.Snapshot();

        replica.Apply(Batch(1, 2, new TextEdit(0, 3, "xyz")));

        Assert.Equal("abc", before.Text);
        Assert.Equal(1, before.Sequence);
        Assert.Equal("xyz", replica.Snapshot().Text);
    }

    [Fact]
    public void Snapshot_IsCachedUntilTheNextChange()
    {
        var replica = new DocumentReplica("abc", 1, 1);

        Assert.Same(replica.Snapshot(), replica.Snapshot());
    }

    [Fact]
    public void Resync_ReplacesEverythingAndCounts()
    {
        var replica = new DocumentReplica("abc", 1, 1);

        replica.Resync("totally different", 40, 40);

        Assert.Equal("totally different", replica.Snapshot().Text);
        Assert.Equal(40, replica.Sequence);
        Assert.Equal(1, replica.ResyncCount);
    }

    [Fact]
    public async Task WaitForSequenceAsync_CompletesImmediatelyWhenAlreadyThere()
    {
        var replica = new DocumentReplica("abc", 5, 5);

        await replica.WaitForSequenceAsync(5, TestContext.Current.CancellationToken);
        await replica.WaitForSequenceAsync(3, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForSequenceAsync_ReleasesWhenTheBatchesArrive()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        var barrier = replica.WaitForSequenceAsync(3, TestContext.Current.CancellationToken);

        Assert.False(barrier.IsCompleted);

        replica.Apply(Batch(1, 2, new TextEdit(0, 0, "x")));
        Assert.False(barrier.IsCompleted);

        replica.Apply(Batch(2, 3, new TextEdit(0, 0, "y")));
        await barrier.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForSequenceAsync_IsSatisfiedByAResyncPastIt()
    {
        // A save waits for the sequence the editor was at. Recovering by snapshot reaches a later sequence, and the
        // text it holds is newer than what was asked for, so the barrier is satisfied rather than stuck.
        var replica = new DocumentReplica("abc", 1, 1);
        var barrier = replica.WaitForSequenceAsync(3, TestContext.Current.CancellationToken);

        replica.Resync("recovered", 9, 9);

        await barrier.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitForSequenceAsync_Cancels()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        using var cancellation = new CancellationTokenSource();

        var barrier = replica.WaitForSequenceAsync(99, cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => barrier);
    }

    [Fact]
    public async Task WaitForSequenceAsync_ReleasesEveryWaiterTheBatchSatisfies()
    {
        var replica = new DocumentReplica("abc", 1, 1);
        var first = replica.WaitForSequenceAsync(2, TestContext.Current.CancellationToken);
        var second = replica.WaitForSequenceAsync(3, TestContext.Current.CancellationToken);
        var later = replica.WaitForSequenceAsync(9, TestContext.Current.CancellationToken);

        replica.Apply(Batch(1, 4, new TextEdit(0, 0, "x")));

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(later.IsCompleted);
    }
}
