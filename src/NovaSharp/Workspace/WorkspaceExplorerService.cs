using System.Collections.Concurrent;
using System.Diagnostics;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Platform;

namespace NovaSharp.Workspace;

public sealed class WorkspaceExplorerService : IAsyncDisposable
{
    private static readonly string[] DefaultIgnored = [".git", "bin", "obj"];

    private readonly IWorkspacePaths _paths;
    private readonly IWorkspaceFileSystem _files;
    private readonly IWorkspaceWatcher _watcher;
    private readonly WorkspacePersistenceService _persistence;
    private readonly INotificationService _notifications;
    private readonly BoundedWorkQueue _mutations = new(capacity: 32, workerCount: 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _enumerations = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private WorkspaceSnapshot _snapshot = new();
    private IReadOnlyList<string> _ignoredPaths = DefaultIgnored;
    private bool _disposed;

    public WorkspaceExplorerService(
        IWorkspacePaths paths,
        IWorkspaceFileSystem files,
        IWorkspaceWatcher watcher,
        WorkspacePersistenceService persistence,
        INotificationService notifications)
    {
        _paths = paths;
        _files = files;
        _watcher = watcher;
        _persistence = persistence;
        _notifications = notifications;
        _watcher.Changed += OnWatcherChangedAsync;
    }

    public WorkspaceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<WorkspaceSnapshot>? Changed;
    public event Func<WorkspaceRelocation, Task>? Relocated;

    public void SetIgnoredPaths(IReadOnlyList<string>? paths) =>
        _ignoredPaths = paths is null or { Count: 0 } ? DefaultIgnored : [.. DefaultIgnored, .. paths];

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Problem is not null)
        {
            Report("novasharp.workspace.restore", loaded.Problem);
        }

        Update(current => current with
        {
            SidebarVisible = loaded.State.SidebarVisible,
            SidebarWidth = Math.Clamp(loaded.State.SidebarWidth, 160, 520),
        });

