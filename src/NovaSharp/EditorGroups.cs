namespace NovaSharp;

public enum SplitOrientation { Horizontal, Vertical }
public enum SplitDirection { Left, Right, Up, Down }

public abstract class EditorLayoutNode(Guid? id = null)
{
    internal Guid Id { get; } = id ?? Guid.NewGuid();
}

public sealed class EditorGroup(Guid? id = null) : EditorLayoutNode(id)
{
    private readonly List<DocumentTab> _tabs = [];
    internal IReadOnlyList<DocumentTab> Tabs => _tabs;
    internal DocumentTab? ActiveTab { get; private set; }

    internal void Add(DocumentTab tab, int? index = null)
    {
        if (_tabs.Contains(tab)) throw new InvalidOperationException("The view already belongs to this group.");
        _tabs.Insert(Math.Clamp(index ?? _tabs.Count, 0, _tabs.Count), tab);
        ActiveTab = tab;
    }

    internal int IndexOf(DocumentTab tab) => _tabs.IndexOf(tab);

    internal bool Remove(DocumentTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return false;
        _tabs.RemoveAt(index);
        if (ReferenceEquals(ActiveTab, tab))
            ActiveTab = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        return true;
    }

    internal void Activate(DocumentTab tab)
    {
        if (!_tabs.Contains(tab)) throw new ArgumentException("The view is not owned by this group.", nameof(tab));
        ActiveTab = tab;
    }
}

public sealed class EditorSplit : EditorLayoutNode
{
    internal EditorSplit(SplitOrientation orientation, EditorLayoutNode first, EditorLayoutNode second,
        double ratio = 0.5, Guid? id = null) : base(id)
    {
        Orientation = orientation;
        First = first;
        Second = second;
        Ratio = ClampRatio(ratio);
    }

    internal SplitOrientation Orientation { get; }
    internal EditorLayoutNode First { get; set; }
    internal EditorLayoutNode Second { get; set; }
    internal double Ratio { get; private set; }
    internal void Resize(double ratio) => Ratio = ClampRatio(ratio);
    private static double ClampRatio(double ratio) => Math.Clamp(double.IsFinite(ratio) ? ratio : 0.5, 0.1, 0.9);
}

internal sealed class EditorLayout
{
    internal const int MaximumDepth = 8;
    internal const int MinimumGroupExtent = 160;
    internal EditorLayoutNode Root { get; private set; }
    internal Guid FocusedGroupId { get; private set; }
    internal IReadOnlyList<EditorGroup> Groups => EnumerateGroups(Root).ToArray();

    internal EditorLayout(EditorGroup? initialGroup = null)
    {
        var group = initialGroup ?? new();
        Root = group;
        FocusedGroupId = group.Id;
    }

    internal EditorLayout(EditorLayoutNode root, Guid focusedGroupId)
    {
        Root = root;
        FocusedGroupId = EnumerateGroups(root).Any(group => group.Id == focusedGroupId)
            ? focusedGroupId : EnumerateGroups(root).First().Id;
    }

    internal EditorGroup? Split(Guid groupId, SplitDirection direction)
    {
        var found = Find(Root, groupId, 0);
        if (found.Group is null || found.Depth >= MaximumDepth) return null;
        var created = new EditorGroup();
        var orientation = direction is SplitDirection.Left or SplitDirection.Right
            ? SplitOrientation.Horizontal : SplitOrientation.Vertical;
        var before = direction is SplitDirection.Left or SplitDirection.Up;
        var replacement = new EditorSplit(orientation, before ? created : found.Group,
            before ? found.Group : created);
        Root = Replace(Root, groupId, replacement);
        FocusedGroupId = created.Id;
        return created;
    }

    internal bool RemoveEmptyGroup(Guid groupId)
    {
        var group = Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group is null || group.Tabs.Count > 0 || ReferenceEquals(group, Root)) return false;
        Root = Remove(Root, groupId)!;
        if (FocusedGroupId == groupId) FocusedGroupId = Groups[0].Id;
        return true;
    }

    internal bool Focus(Guid groupId)
    {
        if (Groups.All(group => group.Id != groupId)) return false;
        FocusedGroupId = groupId;
        return true;
    }

    internal bool Resize(Guid splitId, double ratio)
    {
        var split = Enumerate(Root).OfType<EditorSplit>().FirstOrDefault(node => node.Id == splitId);
        if (split is null) return false;
        split.Resize(ratio);
        return true;
    }

    internal void DistributeEvenly()
    {
        foreach (var split in Enumerate(Root).OfType<EditorSplit>()) split.Resize(0.5);
    }

    private static (EditorGroup? Group, int Depth) Find(EditorLayoutNode node, Guid id, int depth) => node switch
    {
        EditorGroup group when group.Id == id => (group, depth),
        EditorSplit split => Find(split.First, id, depth + 1) is { Group: not null } first
            ? first : Find(split.Second, id, depth + 1),
        _ => (null, depth)
    };

    private static EditorLayoutNode Replace(EditorLayoutNode node, Guid id, EditorLayoutNode replacement)
    {
        if (node.Id == id) return replacement;
        if (node is EditorSplit split)
        {
            split.First = Replace(split.First, id, replacement);
            split.Second = Replace(split.Second, id, replacement);
        }
        return node;
    }

    private static EditorLayoutNode? Remove(EditorLayoutNode node, Guid id)
    {
        if (node.Id == id) return null;
        if (node is not EditorSplit split) return node;
        var first = Remove(split.First, id);
        var second = Remove(split.Second, id);
        if (first is null) return second;
        if (second is null) return first;
        split.First = first;
        split.Second = second;
        return split;
    }

    private static IEnumerable<EditorLayoutNode> Enumerate(EditorLayoutNode node)
    {
        yield return node;
        if (node is not EditorSplit split) yield break;
        foreach (var child in Enumerate(split.First)) yield return child;
        foreach (var child in Enumerate(split.Second)) yield return child;
    }

    private static IEnumerable<EditorGroup> EnumerateGroups(EditorLayoutNode node) => Enumerate(node).OfType<EditorGroup>();
}

