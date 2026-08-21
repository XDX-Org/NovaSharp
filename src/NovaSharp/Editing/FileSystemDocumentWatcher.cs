namespace NovaSharp.Editing;

/// <inheritdoc cref="IDocumentWatcher"/>
public sealed class FileSystemDocumentWatcher : IDocumentWatcher
{
    /// <summary>How long events are collected before one notification is raised.</summary>
    /// <remarks>
    /// A single save by another program is several file-system events on most platforms — a create, one or more
    /// writes, a rename — and asking the user about each of them would be three questions for one change.
    /// </remarks>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);

    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _settle;
    private bool _disposed;

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Watch(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        var name = Path.GetFileName(full);

        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name) || !Directory.Exists(directory))
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();

            // The directory is watched rather than the file, because a file watched by name stops existing the moment
            // another program replaces it with a rename — which is exactly how a careful editor saves.
            var watcher = new FileSystemWatcher(directory, name)
            {
                NotifyFilter = NotifyFilters.LastWrite
                    | NotifyFilters.Size
                    | NotifyFilters.FileName
                    | NotifyFilters.Attributes,
                IncludeSubdirectories = false,
            };

            watcher.Changed += OnEvent;
            watcher.Created += OnEvent;
            watcher.Deleted += OnEvent;
            watcher.Renamed += OnEvent;

            // A watcher that overflows its buffer has lost events it can never recover. Treating that as a change is
            // the only safe reading: the check it triggers is cheap and finding nothing changed costs nothing.
            watcher.Error += (_, _) => Schedule();

            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs args) => Schedule();

    private void Schedule()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // One timer, restarted by each event, so a burst raises a single notification once it stops.
            _settle ??= new Timer(static state => ((FileSystemDocumentWatcher)state!).Raise(), this, Timeout.Infinite, Timeout.Infinite);
            _settle.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Raise()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Changed?.Invoke();
    }

    private void StopCore()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        _settle?.Change(Timeout.Infinite, Timeout.Infinite);
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
            StopCore();
            _settle?.Dispose();
            _settle = null;
        }

        return ValueTask.CompletedTask;
    }
}
