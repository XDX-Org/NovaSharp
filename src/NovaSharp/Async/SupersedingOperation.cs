namespace NovaSharp.Async;

/// <summary>
/// Runs at most one live operation of a kind. Starting a new one cancels the previous and stamps a new version, so a
/// result produced by a superseded operation is discarded instead of published.
/// </summary>
public sealed class SupersedingOperation : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _current;
    private long _version;
    private bool _disposed;

    /// <summary>The version stamped on the most recently started operation.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// Runs <paramref name="work"/>, then publishes its result through <paramref name="publish"/> only if this
    /// operation is still the current one.
    /// </summary>
    /// <returns><see langword="true"/> when the result was published; <see langword="false"/> when it was superseded or canceled.</returns>
    public async Task<bool> RunAsync<T>(
        Func<CancellationToken, Task<T>> work,
        Func<T, CancellationToken, Task> publish,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(publish);

        CancellationTokenSource source;
        long version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Only a live, undisposed source is ever reachable through _current: the owning call clears it under this
            // same lock before disposing it.
            _current?.Cancel();
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _current = source;
            version = ++_version;
        }

        try
        {
            var result = await work(source.Token).ConfigureAwait(false);

            if (Interlocked.Read(ref _version) != version || source.Token.IsCancellationRequested)
            {
                return false;
            }

            await publish(result, source.Token).ConfigureAwait(false);
            return Interlocked.Read(ref _version) == version;
        }
        catch (OperationCanceledException) when (source.Token.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, source))
                {
                    _current = null;
                }
            }

            source.Dispose();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _current?.Cancel();
            _current = null;
        }

        return ValueTask.CompletedTask;
    }
}