public sealed class EditorGroupWorkspace : IDisposable
{
    private readonly DocumentRegistry _registry = new();
    internal EditorLayout Layout { get; private set; } = new();
    internal EditorGroup FocusedGroup => Layout.Groups.Single(group => group.Id == Layout.FocusedGroupId);
    internal string? LastError => _registry.LastError;
    internal event Action? Changed;
    internal DocumentTab? DraggedTab { get; private set; }
    internal Guid? DragSourceGroupId { get; private set; }

    internal async Task<DocumentTab?> OpenAsync(string path, bool preview = false, Guid? groupId = null)
    {
        var group = FindGroup(groupId ?? Layout.FocusedGroupId);
        var canonical = Path.GetFullPath(path);
        var existing = group.Tabs.FirstOrDefault(tab => PathEquals(tab.Document.FilePath, canonical));
        if (existing is not null)
        {
            group.Activate(existing);
            if (!preview) existing.Promote();
            Layout.Focus(group.Id);
            OnChanged();
            return existing;
        }
        if (preview && group.Tabs.FirstOrDefault(tab => tab.IsPreview && !tab.Document.IsDirty) is { } reusable)
            Close(group.Id, reusable, discardDirty: true);
        var document = await _registry.AcquireAsync(canonical);
        if (document is null) return null;
        var tab = new DocumentTab(document, preview);
        group.Add(tab);
        Layout.Focus(group.Id);
        OnChanged();
        return tab;
    }

    internal EditorGroup? Split(Guid groupId, SplitDirection direction)
    {
        var group = Layout.Split(groupId, direction);
        if (group is not null) OnChanged();
        return group;
    }

    internal bool Move(DocumentTab tab, Guid sourceGroupId, Guid targetGroupId, int? index = null)
    {
        var source = FindGroup(sourceGroupId);
        var target = FindGroup(targetGroupId);
        if (!source.Remove(tab)) return false;
        target.Add(tab, index);
        Layout.Focus(target.Id);
        if (source.Tabs.Count == 0) Layout.RemoveEmptyGroup(source.Id);
        OnChanged();
        return true;
    }

    internal async Task<DocumentTab?> CopyAsync(DocumentTab source, Guid targetGroupId, int? index = null)
    {
        var path = source.Document.FilePath;
        if (path is null) return null;
        var target = FindGroup(targetGroupId);
        var document = await _registry.AcquireAsync(path, restoreMissing: true);
        if (document is null) return null;
        var copy = new DocumentTab(document, preview: false);
        copy.ViewState.Restore(source.ViewState.SelectionStart, source.ViewState.SelectionEnd,
            source.ViewState.ScrollTop, source.ViewState.ScrollLeft, document.Content?.Length ?? 0);
        target.Add(copy, index);
        Layout.Focus(target.Id);
        OnChanged();
        return copy;
    }

    internal bool Close(Guid groupId, DocumentTab tab, bool discardDirty = false)
    {
        var group = FindGroup(groupId);
        if (tab.Document.IsDirty && !discardDirty && IsLastView(tab.Document)) return false;
        if (!group.Remove(tab)) return false;
        _registry.Release(tab.Document);
        if (group.Tabs.Count == 0) Layout.RemoveEmptyGroup(group.Id);
        OnChanged();
        return true;
    }

    internal bool IsLastView(EditorDocumentState document) => Layout.Groups
        .SelectMany(group => group.Tabs).Count(tab => ReferenceEquals(tab.Document, document)) == 1;

    internal EditorGroup GroupContaining(DocumentTab tab) => Layout.Groups
        .Single(group => group.Tabs.Contains(tab));

    internal bool Focus(Guid groupId)
    {
        var changed = Layout.Focus(groupId);
        if (changed) OnChanged();
        return changed;
    }

    internal void Promote(DocumentTab tab) { tab.Promote(); OnChanged(); }

    internal WorkbenchLayoutState CaptureState() => new(2, CaptureNode(Layout.Root), Layout.FocusedGroupId);

