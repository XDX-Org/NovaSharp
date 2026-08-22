using NovaSharp.Diagnostics;
using NovaSharp.Platform;
using NovaSharp.Workspace;

namespace NovaSharp.Editing;

public enum DocumentCloseKind
{
    One,
    Others,
    Right,
    Saved,
    All,
}

public sealed record DocumentTabSnapshot(
    string Id,
    Uri DocumentUri,
    string Path,
    string Label,
    string AccessibleLabel,
    bool IsActive,
    bool IsPreview,
    bool IsPinned,
    bool IsDirty,
    bool IsReadOnly,
    bool IsMissing,
    string GroupId = "main");

public sealed record DocumentTabsSnapshot(
    IReadOnlyList<DocumentTabSnapshot> Tabs,
    string? ActiveId = null,
    long Version = 0)
{
    public DocumentTabSnapshot? ActiveTab => Tabs.FirstOrDefault(tab => tab.Id == ActiveId);
    public IReadOnlyList<DocumentTabSnapshot> DirtyTabs => Tabs.Where(tab => tab.IsDirty).ToArray();
}

/// <summary>Owns open document records and ordered editor views; tabs only reference these records.</summary>
public sealed class DocumentRegistry : IAsyncDisposable
{
    private sealed class Entry
    {
        public required string Id { get; set; }
        public required Uri Uri { get; set; }
        public required string Path { get; set; }
        public DocumentSession? Session { get; set; }
        public DocumentStatus Status { get; set; } = new();
        public bool IsPreview { get; set; }
        public bool IsPinned { get; set; }
        public bool IsMissing { get; set; }
        public bool HasModel { get; set; }
        public string Label { get; set; } = string.Empty;
        public EditorViewState? ViewState { get; set; }
        public Func<DocumentStatus, Task>? StatusHandler { get; set; }
    }

    private readonly IEditorHost _host;
    private readonly IWorkspacePaths _paths;
    private readonly WorkspacePersistenceService _persistence;
    private readonly Func<DocumentSession> _createSession;
    private readonly INotificationService _notifications;
    private readonly Func<string?> _workspaceRoot;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly SemaphoreSlim _persistenceWriter = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private string? _activeId;
    private long _version;
    private int _disposed;

    public DocumentRegistry(
        IEditorHost host,
        IWorkspacePaths paths,
        WorkspacePersistenceService persistence,
        Func<DocumentSession> createSession,
        INotificationService notifications,
        Func<string?>? workspaceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(createSession);
        ArgumentNullException.ThrowIfNull(notifications);
        _host = host;
        _paths = paths;
        _persistence = persistence;
        _createSession = createSession;
        _notifications = notifications;
        _workspaceRoot = workspaceRoot ?? (static () => null);
    }

    public event Action<DocumentTabsSnapshot>? Changed;

