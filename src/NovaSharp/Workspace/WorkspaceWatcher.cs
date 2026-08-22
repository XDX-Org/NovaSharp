using System.Threading.Channels;
using NovaSharp.Platform;

namespace NovaSharp.Workspace;

public interface IWorkspaceWatcher : IAsyncDisposable
{
    event Func<WorkspaceChangeBatch, Task>? Changed;
    int Capacity { get; }
    int PendingCount { get; }
    void Watch(string? root);
}

public sealed class FileSystemWorkspaceWatcher : IWorkspaceWatcher
{
    private readonly Channel<WorkspaceChange> _events;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IWorkspacePaths _paths;
    private readonly Task _consumer;
    private FileSystemWatcher? _watcher;
    private int _pending;
    private int _overflowed;
    private int _disposed;

    public FileSystemWorkspaceWatcher(IWorkspacePaths paths, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _paths = paths;
        Capacity = capacity;
        _events = Channel.CreateBounded<WorkspaceChange>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _consumer = Task.Run(ConsumeAsync);
    }

    public event Func<WorkspaceChangeBatch, Task>? Changed;
    public int Capacity { get; }
    public int PendingCount => Volatile.Read(ref _pending);

    public void Watch(string? root)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var previous = Interlocked.Exchange(ref _watcher, null);
        previous?.Dispose();

        if (root is null)
        {
            return;
        }

        var watcher = new FileSystemWatcher(_paths.Canonicalize(root))
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes,
            InternalBufferSize = 32 * 1024,
        };
        watcher.Created += (_, args) => Enqueue(new WorkspaceChange(WorkspaceChangeKind.Created, args.FullPath));
        watcher.Changed += (_, args) => Enqueue(new WorkspaceChange(WorkspaceChangeKind.Changed, args.FullPath));
        watcher.Deleted += (_, args) => Enqueue(new WorkspaceChange(WorkspaceChangeKind.Deleted, args.FullPath));
        watcher.Renamed += (_, args) => Enqueue(new WorkspaceChange(WorkspaceChangeKind.Renamed, args.FullPath, args.OldFullPath));
        watcher.Error += (_, _) => Interlocked.Exchange(ref _overflowed, 1);
        watcher.EnableRaisingEvents = true;
        _watcher = watcher;
    }

    private void Enqueue(WorkspaceChange change)
    {
        if (_events.Writer.TryWrite(change))
        {
            Interlocked.Increment(ref _pending);
        }
        else
        {
            Interlocked.Exchange(ref _overflowed, 1);
        }
    }

    private async Task ConsumeAsync()
    {
        try
        {
            while (await _events.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                var coalesced = new Dictionary<string, WorkspaceChange>(StringComparer.Ordinal);
                Drain(coalesced);
                await Task.Delay(50, _shutdown.Token).ConfigureAwait(false);
                Drain(coalesced);

                var overflowed = Interlocked.Exchange(ref _overflowed, 0) != 0;
                var handler = Changed;
                if (handler is not null && (coalesced.Count > 0 || overflowed))
                {
                    await handler(new WorkspaceChangeBatch([.. coalesced.Values], overflowed)).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Drain(Dictionary<string, WorkspaceChange> coalesced)
    {
        while (_events.Reader.TryRead(out var change))
        {
            Interlocked.Decrement(ref _pending);
            var path = _paths.Canonicalize(change.Path);
            coalesced[path] = change with
            {
                Path = path,
                OldPath = change.OldPath is null ? null : _paths.Canonicalize(change.OldPath),
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _watcher, null)?.Dispose();
        _events.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        try
        {
            await _consumer.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
        }
        _shutdown.Dispose();
    }
}