    internal async Task RestoreAsync(WorkbenchLayoutState state)
    {
        foreach (var tab in Layout.Groups.SelectMany(group => group.Tabs).ToArray()) _registry.Release(tab.Document);
        var ids = new HashSet<Guid>();
        var root = await RestoreNodeAsync(state.Root, 0, ids) ?? new EditorGroup();
        Layout = new(root, state.FocusedGroupId);
        OnChanged();
    }

    internal void BeginDrag(DocumentTab tab)
    {
        DraggedTab = tab;
        DragSourceGroupId = GroupContaining(tab).Id;
        OnChanged();
    }

    internal void PreviewReorder(Guid targetGroupId, int index)
    {
        if (DraggedTab is not { } tab || DragSourceGroupId != targetGroupId) return;
        var group = FindGroup(targetGroupId);
        if (!group.Remove(tab)) return;
        group.Add(tab, index);
        OnChanged();
    }

    internal async Task<bool> DropAsync(Guid targetGroupId, int? index = null,
        SplitDirection? direction = null, bool copy = false)
    {
        if (DraggedTab is not { } tab || DragSourceGroupId is not { } sourceId) return false;
        var destination = direction is null ? FindGroup(targetGroupId) : Layout.Split(targetGroupId, direction.Value);
        if (destination is null) { CancelDrag(); return false; }
        var changed = copy ? await CopyAsync(tab, destination.Id, index) is not null
            : Move(tab, sourceId, destination.Id, index);
        CancelDrag();
        return changed;
    }

    internal void CancelDrag() { DraggedTab = null; DragSourceGroupId = null; OnChanged(); }

    private EditorGroup FindGroup(Guid id) => Layout.Groups.Single(group => group.Id == id);
    private void OnChanged() => Changed?.Invoke();
    private static bool PathEquals(string? left, string right) => left is not null && string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static LayoutNodeState CaptureNode(EditorLayoutNode node) => node switch
    {
        EditorGroup group => new("group", group.Id, Tabs: group.Tabs.Select(tab => new SessionViewState(tab.Id,
            tab.Document.FilePath!, tab.IsPreview, tab.ViewState.SelectionStart, tab.ViewState.SelectionEnd,
            tab.ViewState.ScrollTop, tab.ViewState.ScrollLeft)).ToArray(), ActiveViewId: group.ActiveTab?.Id),
        EditorSplit split => new("split", split.Id, split.Orientation, split.Ratio,
            CaptureNode(split.First), CaptureNode(split.Second)),
        _ => throw new InvalidOperationException("Unknown layout node.")
    };

    private async Task<EditorLayoutNode?> RestoreNodeAsync(LayoutNodeState? state, int depth, HashSet<Guid> ids)
    {
        if (state is null || state.Id == Guid.Empty || !ids.Add(state.Id) || depth > EditorLayout.MaximumDepth) return null;
        if (state.Kind == "group")
        {
            var group = new EditorGroup(state.Id);
            foreach (var view in state.Tabs ?? [])
            {
                if (view.Id == Guid.Empty || !ids.Add(view.Id) || string.IsNullOrWhiteSpace(view.Path)
                    || !Path.IsPathFullyQualified(view.Path)) continue;
                var document = await _registry.AcquireAsync(view.Path, restoreMissing: true);
                if (document is null) continue;
                var tab = new DocumentTab(document, view.IsPreview, view.Id);
                tab.ViewState.Restore(view.SelectionStart, view.SelectionEnd, view.ScrollTop, view.ScrollLeft,
                    document.Content?.Length ?? 0);
                group.Add(tab);
            }
            if (state.ActiveViewId is { } activeId && group.Tabs.FirstOrDefault(tab => tab.Id == activeId) is { } active)
                group.Activate(active);
            return group;
        }
        if (state.Kind != "split" || state.Orientation is null || !double.IsFinite(state.Ratio)
            || state.Ratio is < 0.1 or > 0.9) return null;
        var first = await RestoreNodeAsync(state.First, depth + 1, ids);
        var second = await RestoreNodeAsync(state.Second, depth + 1, ids);
        if (first is null) return second;
        if (second is null) return first;
        return new EditorSplit(state.Orientation.Value, first, second, state.Ratio, state.Id);
    }

    public void Dispose()
    {
        foreach (var tab in Layout.Groups.SelectMany(group => group.Tabs).ToArray())
            _registry.Release(tab.Document);
        _registry.Dispose();
    }
}

internal sealed record SessionViewState(Guid Id, string Path, bool IsPreview = false, int SelectionStart = 0,
    int SelectionEnd = 0, double ScrollTop = 0, double ScrollLeft = 0);
internal sealed record LayoutNodeState(string Kind, Guid Id, SplitOrientation? Orientation = null,
    double Ratio = 0.5, LayoutNodeState? First = null, LayoutNodeState? Second = null,
    SessionViewState[]? Tabs = null, Guid? ActiveViewId = null);
internal sealed record WorkbenchLayoutState(int SchemaVersion, LayoutNodeState? Root, Guid FocusedGroupId);
