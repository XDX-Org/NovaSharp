namespace NovaSharp;

internal enum SplitOrientation { Horizontal, Vertical }
internal enum SplitDirection { Left, Right, Up, Down }

internal abstract class EditorLayoutNode(Guid? id = null)
{
    internal Guid Id { get; } = id ?? Guid.NewGuid();
}

internal sealed class EditorGroup(Guid? id = null) : EditorLayoutNode(id)
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

internal sealed class EditorSplit : EditorLayoutNode
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
