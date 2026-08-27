namespace NovaSharp.Workspace;

public enum WorkspaceNodeKind
{
    Directory,
    SupportedFile,
    UnknownFile,
    SymbolicLink,
}

public sealed record WorkspaceNode(
    string Id,
    string Path,
    string Name,
    WorkspaceNodeKind Kind,
    bool IsExpanded = false,
    bool IsLoading = false,
    IReadOnlyList<WorkspaceNode>? Children = null,
    string? Error = null,
    bool IsDirectoryLink = false)
{
    public bool CanExpand => Kind == WorkspaceNodeKind.Directory;
}

public sealed record ExplorerMetrics(
    int PendingWatcherEvents = 0,
    int ActiveEnumerations = 0,
    int CanceledEnumerations = 0,
    int WatcherOverflows = 0,
    TimeSpan LastEnumerationDuration = default);

public sealed record WorkspaceSnapshot(
    string? RootPath = null,
    WorkspaceNode? Root = null,
    string? SelectedId = null,
    string? ActivePath = null,
    bool SidebarVisible = true,
    int SidebarWidth = 280,
    string? Error = null,
    long Version = 0,
    ExplorerMetrics? Metrics = null)
{
    public ExplorerMetrics Metrics { get; init; } = Metrics ?? new ExplorerMetrics();
    public bool IsOpen => Root is not null;
}

public sealed record WorkspaceRelocation(string OldPath, string NewPath, bool IsDirectory);

public enum WorkspaceChangeKind
{
    Created,
    Changed,
    Deleted,
    Renamed,
}

public sealed record WorkspaceChange(
    WorkspaceChangeKind Kind,
    string Path,
    string? OldPath = null,
    long ObservedTimestamp = 0);

public sealed record WorkspaceChangeBatch(IReadOnlyList<WorkspaceChange> Changes, bool Overflowed = false);
