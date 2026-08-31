using Microsoft.AspNetCore.Components;
using NovaSharp.Diagnostics;
using NovaSharp.Workspace;

namespace NovaSharp.Editing;

public enum EditorSplitOrientation { Horizontal, Vertical }
public enum EditorSplitDirection { Left, Right, Up, Down }

public abstract record EditorLayoutNodeSnapshot(string Id);
public sealed record EditorGroupNodeSnapshot(string GroupId) : EditorLayoutNodeSnapshot(GroupId);
public sealed record EditorSplitNodeSnapshot(
    string Id,
    EditorSplitOrientation Orientation,
    double Ratio,
    EditorLayoutNodeSnapshot First,
    EditorLayoutNodeSnapshot Second) : EditorLayoutNodeSnapshot(Id);

public sealed record EditorGroupTabSnapshot(
    string ViewId,
    string DocumentId,
    Uri DocumentUri,
    string Path,
    string Label,
    string AccessibleLabel,
    bool IsActive,
    bool IsPreview,
    bool IsPinned,
    bool IsDirty,
    bool IsReadOnly,
    bool IsMissing);

public sealed record EditorGroupSnapshot(
    string Id,
    IReadOnlyList<EditorGroupTabSnapshot> Tabs,
    string? ActiveViewId,
    bool IsActive);

public sealed record EditorGroupsSnapshot(
    EditorLayoutNodeSnapshot Layout,
    IReadOnlyDictionary<string, EditorGroupSnapshot> Groups,
    string ActiveGroupId,
    long Version = 0)
{
    public EditorGroupSnapshot ActiveGroup => Groups[ActiveGroupId];
    public EditorGroupTabSnapshot? ActiveTab => ActiveGroup.Tabs.FirstOrDefault(tab => tab.IsActive);
}

/// <summary>Owns editor views and the bounded split tree; documents remain owned by <see cref="DocumentRegistry"/>.</summary>
public sealed class EditorGroupManager : IAsyncDisposable
{
    public const string MainGroupId = "main";
    public const int MaximumLayoutDepth = 4;
    public const int MaximumGroups = 16;
    public const int MaximumViews = 256;

    private sealed class ViewState
    {
        public required string Id { get; init; }
        public required string DocumentId { get; set; }
        public EditorViewState? State { get; set; }
    }

    private sealed class GroupState
    {
        public required string Id { get; init; }
        public List<ViewState> Views { get; } = [];
        public string? ActiveViewId { get; set; }
        public bool IsMounted { get; set; }
    }

    private readonly IEditorHost _host;
    private readonly DocumentRegistry _documents;
    private readonly WorkspacePersistenceService _persistence;
    private readonly INotificationService _notifications;
    private readonly SemaphoreSlim _mutations = new(1, 1);
    private readonly Lock _persistenceGate = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, GroupState> _groups = new(StringComparer.Ordinal);
    private EditorLayoutNodeSnapshot _layout = new EditorGroupNodeSnapshot(MainGroupId);
    private string _activeGroupId = MainGroupId;
    private Task _persistenceTask = Task.CompletedTask;
    private bool _persistAgain;
    private long _version;
    private int _disposed;

