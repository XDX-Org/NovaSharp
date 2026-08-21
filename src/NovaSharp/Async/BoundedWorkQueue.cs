using System.Threading.Channels;

namespace NovaSharp.Async;

/// <summary>
/// A bounded background work queue. Callers hand it work that must not run on the UI or Monaco thread; the queue
/// applies backpressure once it is full rather than accumulating an unbounded backlog.
/// </summary>
public sealed class BoundedWorkQueue : IAsyncDisposable
{
    private readonly Channel<Func<CancellationToken, Task>> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private int _disposed;

    /// <param name="capacity">Maximum queued items before <see cref="EnqueueAsync{T}"/> waits for space.</param>
    /// <param name="workerCount">Number of concurrent workers draining the queue.</param>
    public BoundedWorkQueue(int capacity = 32, int workerCount = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);

        Capacity = capacity;
        _channel = Channel.CreateBounded<Func<CancellationToken, Task>>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

        _workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            _workers[i] = Task.Run(DrainAsync);
        }
    }

    /// <summary>The configured queue depth, exposed so saturation is observable rather than implicit.</summary>
    public int Capacity { get; }

    /// <summary>Queues <paramref name="work"/> and returns its result. Waits for space when the queue is full.</summary>
    public async Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunAsync(CancellationToken queueToken)
        {
            // Reached with an already-cancelled token when the queue is disposed with items still pending. The caller
            // is completed as cancelled rather than left waiting on work that will never run.
            if (queueToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(queueToken);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, cancellationToken);
            try
            {
                completion.TrySetResult(await work(linked.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (linked.Token.IsCancellationRequested)
            {
                completion.TrySetCanceled(linked.Token);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        try
        {
            await _channel.Writer.WriteAsync(RunAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new ObjectDisposedException(nameof(BoundedWorkQueue));
        }

        using var registration = cancellationToken.Register(static state =>
        {
            ((TaskCompletionSource<T>)state!).TrySetCanceled();
        }, completion);

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await item(_shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested.
        }
    }

    /// <summary>
    /// Stops accepting work, cancels what is running, and waits for the workers with a deadline so a stuck item cannot
    /// block application exit.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_workers).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A worker outlived its deadline; shutdown continues rather than hanging the application.
        }
        catch (OperationCanceledException)
        {
            // Expected while draining after cancellation.
        }

        // Anything still queued is completed as cancelled. Abandoning it would leave its caller awaiting forever.
        while (_channel.Reader.TryRead(out var abandoned))
        {
            await abandoned(new CancellationToken(canceled: true)).ConfigureAwait(false);
        }

        _shutdown.Dispose();
    }
}
