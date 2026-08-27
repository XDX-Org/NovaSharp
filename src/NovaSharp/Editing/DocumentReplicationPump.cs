using System.Threading.Channels;

namespace NovaSharp.Editing;

/// <summary>
/// Carries ordered edit batches from Monaco to one document's replica.
/// </summary>
/// <remarks>
/// The producer is a JavaScript callback, so <see cref="TryEnqueue"/> never waits: it hands the batch over or reports
/// that it could not, and the typing path continues either way. A full queue and a sequence gap are the same kind of
/// failure — the shadow can no longer be reconstructed from what it has — and both are answered by one full
/// resynchronization rather than by growing the backlog.
/// </remarks>
public sealed class DocumentReplicationPump : IAsyncDisposable
{
    private readonly Channel<TextEditBatch> _channel;
    private readonly DocumentReplica _replica;
    private readonly Func<CancellationToken, Task<DocumentSnapshot>> _requestSnapshot;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _consumer;

    private int _resyncPending;
    private int _droppedBatchCount;
    private int _disposed;

    /// <param name="replica">The shadow this pump is the single writer of.</param>
    /// <param name="requestSnapshot">Fetches Monaco's current text and sequence, used to recover from a gap.</param>
    /// <param name="capacity">Batches held before further ones are dropped in favour of a resynchronization.</param>
    public DocumentReplicationPump(
        DocumentReplica replica,
        Func<CancellationToken, Task<DocumentSnapshot>> requestSnapshot,
        int capacity = 256)
    {
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentNullException.ThrowIfNull(requestSnapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _replica = replica;
        _requestSnapshot = requestSnapshot;
        Capacity = capacity;

        _channel = Channel.CreateBounded<TextEditBatch>(new BoundedChannelOptions(capacity)
        {
            // Wait, paired with TryWrite, is what makes a full queue observable: TryWrite returns false rather than
            // blocking, and the caller answers with a resynchronization. The dropping modes would silently discard an
            // item and still report success, which is a shadow that diverges with nothing to notice it.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>The configured queue depth.</summary>
    public int Capacity { get; }

    /// <summary>Batches waiting to be applied, so saturation is observable rather than inferred.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>How many batches were dropped because the queue was full.</summary>
    public int DroppedBatchCount => Volatile.Read(ref _droppedBatchCount);

    /// <summary>How many full resynchronizations this document has needed.</summary>
    public int ResyncCount => _replica.ResyncCount;

    /// <summary>Raised when a resynchronization could not be completed, so the workbench can say so.</summary>
    public event Action<Exception>? ResyncFailed;

    /// <summary>Raised from the pump worker after the replica advances.</summary>
    public event Action<long>? ReplicaAdvanced;

    /// <summary>
    /// Hands <paramref name="batch"/> to the pump without waiting.
    /// </summary>
    /// <returns><see langword="false"/> when the batch was dropped and a resynchronization was scheduled instead.</returns>
    public bool TryEnqueue(TextEditBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(batch))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedBatchCount);
        RequestResync();
        return false;
    }

    /// <summary>Completes once the replica has caught up to <paramref name="sequence"/>.</summary>
    public Task WaitForSequenceAsync(long sequence, CancellationToken cancellationToken) =>
        _replica.WaitForSequenceAsync(sequence, cancellationToken);

    /// <summary>
    /// Asks for a full resynchronization, for a change to the model that no edit batch can describe.
    /// </summary>
    /// <remarks>
    /// A sentinel is queued as well as the flag being set, because the consumer may be idle with nothing in hand and a
    /// flag nobody looks at is a shadow that stays wrong. The sentinel carries a sequence no replica can be behind, so
    /// it is skipped rather than applied.
    /// </remarks>
    public void RequestResync()
    {
        Interlocked.Exchange(ref _resyncPending, 1);
        _channel.Writer.TryWrite(ResyncSentinel);
    }

    private static TextEditBatch ResyncSentinel { get; } = new(
        DocumentUri: string.Empty,
        BaseSequence: -1,
        ResultSequence: -1,
        AlternativeSequence: -1,
        EditOrigins.NovaSharp,
        []);

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var batch in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                // A batch already contained in a snapshot taken after it was queued is not a gap; it is work the
                // resynchronization has already done. Applying it would fail validation and ask for another snapshot,
                // turning one recovery into one per stale batch.
                if (batch.ResultSequence > _replica.Sequence)
                {
                    if (_replica.Apply(batch) == ReplicaApplyOutcome.NeedsResync)
                    {
                        RequestResync();
                    }
                    else
                    {
                        ReplicaAdvanced?.Invoke(_replica.Sequence);
                    }
                }

                if (Interlocked.Exchange(ref _resyncPending, 0) == 1)
                {
                    await ResyncAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    private async Task ResyncAsync()
    {
        try
        {
            var snapshot = await _requestSnapshot(_shutdown.Token).ConfigureAwait(false);
            _replica.Resync(snapshot.Text, snapshot.Sequence, snapshot.AlternativeSequence);
            ReplicaAdvanced?.Invoke(snapshot.Sequence);
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
        catch (Exception exception)
        {
            // The shadow is now knowingly stale. Leaving it silently wrong would let a later save write text the user
            // never typed, so the failure is surfaced and the flag is left set for the next batch to retry.
            RequestResync();
            ResyncFailed?.Invoke(exception);
        }
    }

    /// <summary>Stops accepting batches, drains what is queued, and waits for the consumer with a deadline.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            // Drains rather than cancelling first: batches already accepted are the user's typing, and phase 2's
            // shutdown contract is to checkpoint them to a deadline before letting the process go.
            await _consumer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A consumer that outlived its deadline must not hold up shutdown.
        }
        finally
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
            _shutdown.Dispose();
        }
    }
}