    public DocumentTabsSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshot();
            }
        }
    }

    public DocumentSession? ActiveDocument
    {
        get
        {
            lock (_gate)
            {
                return _entries.FirstOrDefault(entry => entry.Id == _activeId)?.Session;
            }
        }
    }

    public bool Replicate(IReadOnlyList<TextEditBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0) return true;

        DocumentSession? session;
        lock (_gate)
        {
            session = FindByUri(batches[0].DocumentUri)?.Session;
        }

        return session?.Replicate(batches) == true;
    }

    public void RequestResync(string? documentUri)
    {
        DocumentSession? session;
        lock (_gate)
        {
            session = documentUri is null
                ? _entries.FirstOrDefault(entry => entry.Id == _activeId)?.Session
                : FindByUri(documentUri)?.Session;
        }
        session?.RequestResync();
    }

    public Task OpenPinnedAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, preview: false, cancellationToken);

    public Task OpenPreviewAsync(string path, CancellationToken cancellationToken = default) =>
        OpenAsync(path, preview: true, cancellationToken);

    public async Task OpenAsync(string path, bool preview, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = _paths.Canonicalize(path);
        var uri = _paths.ToDocumentUri(canonicalPath);

        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry? existing;
            lock (_gate)
            {
                existing = FindByUri(uri.AbsoluteUri);
                if (existing is not null && !preview)
                {
                    existing.IsPreview = false;
                    existing.IsPinned = true;
                }
            }

            if (existing is not null)
            {
                await ActivateCoreAsync(existing, cancellationToken).ConfigureAwait(false);
                Publish();
                QueuePersist();
                return;
            }

            Entry? replacedPreview;
            lock (_gate)
            {
                replacedPreview = preview
                    ? _entries.FirstOrDefault(entry => entry.IsPreview && !entry.Status.IsDirty)
                    : null;
            }
            if (replacedPreview is not null)
            {
                await CloseEntryAsync(replacedPreview, cancellationToken).ConfigureAwait(false);
            }

            var entry = CreateEntry(canonicalPath, uri, preview, pinned: !preview);
            lock (_gate)
            {
                _entries.Add(entry);
                _activeId = entry.Id;
                RecomputeLabels();
            }

            await entry.Session!.OpenAsync(canonicalPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                entry.Status = entry.Session.Status;
                entry.HasModel = entry.Status.IsOpen;
                entry.IsMissing = !entry.Status.IsOpen || entry.Status.ExternalChange == ExternalChangeState.Deleted;
            }
            if (!entry.HasModel)
            {
                await _host.ClearDocumentAsync(cancellationToken).ConfigureAwait(false);
                entry.Session.StatusChanged -= entry.StatusHandler;
                await entry.Session.DisposeAsync().ConfigureAwait(false);
                entry.Session = null;
            }

            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task ActivateAsync(string id, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Entry? entry;
            lock (_gate)
            {
                entry = _entries.FirstOrDefault(candidate => candidate.Id == id);
            }
            if (entry is null) return;
            await ActivateCoreAsync(entry, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task MoveAsync(string id, int targetIndex, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                var current = _entries.FindIndex(entry => entry.Id == id);
                if (current < 0) return;
                var entry = _entries[current];
                _entries.RemoveAt(current);
                _entries.Insert(Math.Clamp(targetIndex, 0, _entries.Count), entry);
            }
            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public Task MoveRelativeAsync(string id, int offset, CancellationToken cancellationToken = default)
    {
        int index;
        lock (_gate)
        {
            index = _entries.FindIndex(entry => entry.Id == id);
        }
        return index < 0 ? Task.CompletedTask : MoveAsync(id, index + offset, cancellationToken);
    }

    public IReadOnlyList<DocumentTabSnapshot> GetCloseCandidates(DocumentCloseKind kind, string? id = null)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(entry => entry.Id == (id ?? _activeId));
            IEnumerable<Entry> selected = kind switch
            {
                DocumentCloseKind.One => index < 0 ? [] : [_entries[index]],
                DocumentCloseKind.Others => index < 0 ? [] : _entries.Where((_, candidate) => candidate != index),
                DocumentCloseKind.Right => index < 0 ? [] : _entries.Skip(index + 1),
                DocumentCloseKind.Saved => _entries.Where(entry => !entry.Status.IsDirty),
                DocumentCloseKind.All => _entries,
                _ => [],
            };
            var ids = selected.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
            return BuildSnapshot().Tabs.Where(tab => ids.Contains(tab.Id)).ToArray();
        }
    }

    public async Task CloseAsync(
        IReadOnlyCollection<string> ids,
        bool discardDirty,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<Entry> closing;
            lock (_gate)
            {
                var selected = ids.ToHashSet(StringComparer.Ordinal);
                closing = _entries.Where(entry => selected.Contains(entry.Id)).ToList();
                if (!discardDirty && closing.Any(entry => entry.Status.IsDirty)) return;
            }

            foreach (var entry in closing)
            {
                await CloseEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            Entry? next;
            lock (_gate)
            {
                next = _entries.FirstOrDefault(entry => entry.Id == _activeId) ?? _entries.LastOrDefault();
                _activeId = next?.Id;
                RecomputeLabels();
            }
            if (next is null || !next.HasModel)
            {
                await _host.ClearDocumentAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _host.SwitchDocumentAsync(next.Uri, next.ViewState, cancellationToken).ConfigureAwait(false);
            }

            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task PromoteAsync(string id, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_entries.FirstOrDefault(entry => entry.Id == id) is not { } entry) return;
                entry.IsPreview = false;
                entry.IsPinned = true;
            }
            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task RelocateAsync(WorkspaceRelocation relocation, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<(Entry Entry, string OldPath, string NewPath)> relocations = [];
            lock (_gate)
            {
                foreach (var entry in _entries)
                {
                    string? target = null;
                    if (_paths.IsSamePath(relocation.OldPath, entry.Path)) target = relocation.NewPath;
                    else if (relocation.IsDirectory && _paths.IsDescendantOrSelf(relocation.OldPath, entry.Path))
                    {
                        var relative = _paths.ToWorkspaceRelativePath(relocation.OldPath, entry.Path);
                        target = _paths.ResolveWorkspaceRelativePath(relocation.NewPath, relative);
                    }
                    if (target is not null) relocations.Add((entry, entry.Path, target));
                }
            }

            foreach (var item in relocations)
            {
                var uri = _paths.ToDocumentUri(item.NewPath);
                if (item.Entry.Session is not null)
                    await item.Entry.Session.RelocateAsync(
                        item.OldPath,
                        item.NewPath,
                        uri,
                        cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    var oldId = item.Entry.Id;
                    item.Entry.Path = item.NewPath;
                    item.Entry.Uri = uri;
                    item.Entry.Id = uri.AbsoluteUri;
                    if (_activeId == oldId) _activeId = item.Entry.Id;
                }
            }
            lock (_gate) RecomputeLabels();
            Publish();
            QueuePersist();
        }
        finally
        {
            _mutations.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RestoreCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _mutations.Release();
        }
    }

    private async Task RestoreCoreAsync(CancellationToken cancellationToken)
    {
        var loaded = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.Problem is not null)
        {
            _notifications.Raise(
                "novasharp.documents.restore",
                NotificationSeverity.Warning,
                loaded.Problem);
        }

        var state = loaded.State;
        var restored = new List<(Entry Entry, PersistedDocumentView State)>();
        foreach (var saved in state.OpenDocuments)
        {
            if (saved is null) continue;
            try
            {
                var path = saved.WorkspaceRelative && state.WorkspacePath is not null
                    ? _paths.ResolveWorkspaceRelativePath(state.WorkspacePath, saved.Path)
                    : _paths.Canonicalize(saved.Path);
                var uri = _paths.ToDocumentUri(path);
                if (restored.Any(item => _paths.IsSameDocument(item.Entry.Uri, uri))) continue;
                var entry = CreateEntry(path, uri, saved.IsPreview, saved.IsPinned);
                entry.ViewState = saved.ViewState;
                restored.Add((entry, saved));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }
        if (restored.Count == 0) return;

        var previewRetained = false;
        for (var index = restored.Count - 1; index >= 0; index--)
        {
            if (!restored[index].Entry.IsPreview) continue;
            if (!previewRetained) { previewRetained = true; continue; }
            restored[index].Entry.IsPreview = false;
            restored[index].Entry.IsPinned = true;
        }

        lock (_gate)
        {
            _entries.AddRange(restored.Select(item => item.Entry));
            var active = restored.FirstOrDefault(item => item.State.Id == state.ActiveDocumentId).Entry
                ?? restored[0].Entry;
            _activeId = active.Id;
            RecomputeLabels();
        }
        Publish();

        var activeEntry = restored.First(item => item.Entry.Id == _activeId).Entry;
        var loads = new List<Task> { RestoreEntryAsync(activeEntry, cancellationToken) };
        loads.AddRange(restored.Where(item => item.Entry != activeEntry)
            .Select(item => RestoreEntryAsync(item.Entry, cancellationToken)));
        await Task.WhenAll(loads).ConfigureAwait(false);

        if (!activeEntry.HasModel)
            await _host.ClearDocumentAsync(cancellationToken).ConfigureAwait(false);
        else
            await _host.SwitchDocumentAsync(activeEntry.Uri, activeEntry.ViewState, cancellationToken).ConfigureAwait(false);

        Publish();
    }

    private Entry CreateEntry(string path, Uri uri, bool preview, bool pinned)
    {
        var session = _createSession();
        var entry = new Entry
        {
            Id = uri.AbsoluteUri,
            Uri = uri,
            Path = path,
            Session = session,
            IsPreview = preview,
            IsPinned = !preview && pinned,
            Label = _paths.ToDisplayName(path),
        };
        entry.StatusHandler = status => OnStatusChangedAsync(entry, status);
        session.StatusChanged += entry.StatusHandler;
        return entry;
    }

    private async Task RestoreEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        var foreground = entry.Id == _activeId;
        await entry.Session!.OpenAsync(
            entry.Path,
            foreground: foreground,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            entry.Status = entry.Session.Status;
            entry.HasModel = entry.Status.IsOpen;
            entry.IsMissing = !entry.Status.IsOpen || entry.Status.ExternalChange == ExternalChangeState.Deleted;
        }
        if (!entry.HasModel)
        {
            entry.Session.StatusChanged -= entry.StatusHandler;
            await entry.Session.DisposeAsync().ConfigureAwait(false);
            entry.Session = null;
        }
    }

    private Task OnStatusChangedAsync(Entry entry, DocumentStatus status)
    {
        var promoted = false;
        lock (_gate)
        {
            entry.Status = status;
            entry.HasModel = status.IsOpen;
            entry.IsMissing = !status.IsBusy
                && (!status.IsOpen || status.ExternalChange == ExternalChangeState.Deleted);
            if (status.Path is { } path)
            {
                entry.Path = path;
                if (entry.Session?.DocumentUri is { } uri)
                {
                    var oldId = entry.Id;
                    entry.Uri = uri;
                    entry.Id = uri.AbsoluteUri;
                    if (_activeId == oldId) _activeId = entry.Id;
                }
            }
            if (status.IsDirty && entry.IsPreview)
            {
                entry.IsPreview = false;
                entry.IsPinned = true;
                promoted = true;
            }
            RecomputeLabels();
        }
        Publish();
        if (promoted) QueuePersist();
        return Task.CompletedTask;
    }

    private async Task ActivateCoreAsync(Entry entry, CancellationToken cancellationToken)
    {
        Entry? previous;
        lock (_gate)
        {
            previous = _entries.FirstOrDefault(candidate => candidate.Id == _activeId);
        }
        if (previous is not null && previous != entry && previous.HasModel)
            previous.ViewState = await _host.GetViewStateAsync(previous.Uri, cancellationToken).ConfigureAwait(false);

        if (!entry.HasModel)
            await _host.ClearDocumentAsync(cancellationToken).ConfigureAwait(false);
        else
            await _host.SwitchDocumentAsync(entry.Uri, entry.ViewState, cancellationToken).ConfigureAwait(false);
        lock (_gate) _activeId = entry.Id;
    }

    private async Task CloseEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        if (entry.HasModel)
        {
            entry.ViewState = await _host.GetViewStateAsync(entry.Uri, cancellationToken).ConfigureAwait(false);
            await _host.CloseDocumentAsync(entry.Uri, cancellationToken).ConfigureAwait(false);
        }
        if (entry.Session is not null)
        {
            entry.Session.StatusChanged -= entry.StatusHandler;
            await entry.Session.DisposeAsync().ConfigureAwait(false);
            entry.Session = null;
        }
        lock (_gate)
        {
            var index = _entries.IndexOf(entry);
            if (index < 0) return;
            _entries.RemoveAt(index);
            if (_activeId == entry.Id)
                _activeId = _entries.ElementAtOrDefault(Math.Min(index, _entries.Count - 1))?.Id;
        }
    }

    private Entry? FindByUri(string uri) =>
        _entries.FirstOrDefault(entry => string.Equals(entry.Uri.AbsoluteUri, uri, StringComparison.Ordinal));

    private void RecomputeLabels()
    {
        foreach (var group in _entries.GroupBy(entry => _paths.ToDisplayName(entry.Path), StringComparer.Ordinal))
        {
            if (group.Count() == 1)
            {
                group.First().Label = group.Key;
                continue;
            }

            var entries = group.ToArray();
            string[]? previousLabels = null;
            for (var depth = 1; ; depth++)
            {
                var labels = entries.Select(entry => ParentSuffix(entry.Path, depth)).ToArray();
                if (labels.Distinct(StringComparer.Ordinal).Count() == entries.Length)
                {
                    for (var index = 0; index < entries.Length; index++)
                        entries[index].Label = $"{group.Key} — {labels[index]}";
                    break;
                }
                if (previousLabels is null || !labels.SequenceEqual(previousLabels, StringComparer.Ordinal))
                {
                    previousLabels = labels;
                    continue;
                }

                labels = entries.Select(entry => Path.GetDirectoryName(entry.Path) ?? entry.Path).ToArray();
                for (var index = 0; index < entries.Length; index++)
                    entries[index].Label = $"{group.Key} — {labels[index]}";
                break;
            }
        }
    }

    private static string ParentSuffix(string path, int depth)
    {
        var parents = new List<string>();
        var current = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current) && parents.Count < depth)
        {
            parents.Add(Path.GetFileName(current));
            current = Path.GetDirectoryName(current);
        }
        parents.Reverse();
        return string.Join(" / ", parents);
    }

    private DocumentTabsSnapshot BuildSnapshot()
    {
        var tabs = _entries.Select(entry =>
        {
            var states = new List<string>();
            if (entry.Id == _activeId) states.Add("active");
            if (entry.Status.IsDirty) states.Add("unsaved");
            if (entry.IsPreview) states.Add("preview");
            if (entry.IsPinned) states.Add("pinned");
            if (entry.Status.IsReadOnly) states.Add("read-only");
            if (entry.IsMissing) states.Add("missing file");
            var suffix = states.Count == 0 ? string.Empty : $", {string.Join(", ", states)}";
            return new DocumentTabSnapshot(
                entry.Id,
                entry.Uri,
                entry.Path,
                entry.Label,
                $"{entry.Label}{suffix}",
                entry.Id == _activeId,
                entry.IsPreview,
                entry.IsPinned,
                entry.Status.IsDirty,
                entry.Status.IsReadOnly,
                entry.IsMissing);
        }).ToArray();
        return new DocumentTabsSnapshot(tabs, _activeId, _version);
    }

    private void Publish()
    {
        DocumentTabsSnapshot snapshot;
        lock (_gate)
        {
            _version++;
            snapshot = BuildSnapshot();
        }
        Changed?.Invoke(snapshot);
    }

    private void QueuePersist() => _ = PersistLatestAsync();

    private async Task PersistLatestAsync()
    {
        var entered = false;
        try
        {
            await _persistenceWriter.WaitAsync().ConfigureAwait(false);
            entered = true;
            DocumentTabsSnapshot snapshot;
            List<(DocumentTabSnapshot Tab, EditorViewState? View)> views;
            lock (_gate)
            {
                snapshot = BuildSnapshot();
                views = _entries.Select(entry =>
                    (snapshot.Tabs.First(tab => tab.Id == entry.Id), entry.ViewState)).ToList();
            }
            var root = _workspaceRoot();
            await _persistence.UpdateAsync(state => state with
            {
                OpenDocuments = views.Select(item =>
                {
                    var relative = root is not null && _paths.IsDescendantOrSelf(root, item.Tab.Path);
                    return new PersistedDocumentView(
                        item.Tab.Id,
                        relative ? _paths.ToWorkspaceRelativePath(root!, item.Tab.Path) : item.Tab.Path,
                        relative,
                        item.Tab.IsPreview,
                        item.Tab.IsPinned,
                        item.View);
                }).ToArray(),
                ActiveDocumentId = snapshot.ActiveId,
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _notifications.Raise(
                "novasharp.documents.persist",
                NotificationSeverity.Warning,
                $"Open editors could not be saved for the next session: {exception.Message}");
        }
        finally
        {
            if (entered) _persistenceWriter.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            Entry? active;
            lock (_gate) active = _entries.FirstOrDefault(entry => entry.Id == _activeId);
            if (active is not null && active.HasModel)
                active.ViewState = await _host.GetViewStateAsync(active.Uri, CancellationToken.None).ConfigureAwait(false);
            await PersistLatestAsync().ConfigureAwait(false);

            Entry[] entries;
            lock (_gate) entries = [.. _entries];
            foreach (var entry in entries)
                await CloseEntryAsync(entry, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _mutations.Release();
            _mutations.Dispose();
            _persistenceWriter.Dispose();
        }
    }
}