    public EditorGroupManager(
        IEditorHost host,
        DocumentRegistry documents,
        WorkspacePersistenceService persistence,
        INotificationService notifications)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(persistence);
        ArgumentNullException.ThrowIfNull(notifications);
        _host = host;
        _documents = documents;
        _persistence = persistence;
        _notifications = notifications;
        _groups.Add(MainGroupId, new GroupState { Id = MainGroupId, IsMounted = true });
        _documents.Changed += OnDocumentsChanged;
    }

    public event Action<EditorGroupsSnapshot>? Changed;

    public EditorGroupsSnapshot Snapshot
    {
        get { lock (_gate) return BuildSnapshot(); }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await _persistence.LoadAsync(cancellationToken).ConfigureAwait(false);
            var documents = _documents.Snapshot;
            var restored = TryRestore(loaded.State.EditorLayout, documents);
            lock (_gate)
            {
                if (!restored) CreateDefaultLayout(documents);
                EnsureEveryDocumentHasAView(documents);
            }
            GroupState? primary;
            lock (_gate) primary = _activeGroupId == MainGroupId ? null : _groups.GetValueOrDefault(MainGroupId);
            if (primary is not null) await AttachGroupAsync(primary, focus: false, cancellationToken).ConfigureAwait(false);
            await ActivateCurrentAsync(focus: false, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task RegisterGroupAsync(
        string groupId,
        ElementReference container,
        CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? group;
            bool focus;
            lock (_gate)
            {
                _groups.TryGetValue(groupId, out group);
                if (group is null) return;
                group.IsMounted = true;
                focus = group.Id == _activeGroupId;
            }
            await _host.CreateViewAsync(groupId, container, cancellationToken).ConfigureAwait(false);
            ViewState? active;
            lock (_gate) active = focus ? ActiveView(group) : null;
            if (active is not null)
            {
                await _host.SetActiveViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
                if (_documents.Snapshot.ActiveId != active.DocumentId)
                    await _documents.ActivateAsync(active.DocumentId, cancellationToken).ConfigureAwait(false);
            }
            await AttachGroupAsync(group, focus, cancellationToken).ConfigureAwait(false);
        }
        finally { _mutations.Release(); }
    }

    public async Task OpenPinnedAsync(string path, CancellationToken cancellationToken = default) =>
        await OpenAsync(path, preview: false, targetGroupId: null, int.MaxValue, cancellationToken).ConfigureAwait(false);

    public async Task OpenPreviewAsync(string path, CancellationToken cancellationToken = default) =>
        await OpenAsync(path, preview: true, targetGroupId: null, int.MaxValue, cancellationToken).ConfigureAwait(false);

    public Task OpenPinnedInGroupAsync(
        string path,
        string targetGroupId,
        int targetIndex,
        CancellationToken cancellationToken = default) =>
        OpenAsync(path, preview: false, targetGroupId, targetIndex, cancellationToken);

    private async Task OpenAsync(
        string path,
        bool preview,
        string? targetGroupId,
        int targetIndex,
        CancellationToken cancellationToken)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState current;
            GroupState? target;
            lock (_gate)
            {
                current = _groups[_activeGroupId];
                target = targetGroupId is null ? current : _groups.GetValueOrDefault(targetGroupId);
            }
            if (target is null) return;
            await CaptureActiveViewAsync(current, cancellationToken).ConfigureAwait(false);
            if (target != current) await CaptureActiveViewAsync(target, cancellationToken).ConfigureAwait(false);
            lock (_gate) _activeGroupId = target.Id;
            await _host.SetActiveViewAsync(target.Id, cancellationToken).ConfigureAwait(false);
            if (preview) await _documents.OpenPreviewAsync(path, cancellationToken).ConfigureAwait(false);
            else await _documents.OpenPinnedAsync(path, cancellationToken).ConfigureAwait(false);
            var documentId = _documents.Snapshot.ActiveId;
            if (documentId is null) return;
            lock (_gate)
            {
                var view = target.Views.FirstOrDefault(candidate => candidate.DocumentId == documentId)
                    ?? AddView(target, documentId, targetIndex);
                if (targetGroupId is not null) RepositionView(target, view, targetIndex);
                target.ActiveViewId = view.Id;
                _activeGroupId = target.Id;
            }
            await ActivateCurrentAsync(focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task OpenPinnedAtEdgeAsync(
        string path,
        string targetGroupId,
        EditorSplitDirection direction,
        CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState current;
            GroupState? target;
            HashSet<string> viewedDocuments;
            lock (_gate)
            {
                current = _groups[_activeGroupId];
                target = _groups.GetValueOrDefault(targetGroupId);
                viewedDocuments = _groups.Values.SelectMany(group => group.Views)
                    .Select(view => view.DocumentId).ToHashSet(StringComparer.Ordinal);
                if (target is null) return;
                if (_groups.Count >= MaximumGroups || DepthOf(_layout, target.Id) >= MaximumLayoutDepth
                    || TotalViews() >= MaximumViews)
                {
                    _notifications.Raise("novasharp.groups.limit", NotificationSeverity.Warning,
                        $"Editor groups are limited to {MaximumGroups} groups, {MaximumLayoutDepth} nested splits, and {MaximumViews} views.");
                    return;
                }
            }

            await CaptureActiveViewAsync(current, cancellationToken).ConfigureAwait(false);
            if (target != current) await CaptureActiveViewAsync(target, cancellationToken).ConfigureAwait(false);
            lock (_gate) _activeGroupId = target.Id;
            await _host.SetActiveViewAsync(target.Id, cancellationToken).ConfigureAwait(false);
            await _documents.OpenPinnedAsync(path, cancellationToken).ConfigureAwait(false);
            var documentId = _documents.Snapshot.ActiveId;
            if (documentId is null) return;

            lock (_gate)
            {
                target = _groups[targetGroupId];
                var seed = _groups.Values.SelectMany(group => group.Views)
                    .FirstOrDefault(view => view.DocumentId == documentId);
                ViewState added;
                if (!viewedDocuments.Contains(documentId) && seed is not null && target.Views.Remove(seed))
                {
                    if (target.ActiveViewId == seed.Id) target.ActiveViewId = target.Views.FirstOrDefault()?.Id;
                    added = seed;
                }
                else
                {
                    added = new ViewState { Id = NextId("view"), DocumentId = documentId, State = seed?.State };
                }

                var groupId = NextId("group");
                var group = new GroupState { Id = groupId, ActiveViewId = added.Id };
                group.Views.Add(added);
                _groups.Add(groupId, group);
                var newFirst = direction is EditorSplitDirection.Left or EditorSplitDirection.Up;
                var orientation = direction is EditorSplitDirection.Left or EditorSplitDirection.Right
                    ? EditorSplitOrientation.Horizontal : EditorSplitOrientation.Vertical;
                var existing = new EditorGroupNodeSnapshot(target.Id);
                var newGroup = new EditorGroupNodeSnapshot(groupId);
                _layout = ReplaceGroup(_layout, target.Id, new EditorSplitNodeSnapshot(
                    NextId("split"), orientation, 0.5, newFirst ? newGroup : existing, newFirst ? existing : newGroup));
                _activeGroupId = groupId;
            }
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task ActivateAsync(string groupId, string viewId, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? group;
            ViewState? view;
            lock (_gate)
            {
                group = _groups.GetValueOrDefault(groupId);
                view = group?.Views.FirstOrDefault(candidate => candidate.Id == viewId);
            }
            if (group is null || view is null) return;
            await CaptureActiveViewAsync(group, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                group.ActiveViewId = view.Id;
                _activeGroupId = group.Id;
            }
            await _host.SetActiveViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
            await _documents.ActivateAsync(view.DocumentId, cancellationToken).ConfigureAwait(false);
            await AttachGroupAsync(group, focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task FocusAsync(string groupId, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? group;
            ViewState? view;
            lock (_gate)
            {
                group = _groups.GetValueOrDefault(groupId);
                view = group is null ? null : ActiveView(group);
            }
            if (group is null || view is null || !group.IsMounted) return;

            lock (_gate) _activeGroupId = group.Id;
            await _host.SetActiveViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
            if (_documents.Snapshot.ActiveId != view.DocumentId)
                await _documents.ActivateAsync(view.DocumentId, cancellationToken).ConfigureAwait(false);
            await AttachGroupAsync(group, focus: false, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task SplitAsync(EditorSplitDirection direction, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState source;
            ViewState? active;
            lock (_gate)
            {
                source = _groups[_activeGroupId];
                active = ActiveView(source);
            }
            if (active is null) return;
            await CaptureActiveViewAsync(source, cancellationToken).ConfigureAwait(false);
            string groupId;
            lock (_gate)
            {
                if (_groups.Count >= MaximumGroups || DepthOf(_layout, source.Id) >= MaximumLayoutDepth)
                {
                    _notifications.Raise("novasharp.groups.limit", NotificationSeverity.Warning,
                        $"Editor groups are limited to {MaximumGroups} groups and {MaximumLayoutDepth} nested splits.");
                    return;
                }
                groupId = NextId("group");
                var group = new GroupState { Id = groupId };
                if (active is not null)
                {
                    var copy = AddView(group, active.DocumentId);
                    copy.State = active.State;
                    group.ActiveViewId = copy.Id;
                }
                _groups.Add(groupId, group);
                var newFirst = direction is EditorSplitDirection.Left or EditorSplitDirection.Up;
                var orientation = direction is EditorSplitDirection.Left or EditorSplitDirection.Right
                    ? EditorSplitOrientation.Horizontal
                    : EditorSplitOrientation.Vertical;
                var existing = new EditorGroupNodeSnapshot(source.Id);
                var added = new EditorGroupNodeSnapshot(groupId);
                _layout = ReplaceGroup(_layout, source.Id, new EditorSplitNodeSnapshot(
                    NextId("split"), orientation, 0.5, newFirst ? added : existing, newFirst ? existing : added));
                _activeGroupId = groupId;
            }
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public Task MoveViewAsync(
        string viewId,
        string targetGroupId,
        int targetIndex,
        CancellationToken cancellationToken = default) =>
        TransferViewAsync(viewId, targetGroupId, targetIndex, copy: false, cancellationToken);

    public Task CopyViewAsync(
        string viewId,
        string targetGroupId,
        int targetIndex,
        CancellationToken cancellationToken = default) =>
        TransferViewAsync(viewId, targetGroupId, targetIndex, copy: true, cancellationToken);

    public int ViewCount(string documentId)
    {
        lock (_gate) return _groups.Values.Sum(group => group.Views.Count(view => view.DocumentId == documentId));
    }

    public Task TransferActiveToRelativeGroupAsync(bool copy, CancellationToken cancellationToken = default)
    {
        string? viewId;
        string? target;
        lock (_gate)
        {
            var leaves = LeafIds(_layout).ToArray();
            if (leaves.Length < 2) return Task.CompletedTask;
            var index = Array.IndexOf(leaves, _activeGroupId);
            target = leaves[(index + 1) % leaves.Length];
            viewId = ActiveView(_groups[_activeGroupId])?.Id;
        }
        return viewId is null ? Task.CompletedTask
            : TransferViewAsync(viewId, target!, int.MaxValue, copy, cancellationToken);
    }

    public Task CloseActiveGroupAsync(bool discardDirty, CancellationToken cancellationToken = default)
    {
        string groupId;
        lock (_gate) groupId = _activeGroupId;
        return CloseGroupAsync(groupId, discardDirty, cancellationToken);
    }

    public async Task SplitViewAsync(
        string viewId,
        string targetGroupId,
        EditorSplitDirection direction,
        bool copy,
        CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? source;
            GroupState? target;
            ViewState? sourceView;
            lock (_gate)
            {
                source = _groups.Values.FirstOrDefault(group => group.Views.Any(view => view.Id == viewId));
                target = _groups.GetValueOrDefault(targetGroupId);
                sourceView = source?.Views.FirstOrDefault(view => view.Id == viewId);
            }
            if (source is null || target is null || sourceView is null) return;
            await CaptureActiveViewAsync(source, cancellationToken).ConfigureAwait(false);
            string? removedGroup = null;
            lock (_gate)
            {
                if (_groups.Count >= MaximumGroups || DepthOf(_layout, target.Id) >= MaximumLayoutDepth) return;
                var newGroupId = NextId("group");
                var newGroup = new GroupState { Id = newGroupId };
                var newView = new ViewState
                {
                    Id = copy ? NextId("view") : sourceView.Id,
                    DocumentId = sourceView.DocumentId,
                    State = sourceView.State,
                };
                newGroup.Views.Add(newView);
                newGroup.ActiveViewId = newView.Id;
                _groups.Add(newGroupId, newGroup);
                if (!copy)
                {
                    source.Views.Remove(sourceView);
                    source.ActiveViewId = source.Views.FirstOrDefault()?.Id;
                }
                var newFirst = direction is EditorSplitDirection.Left or EditorSplitDirection.Up;
                var orientation = direction is EditorSplitDirection.Left or EditorSplitDirection.Right
                    ? EditorSplitOrientation.Horizontal : EditorSplitOrientation.Vertical;
                var existing = new EditorGroupNodeSnapshot(target.Id);
                var added = new EditorGroupNodeSnapshot(newGroupId);
                _layout = ReplaceGroup(_layout, target.Id, new EditorSplitNodeSnapshot(
                    NextId("split"), orientation, 0.5, newFirst ? added : existing, newFirst ? existing : added));
                if (!copy) removedGroup = RemoveGroupIfEmpty(source.Id);
                _activeGroupId = newGroupId;
            }
            if (removedGroup is not null) await _host.RemoveViewAsync(removedGroup, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    private async Task TransferViewAsync(
        string viewId,
        string targetGroupId,
        int targetIndex,
        bool copy,
        CancellationToken cancellationToken)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? source;
            GroupState? target;
            ViewState? view;
            lock (_gate)
            {
                source = _groups.Values.FirstOrDefault(group => group.Views.Any(candidate => candidate.Id == viewId));
                target = _groups.GetValueOrDefault(targetGroupId);
                view = source?.Views.FirstOrDefault(candidate => candidate.Id == viewId);
            }
            if (source is null || target is null || view is null || (copy && TotalViews() >= MaximumViews)) return;
            await CaptureActiveViewAsync(source, cancellationToken).ConfigureAwait(false);
            string? removedGroup = null;
            lock (_gate)
            {
                if (!copy && source == target)
                {
                    var sourceIndex = source.Views.IndexOf(view);
                    source.Views.Remove(view);
                    if (sourceIndex < targetIndex) targetIndex--;
                    source.Views.Insert(Math.Clamp(targetIndex, 0, source.Views.Count), view);
                    source.ActiveViewId = view.Id;
                    _activeGroupId = source.Id;
                }
                else
                {
                    var existing = target.Views.FirstOrDefault(candidate => candidate.DocumentId == view.DocumentId);
                    ViewState transferred;
                    if (existing is not null) transferred = existing;
                    else
                    {
                        transferred = copy
                            ? new ViewState { Id = NextId("view"), DocumentId = view.DocumentId, State = view.State }
                            : view;
                        target.Views.Insert(Math.Clamp(targetIndex, 0, target.Views.Count), transferred);
                    }
                    if (!copy && source != target)
                    {
                        source.Views.Remove(view);
                        source.ActiveViewId = source.Views.FirstOrDefault()?.Id;
                        removedGroup = RemoveGroupIfEmpty(source.Id);
                    }
                    target.ActiveViewId = transferred.Id;
                    _activeGroupId = target.Id;
                }
            }
            if (removedGroup is not null) await _host.RemoveViewAsync(removedGroup, cancellationToken).ConfigureAwait(false);
            await ActivateCurrentAsync(focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task CloseViewAsync(string viewId, bool discardDirty, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? group;
            ViewState? view;
            lock (_gate)
            {
                group = _groups.Values.FirstOrDefault(candidate => candidate.Views.Any(item => item.Id == viewId));
                view = group?.Views.FirstOrDefault(item => item.Id == viewId);
            }
            if (group is null || view is null) return;
            await CaptureActiveViewAsync(group, cancellationToken).ConfigureAwait(false);
            var documentId = view.DocumentId;
            string? removedGroup;
            lock (_gate)
            {
                var index = group.Views.IndexOf(view);
                group.Views.RemoveAt(index);
                group.ActiveViewId = group.Views.ElementAtOrDefault(Math.Min(index, group.Views.Count - 1))?.Id;
                removedGroup = RemoveGroupIfEmpty(group.Id);
            }
            if (!HasView(documentId))
                await _documents.CloseAsync([documentId], discardDirty, cancellationToken).ConfigureAwait(false);
            if (removedGroup is not null) await _host.RemoveViewAsync(removedGroup, cancellationToken).ConfigureAwait(false);
            await ActivateCurrentAsync(focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task CloseGroupAsync(string groupId, bool discardDirty, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GroupState? group;
            string[] closingDocuments;
            lock (_gate)
            {
                group = _groups.GetValueOrDefault(groupId);
                if (group is null || _groups.Count == 1) return;
                closingDocuments = group.Views.Select(view => view.DocumentId)
                    .Where(documentId => _groups.Values.Where(candidate => candidate != group)
                        .All(candidate => candidate.Views.All(view => view.DocumentId != documentId)))
                    .Distinct(StringComparer.Ordinal).ToArray();
                _groups.Remove(groupId);
                _layout = RemoveGroup(_layout, groupId) ?? new EditorGroupNodeSnapshot(MainGroupId);
                _activeGroupId = LeafIds(_layout).First();
            }
            if (closingDocuments.Length > 0)
                await _documents.CloseAsync(closingDocuments, discardDirty, cancellationToken).ConfigureAwait(false);
            await _host.RemoveViewAsync(groupId, cancellationToken).ConfigureAwait(false);
            await ActivateCurrentAsync(focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task FocusRelativeAsync(int offset, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                var leaves = LeafIds(_layout).ToArray();
                var index = Array.IndexOf(leaves, _activeGroupId);
                _activeGroupId = leaves[(index + offset + leaves.Length) % leaves.Length];
            }
            await ActivateCurrentAsync(focus: true, cancellationToken).ConfigureAwait(false);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task ResizeAsync(string splitId, double ratio, CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) _layout = Resize(_layout, splitId, Math.Clamp(ratio, 0.1, 0.9));
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    public async Task DistributeEvenlyAsync(CancellationToken cancellationToken = default)
    {
        await _mutations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate) _layout = Distribute(_layout);
            Publish();
            QueuePersist();
        }
        finally { _mutations.Release(); }
    }

    private async Task ActivateCurrentAsync(bool focus, CancellationToken cancellationToken)
    {
        GroupState group;
        ViewState? view;
        lock (_gate)
        {
            group = _groups[_activeGroupId];
            view = ActiveView(group);
        }
        if (!group.IsMounted) return;
        await _host.SetActiveViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            if (group.IsMounted) await _host.ClearViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (_documents.Snapshot.ActiveId != view.DocumentId)
            await _documents.ActivateAsync(view.DocumentId, cancellationToken).ConfigureAwait(false);
        await AttachGroupAsync(group, focus, cancellationToken).ConfigureAwait(false);
    }

    private async Task AttachGroupAsync(GroupState group, bool focus, CancellationToken cancellationToken)
    {
        if (!group.IsMounted) return;
        ViewState? view;
        DocumentTabSnapshot? document;
        lock (_gate)
        {
            view = ActiveView(group);
            document = view is null ? null : _documents.Snapshot.Tabs.FirstOrDefault(tab => tab.Id == view.DocumentId);
        }
        if (view is null || document is null || document.IsMissing)
            await _host.ClearViewAsync(group.Id, cancellationToken).ConfigureAwait(false);
        else
            await _host.SwitchViewDocumentAsync(group.Id, document.DocumentUri, view.State, focus, cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureActiveViewAsync(GroupState group, CancellationToken cancellationToken)
    {
        if (!group.IsMounted) return;
        ViewState? view;
        DocumentTabSnapshot? document;
        lock (_gate)
        {
            view = ActiveView(group);
            document = view is null ? null : _documents.Snapshot.Tabs.FirstOrDefault(tab => tab.Id == view.DocumentId);
        }
        if (view is null || document is null) return;
        var state = await _host.GetViewStateAsync(group.Id, document.DocumentUri, cancellationToken).ConfigureAwait(false);
        lock (_gate) view.State = state;
    }

    private bool TryRestore(PersistedEditorLayout? persisted, DocumentTabsSnapshot documents)
    {
        if (persisted is null || persisted.Groups is null || persisted.Root is null) return false;
        try
        {
            var layout = ReadNode(persisted.Root, 1);
            var leaves = LeafIds(layout).ToArray();
            var nodeIds = NodeIds(layout).ToArray();
            if (leaves.Length is 0 or > MaximumGroups || leaves.Distinct(StringComparer.Ordinal).Count() != leaves.Length)
                return false;
            if (nodeIds.Any(string.IsNullOrWhiteSpace)
                || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Length) return false;
            var knownDocuments = documents.Tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
            var groups = new Dictionary<string, GroupState>(StringComparer.Ordinal);
            var viewIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var saved in persisted.Groups)
            {
                if (saved is null || !leaves.Contains(saved.Id, StringComparer.Ordinal) || groups.ContainsKey(saved.Id)) return false;
                var group = new GroupState { Id = saved.Id, IsMounted = saved.Id == MainGroupId };
                foreach (var view in saved.Views ?? [])
                {
                    if (view is null || !knownDocuments.Contains(view.DocumentId) || !viewIds.Add(view.Id)) continue;
                    group.Views.Add(new ViewState { Id = view.Id, DocumentId = view.DocumentId, State = view.ViewState });
                }
                group.ActiveViewId = group.Views.Any(view => view.Id == saved.ActiveViewId)
                    ? saved.ActiveViewId : group.Views.FirstOrDefault()?.Id;
                groups.Add(group.Id, group);
            }
            if (leaves.Any(id => !groups.ContainsKey(id))) return false;
            if (groups.Count > 1 && groups.Values.Any(group => group.Views.Count == 0)) return false;
            var activeGroupId = groups.ContainsKey(persisted.ActiveGroupId) ? persisted.ActiveGroupId : leaves[0];
            if (!groups.ContainsKey(MainGroupId))
            {
                if (nodeIds.Contains(MainGroupId, StringComparer.Ordinal)) return false;
                var original = groups[activeGroupId];
                var primary = new GroupState
                {
                    Id = MainGroupId,
                    ActiveViewId = original.ActiveViewId,
                    IsMounted = true,
                };
                primary.Views.AddRange(original.Views);
                groups.Remove(original.Id);
                groups.Add(primary.Id, primary);
                layout = ReplaceGroup(layout, original.Id, new EditorGroupNodeSnapshot(primary.Id));
                activeGroupId = primary.Id;
            }
            lock (_gate)
            {
                _groups.Clear();
                foreach (var item in groups) _groups.Add(item.Key, item.Value);
                _layout = layout;
                _activeGroupId = activeGroupId;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or FormatException)
        {
            _notifications.Raise("novasharp.groups.restore", NotificationSeverity.Warning,
                $"The saved editor layout was invalid and one group was restored instead: {exception.Message}");
            return false;
        }
    }

    private EditorLayoutNodeSnapshot ReadNode(PersistedEditorLayoutNode node, int depth)
    {
        if (depth > MaximumLayoutDepth) throw new InvalidOperationException("The split layout is too deep.");
        if (node.Kind == "group" && !string.IsNullOrWhiteSpace(node.GroupId)) return new EditorGroupNodeSnapshot(node.GroupId);
        if (node.Kind != "split" || node.First is null || node.Second is null
            || !Enum.TryParse<EditorSplitOrientation>(node.Orientation, true, out var orientation))
            throw new InvalidOperationException("The split layout contains an unknown node.");
        return new EditorSplitNodeSnapshot(node.Id, orientation, Math.Clamp(node.Ratio, 0.1, 0.9),
            ReadNode(node.First, depth + 1), ReadNode(node.Second, depth + 1));
    }

    private void CreateDefaultLayout(DocumentTabsSnapshot documents)
    {
        _groups.Clear();
        var group = new GroupState { Id = MainGroupId, IsMounted = true };
        foreach (var tab in documents.Tabs)
        {
            var view = AddView(group, tab.Id);
            if (tab.Id == documents.ActiveId) group.ActiveViewId = view.Id;
        }
        group.ActiveViewId ??= group.Views.FirstOrDefault()?.Id;
        _groups.Add(group.Id, group);
        _layout = new EditorGroupNodeSnapshot(group.Id);
        _activeGroupId = group.Id;
    }

    private void EnsureEveryDocumentHasAView(DocumentTabsSnapshot documents)
    {
        var target = _groups[_activeGroupId];
        foreach (var document in documents.Tabs)
            if (!_groups.Values.Any(group => group.Views.Any(view => view.DocumentId == document.Id)))
                AddView(target, document.Id);
        target.ActiveViewId ??= target.Views.FirstOrDefault()?.Id;
    }

    private void OnDocumentsChanged(DocumentTabsSnapshot documents)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_gate)
        {
            var known = documents.Tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var group in _groups.Values)
            {
                group.Views.RemoveAll(view => !known.Contains(view.DocumentId));
                if (group.Views.All(view => view.Id != group.ActiveViewId)) group.ActiveViewId = group.Views.FirstOrDefault()?.Id;
            }
            EnsureEveryDocumentHasAView(documents);
        }
        Publish();
    }

    private EditorGroupsSnapshot BuildSnapshot()
    {
        var documents = _documents.Snapshot.Tabs.ToDictionary(tab => tab.Id, StringComparer.Ordinal);
        var groups = _groups.Values.ToDictionary(group => group.Id, group =>
        {
            var tabs = group.Views.Where(view => documents.ContainsKey(view.DocumentId)).Select(view =>
            {
                var document = documents[view.DocumentId];
                var active = view.Id == group.ActiveViewId;
                var label = document.AccessibleLabel + (group.Id == _activeGroupId && active ? ", active group" : string.Empty);
                return new EditorGroupTabSnapshot(view.Id, view.DocumentId, document.DocumentUri, document.Path,
                    document.Label, label, active, document.IsPreview, document.IsPinned, document.IsDirty,
                    document.IsReadOnly, document.IsMissing);
            }).ToArray();
            return new EditorGroupSnapshot(group.Id, tabs, group.ActiveViewId, group.Id == _activeGroupId);
        }, StringComparer.Ordinal);
        return new EditorGroupsSnapshot(_layout, groups, _activeGroupId, _version);
    }

    private ViewState AddView(GroupState group, string documentId, int targetIndex = int.MaxValue)
    {
        var view = new ViewState { Id = NextId("view"), DocumentId = documentId };
        group.Views.Insert(Math.Clamp(targetIndex, 0, group.Views.Count), view);
        group.ActiveViewId ??= view.Id;
        return view;
    }

    private static void RepositionView(GroupState group, ViewState view, int targetIndex)
    {
        var sourceIndex = group.Views.IndexOf(view);
        if (sourceIndex < 0) return;
        group.Views.RemoveAt(sourceIndex);
        if (sourceIndex < targetIndex) targetIndex--;
        group.Views.Insert(Math.Clamp(targetIndex, 0, group.Views.Count), view);
    }

    private ViewState? ActiveView(GroupState group) =>
        group.Views.FirstOrDefault(view => view.Id == group.ActiveViewId) ?? group.Views.FirstOrDefault();

    private int TotalViews() => _groups.Values.Sum(group => group.Views.Count);
    private bool HasView(string documentId) => _groups.Values.Any(group => group.Views.Any(view => view.DocumentId == documentId));
    private static string NextId(string kind) => $"{kind}-{Guid.NewGuid():N}";

    private string? RemoveGroupIfEmpty(string groupId)
    {
        if (_groups.Count == 1 || _groups[groupId].Views.Count > 0) return null;
        _groups.Remove(groupId);
        _layout = RemoveGroup(_layout, groupId) ?? new EditorGroupNodeSnapshot(MainGroupId);
        _activeGroupId = LeafIds(_layout).First();
        return groupId;
    }

    private static int DepthOf(EditorLayoutNodeSnapshot node, string groupId, int depth = 1) => node switch
    {
        EditorGroupNodeSnapshot group => group.GroupId == groupId ? depth : -1,
        EditorSplitNodeSnapshot split => Math.Max(DepthOf(split.First, groupId, depth + 1), DepthOf(split.Second, groupId, depth + 1)),
        _ => -1,
    };

    private static IEnumerable<string> LeafIds(EditorLayoutNodeSnapshot node)
    {
        if (node is EditorGroupNodeSnapshot group) { yield return group.GroupId; yield break; }
        if (node is not EditorSplitNodeSnapshot split) yield break;
        foreach (var id in LeafIds(split.First)) yield return id;
        foreach (var id in LeafIds(split.Second)) yield return id;
    }

    private static IEnumerable<string> NodeIds(EditorLayoutNodeSnapshot node)
    {
        yield return node.Id;
        if (node is not EditorSplitNodeSnapshot split) yield break;
        foreach (var id in NodeIds(split.First)) yield return id;
        foreach (var id in NodeIds(split.Second)) yield return id;
    }

    private static EditorLayoutNodeSnapshot ReplaceGroup(EditorLayoutNodeSnapshot node, string groupId, EditorLayoutNodeSnapshot replacement) => node switch
    {
        EditorGroupNodeSnapshot group when group.GroupId == groupId => replacement,
        EditorSplitNodeSnapshot split => split with
        {
            First = ReplaceGroup(split.First, groupId, replacement),
            Second = ReplaceGroup(split.Second, groupId, replacement),
        },
        _ => node,
    };

    private static EditorLayoutNodeSnapshot? RemoveGroup(EditorLayoutNodeSnapshot node, string groupId) => node switch
    {
        EditorGroupNodeSnapshot group => group.GroupId == groupId ? null : group,
        EditorSplitNodeSnapshot split => Collapse(split, RemoveGroup(split.First, groupId), RemoveGroup(split.Second, groupId)),
        _ => node,
    };

    private static EditorLayoutNodeSnapshot? Collapse(
        EditorSplitNodeSnapshot split,
        EditorLayoutNodeSnapshot? first,
        EditorLayoutNodeSnapshot? second) => (first, second) switch
    {
        (null, null) => null,
        (null, not null) => second,
        (not null, null) => first,
        _ => split with { First = first!, Second = second! },
    };

    private static EditorLayoutNodeSnapshot Resize(EditorLayoutNodeSnapshot node, string splitId, double ratio) => node switch
    {
        EditorSplitNodeSnapshot split when split.Id == splitId => split with { Ratio = ratio },
        EditorSplitNodeSnapshot split => split with
        {
            First = Resize(split.First, splitId, ratio),
            Second = Resize(split.Second, splitId, ratio),
        },
        _ => node,
    };

    private static EditorLayoutNodeSnapshot Distribute(EditorLayoutNodeSnapshot node) => node is EditorSplitNodeSnapshot split
        ? split with { Ratio = 0.5, First = Distribute(split.First), Second = Distribute(split.Second) }
        : node;

    private PersistedEditorLayout CapturePersistedLayout()
    {
        lock (_gate)
        {
            return new PersistedEditorLayout(
                WriteNode(_layout),
                _groups.Values.Select(group => new PersistedEditorGroup(group.Id,
                    group.Views.Select(view => new PersistedEditorView(view.Id, view.DocumentId, view.State)).ToArray(),
                    group.ActiveViewId)).ToArray(),
                _activeGroupId);
        }
    }

    private static PersistedEditorLayoutNode WriteNode(EditorLayoutNodeSnapshot node) => node switch
    {
        EditorGroupNodeSnapshot group => new PersistedEditorLayoutNode(group.Id, "group", GroupId: group.GroupId),
        EditorSplitNodeSnapshot split => new PersistedEditorLayoutNode(split.Id, "split",
            Orientation: split.Orientation.ToString(), Ratio: split.Ratio,
            First: WriteNode(split.First), Second: WriteNode(split.Second)),
        _ => throw new InvalidOperationException("Unknown editor layout node."),
    };

    private void Publish()
    {
        EditorGroupsSnapshot snapshot;
        lock (_gate) { _version++; snapshot = BuildSnapshot(); }
        Changed?.Invoke(snapshot);
    }

    private void QueuePersist()
    {
        lock (_persistenceGate)
        {
            _persistAgain = true;
            if (_persistenceTask.IsCompleted) _persistenceTask = PersistLatestAsync();
        }
    }

    private async Task PersistLatestAsync()
    {
        await Task.Yield();
        while (true)
        {
            lock (_persistenceGate)
            {
                if (!_persistAgain)
                {
                    _persistenceTask = Task.CompletedTask;
                    return;
                }
                _persistAgain = false;
            }
            try
            {
                var layout = CapturePersistedLayout();
                await _persistence.UpdateAsync(state => state with { EditorLayout = layout }).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _notifications.Raise("novasharp.groups.persist", NotificationSeverity.Warning,
                    $"The editor layout could not be saved: {exception.Message}");
            }
        }
    }

    private async Task FlushPersistenceAsync()
    {
        QueuePersist();
        Task persistence;
        lock (_persistenceGate) persistence = _persistenceTask;
        await persistence.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _documents.Changed -= OnDocumentsChanged;
        await _mutations.WaitAsync().ConfigureAwait(false);
        try
        {
            GroupState[] groups;
            lock (_gate) groups = _groups.Values.ToArray();
            foreach (var group in groups) await CaptureActiveViewAsync(group, CancellationToken.None).ConfigureAwait(false);
            await FlushPersistenceAsync().ConfigureAwait(false);
            foreach (var group in groups.Where(group => group.Id != MainGroupId && group.IsMounted))
                await _host.RemoveViewAsync(group.Id, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _mutations.Release();
            _mutations.Dispose();
        }
    }
}