        if (loaded.State.WorkspacePath is null
            || !await _files.DirectoryExistsAsync(loaded.State.WorkspacePath, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await OpenAsync(loaded.State.WorkspacePath, cancellationToken).ConfigureAwait(false);
        foreach (var relative in loaded.State.ExpandedPaths.OrderBy(static value => value.Count(character => character == '/')))
        {
            try
            {
                await ExpandAsync(_paths.ResolveWorkspaceRelativePath(loaded.State.WorkspacePath, relative), cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException)
            {
            }
        }

        var selected = ResolveOptional(loaded.State.WorkspacePath, loaded.State.SelectedPath);
        var active = ResolveOptional(loaded.State.WorkspacePath, loaded.State.ActivePath);
        Update(current => current with { SelectedId = selected is null ? null : Id(selected), ActivePath = active });
    }

    public async Task OpenAsync(string root, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var canonical = _paths.Canonicalize(root);
        if (!await _files.DirectoryExistsAsync(canonical, cancellationToken).ConfigureAwait(false))
        {
            throw new DirectoryNotFoundException($"Workspace folder not found: {canonical}");
        }

        CancelEnumerations();
        var node = new WorkspaceNode(Id(canonical), canonical, _paths.ToDisplayName(canonical), WorkspaceNodeKind.Directory);
        Update(current => current with { RootPath = canonical, Root = node, SelectedId = node.Id, ActivePath = null, Error = null });
        _watcher.Watch(canonical);
        await ExpandAsync(canonical, cancellationToken: cancellationToken).ConfigureAwait(false);
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        CancelEnumerations();
        _watcher.Watch(null);
        Update(current => current with { RootPath = null, Root = null, SelectedId = null, ActivePath = null, Error = null });
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ExpandAsync(string path, string? explicitlyVisiblePath = null, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot;
        if (snapshot.RootPath is null || snapshot.Root is null || !_paths.IsDescendantOrSelf(snapshot.RootPath, path))
        {
            return;
        }

        var canonical = _paths.Canonicalize(path);
        var currentNode = Find(snapshot.Root, Id(canonical));
        if (currentNode is null || !currentNode.CanExpand)
        {
            return;
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_enumerations.TryGetValue(currentNode.Id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
            IncrementCanceled();
        }
        _enumerations[currentNode.Id] = operation;

        Update(current => Replace(current, currentNode.Id, node => node with { IsExpanded = true, IsLoading = true, Error = null }));
        var watch = Stopwatch.StartNew();
        IncrementActive(1);
        try
        {
            var children = await _files.EnumerateAsync(
                snapshot.RootPath,
                canonical,
                _ignoredPaths,
                explicitlyVisiblePath,
                operation.Token).ConfigureAwait(false);

            if (!_enumerations.TryGetValue(currentNode.Id, out var latest) || !ReferenceEquals(latest, operation))
            {
                return;
            }

            Update(current =>
            {
                var existing = current.Root is null ? [] : Find(current.Root, currentNode.Id)?.Children ?? [];
                var existingById = existing.ToDictionary(static item => item.Id, StringComparer.Ordinal);
                var merged = children.Select(entry =>
                {
                    var entryId = Id(entry.Path);
                    existingById.TryGetValue(entryId, out var old);
                    return old is null
                        ? new WorkspaceNode(entryId, entry.Path, entry.Name, entry.Kind, IsDirectoryLink: entry.IsDirectoryLink)
                        : old with { Name = entry.Name, Kind = entry.Kind, IsDirectoryLink = entry.IsDirectoryLink, Error = null };
                }).ToArray();
                return Replace(current, currentNode.Id, node => node with
                {
                    IsExpanded = true,
                    IsLoading = false,
                    Children = merged,
                    Error = null,
                }, watch.Elapsed);
            });
            await PersistAsync(operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Update(current => Replace(current, currentNode.Id, node => node with { IsLoading = false, Error = exception.Message }));
            Report("novasharp.workspace.enumerate", $"Could not read {currentNode.Name}: {exception.Message}");
        }
        finally
        {
            IncrementActive(-1);
            if (_enumerations.TryRemove(new KeyValuePair<string, CancellationTokenSource>(currentNode.Id, operation)))
            {
                operation.Dispose();
            }
        }
    }

    public async Task CollapseAsync(string path, CancellationToken cancellationToken = default)
    {
        var id = Id(path);
        if (_enumerations.TryRemove(id, out var operation))
        {
            operation.Cancel();
            operation.Dispose();
            IncrementCanceled();
        }
        Update(current => Replace(current, id, node => node with { IsExpanded = false, IsLoading = false }));
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevealAsync(string path, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot;
        if (snapshot.RootPath is null || !_paths.IsDescendantOrSelf(snapshot.RootPath, path))
        {
            return;
        }

        var canonical = _paths.Canonicalize(path);
        var relative = _paths.ToWorkspaceRelativePath(snapshot.RootPath, canonical);
        var current = snapshot.RootPath;
        await ExpandAsync(current, canonical, cancellationToken).ConfigureAwait(false);
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1))
        {
            current = Path.Combine(current, segment);
            await ExpandAsync(current, canonical, cancellationToken).ConfigureAwait(false);
        }

        Update(current => current with { SelectedId = Id(canonical), ActivePath = canonical });
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SelectAsync(string path, CancellationToken cancellationToken = default)
    {
        Update(current => current with { SelectedId = Id(path) });
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetSidebarAsync(bool visible, int width, CancellationToken cancellationToken = default)
    {
        Update(current => current with { SidebarVisible = visible, SidebarWidth = Math.Clamp(width, 160, 520) });
        await PersistAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CreateAsync(string parent, string name, bool directory, CancellationToken cancellationToken = default)
    {
        return MutateAsync(async token =>
        {
            ValidateName(name);
            var target = await ResolveTargetAsync(parent, name, token).ConfigureAwait(false);
            await _files.CreateAsync(target, directory, token).ConfigureAwait(false);
            await ExpandAsync(parent, cancellationToken: token).ConfigureAwait(false);
            Update(current => current with { SelectedId = Id(target) });
        }, cancellationToken);
    }

    public Task RenameAsync(string path, string name, CancellationToken cancellationToken = default)
    {
        return MutateAsync(async token =>
        {
            ValidateName(name);
            var node = RequireNode(path);
            var parent = Path.GetDirectoryName(node.Path)!;
            var target = await ResolveTargetAsync(parent, name, token).ConfigureAwait(false);
            await _files.MoveAsync(node.Path, target, node.Kind == WorkspaceNodeKind.Directory || node.IsDirectoryLink, token).ConfigureAwait(false);
            if (Relocated is { } relocated)
            {
                await relocated(new WorkspaceRelocation(node.Path, target, node.Kind == WorkspaceNodeKind.Directory)).ConfigureAwait(false);
            }
            await ExpandAsync(parent, cancellationToken: token).ConfigureAwait(false);
            Update(current => current with { SelectedId = Id(target) });
        }, cancellationToken);
    }

    public Task MoveAsync(string path, string targetDirectory, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var node = RequireNode(path);
            var target = await ResolveTargetAsync(targetDirectory, node.Name, token).ConfigureAwait(false);
            if (node.Kind == WorkspaceNodeKind.Directory && _paths.IsDescendantOrSelf(node.Path, targetDirectory))
            {
                throw new IOException("A folder cannot be moved inside itself.");
            }
            await _files.MoveAsync(node.Path, target, node.Kind == WorkspaceNodeKind.Directory || node.IsDirectoryLink, token).ConfigureAwait(false);
            if (Relocated is { } relocated)
            {
                await relocated(new WorkspaceRelocation(node.Path, target, node.Kind == WorkspaceNodeKind.Directory)).ConfigureAwait(false);
            }
            await ExpandAsync(Path.GetDirectoryName(node.Path)!, cancellationToken: token).ConfigureAwait(false);
            await ExpandAsync(targetDirectory, cancellationToken: token).ConfigureAwait(false);
            Update(current => current with { SelectedId = Id(target) });
        }, cancellationToken);

    public Task DeleteAsync(string path, CancellationToken cancellationToken = default) =>
        MutateAsync(async token =>
        {
            var node = RequireNode(path);
            await _files.DeleteAsync(node.Path, node.Kind == WorkspaceNodeKind.Directory || node.IsDirectoryLink, token).ConfigureAwait(false);
            await ExpandAsync(Path.GetDirectoryName(node.Path)!, cancellationToken: token).ConfigureAwait(false);
        }, cancellationToken);

    private Task MutateAsync(Func<CancellationToken, Task> mutation, CancellationToken cancellationToken) =>
        _mutations.EnqueueAsync(async token =>
        {
            try
            {
                await mutation(token).ConfigureAwait(false);
                await PersistAsync(token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Report("novasharp.workspace.mutation", exception.Message);
                Update(current => current with { Error = exception.Message });
            }
            return true;
        }, cancellationToken);

    private async Task OnWatcherChangedAsync(WorkspaceChangeBatch batch)
    {
        var snapshot = Snapshot;
        if (snapshot.RootPath is null || snapshot.Root is null)
        {
            return;
        }

        var directories = new HashSet<string>(StringComparer.Ordinal);
        if (batch.Overflowed)
        {
            CollectExpanded(snapshot.Root, directories);
            Update(current => current with { Metrics = current.Metrics with { WatcherOverflows = current.Metrics.WatcherOverflows + 1 } });
            Report("novasharp.workspace.watcherOverflow", "Some filesystem changes arrived too quickly; expanded folders were rescanned.", NotificationSeverity.Warning);
        }
        else
        {
            foreach (var change in batch.Changes)
            {
                if (change.Kind == WorkspaceChangeKind.Renamed && change.OldPath is not null)
                {
                    Update(current => current with
                    {
                        SelectedId = current.SelectedId == Id(change.OldPath) ? Id(change.Path) : current.SelectedId,
                        ActivePath = current.ActivePath is not null && _paths.IsSamePath(current.ActivePath, change.OldPath)
                            ? change.Path
                            : current.ActivePath,
                    });
                }
                if (Path.GetDirectoryName(change.Path) is { } parent)
                {
                    directories.Add(parent);
                }
                if (change.OldPath is not null && Path.GetDirectoryName(change.OldPath) is { } oldParent)
                {
                    directories.Add(oldParent);
                }
            }
        }

        Update(current => current with { Metrics = current.Metrics with { PendingWatcherEvents = _watcher.PendingCount } });
        foreach (var directory in directories)
        {
            var node = Find(Snapshot.Root!, Id(directory));
            if (node?.IsExpanded == true)
            {
                await ExpandAsync(directory).ConfigureAwait(false);
            }
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var snapshot = Snapshot;
        var root = snapshot.RootPath;
        var expanded = new List<string>();
        if (root is not null && snapshot.Root is not null)
        {
            CollectExpanded(snapshot.Root, expanded, root);
        }

        await _persistence.SaveAsync(new WorkspaceStateDocument
        {
            WorkspacePath = root,
            ExpandedPaths = [.. expanded],
            SelectedPath = RelativeOptional(root, snapshot.SelectedId is null ? null : Find(snapshot.Root!, snapshot.SelectedId)?.Path),
            ActivePath = RelativeOptional(root, snapshot.ActivePath),
            SidebarVisible = snapshot.SidebarVisible,
            SidebarWidth = snapshot.SidebarWidth,
        }, cancellationToken).ConfigureAwait(false);
    }

    private WorkspaceNode RequireNode(string path)
    {
        var snapshot = Snapshot;
        if (snapshot.RootPath is null || !_paths.IsDescendantOrSelf(snapshot.RootPath, path))
        {
            throw new ArgumentException("The path is outside the workspace.", nameof(path));
        }
        return Find(snapshot.Root!, Id(path)) ?? throw new IOException("The item no longer exists in the Explorer.");
    }

    private async Task<string> ResolveTargetAsync(string parent, string name, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot;
        if (snapshot.RootPath is null || !_paths.IsDescendantOrSelf(snapshot.RootPath, parent))
        {
            throw new ArgumentException("The target is outside the workspace.", nameof(parent));
        }
        var target = _paths.Canonicalize(Path.Combine(parent, name));
        if (!_paths.IsDescendantOrSelf(snapshot.RootPath, target)
            || await _files.PathExistsAsync(target, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The target is invalid or already exists.");
        }
        return target;
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.GetFileName(name) != name)
        {
            throw new ArgumentException("Enter one valid file or folder name.", nameof(name));
        }
    }

    private string Id(string path) => _paths.ToDocumentUri(path).AbsoluteUri;

    private static WorkspaceNode? Find(WorkspaceNode node, string id)
    {
        if (node.Id == id) return node;
        if (node.Children is null) return null;
        foreach (var child in node.Children)
        {
            var found = Find(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    private WorkspaceSnapshot Replace(
        WorkspaceSnapshot snapshot,
        string id,
        Func<WorkspaceNode, WorkspaceNode> replace,
        TimeSpan? elapsed = null)
    {
        if (snapshot.Root is null) return snapshot;
        WorkspaceNode Visit(WorkspaceNode node)
        {
            if (node.Id == id) return replace(node);
            if (node.Children is null) return node;
            return node with { Children = node.Children.Select(Visit).ToArray() };
        }
        return snapshot with
        {
            Root = Visit(snapshot.Root),
            Metrics = elapsed is null ? snapshot.Metrics : snapshot.Metrics with { LastEnumerationDuration = elapsed.Value },
        };
    }

    private void Update(Func<WorkspaceSnapshot, WorkspaceSnapshot> update)
    {
        WorkspaceSnapshot snapshot;
        lock (_gate)
        {
            snapshot = update(_snapshot) with { Version = _snapshot.Version + 1 };
            _snapshot = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    private void IncrementActive(int delta)
    {
        Update(current => current with
        {
            Metrics = current.Metrics with
            {
                ActiveEnumerations = Math.Max(0, current.Metrics.ActiveEnumerations + delta),
            },
        });
    }

    private void IncrementCanceled()
    {
        Update(current => current with
        {
            Metrics = current.Metrics with { CanceledEnumerations = current.Metrics.CanceledEnumerations + 1 },
        });
    }

    private void Report(string id, string message, NotificationSeverity severity = NotificationSeverity.Error) =>
        _notifications.Raise(id, severity, message);

    private void CancelEnumerations()
    {
        foreach (var pair in _enumerations)
        {
            pair.Value.Cancel();
            pair.Value.Dispose();
        }
        _enumerations.Clear();
    }

    private static void CollectExpanded(WorkspaceNode node, ISet<string> paths)
    {
        if (node.IsExpanded) paths.Add(node.Path);
        if (node.Children is null) return;
        foreach (var child in node.Children) CollectExpanded(child, paths);
    }

    private void CollectExpanded(WorkspaceNode node, ICollection<string> paths, string root)
    {
        if (node.IsExpanded) paths.Add(_paths.ToWorkspaceRelativePath(root, node.Path));
        if (node.Children is null) return;
        foreach (var child in node.Children) CollectExpanded(child, paths, root);
    }

    private string? RelativeOptional(string? root, string? path) =>
        root is null || path is null || !_paths.IsDescendantOrSelf(root, path)
            ? null
            : _paths.ToWorkspaceRelativePath(root, path);

    private string? ResolveOptional(string root, string? relative)
    {
        if (relative is null) return null;
        try { return _paths.ResolveWorkspaceRelativePath(root, relative); }
        catch (ArgumentException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.Changed -= OnWatcherChangedAsync;
        CancelEnumerations();
        await _watcher.DisposeAsync().ConfigureAwait(false);
        await _mutations.DisposeAsync().ConfigureAwait(false);
    }
}
