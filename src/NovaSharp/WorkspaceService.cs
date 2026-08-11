using System.Text.Json;

namespace NovaSharp;

internal enum WorkspaceEntryKind { Folder, SupportedFile, UnknownFile, SymbolicLink }

internal sealed record WorkspaceEntry(string Id, string Name, string Path, WorkspaceEntryKind Kind)
{
    internal bool IsDirectory => Kind == WorkspaceEntryKind.Folder;
}

internal sealed record WorkspaceRestoreState(
    int SchemaVersion = 1,
    string? WorkspacePath = null,
    string[]? ExpandedPaths = null,
    bool SidebarCollapsed = false,
    double SidebarWidth = 280,
    bool? SolutionView = null);

internal sealed class WorkspacePersistence(string path)
{
    internal async Task<WorkspaceRestoreState> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new();
        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<WorkspaceRestoreState>(stream, cancellationToken: cancellationToken);
            return state is { SchemaVersion: 1, SidebarWidth: >= 180 and <= 800 } ? state : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal Task SaveAsync(WorkspaceRestoreState state, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(state), cancellationToken);
}

internal sealed class WorkspaceService : IDisposable
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> DefaultIgnored = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".cs", ".csproj", ".sln", ".slnx", ".razor", ".cshtml", ".html", ".htm", ".css",
            ".js", ".jsx", ".ts", ".tsx", ".json", ".xml", ".md", ".txt" };
    private readonly HashSet<string> _ignored;
    private FileSystemWatcher? _watcher;
    private readonly object _changeGate = new();
    private readonly HashSet<string> _pendingChanges = new(Paths);
    private Timer? _changeTimer;

    internal WorkspaceService(IEnumerable<string>? ignoredNames = null) =>
        _ignored = new(DefaultIgnored.Concat(ignoredNames ?? []), StringComparer.OrdinalIgnoreCase);

    internal string? RootPath { get; private set; }
    internal event Action<string?>? Changed;
    internal event Action<string>? Error;
    internal event Action? RescanRequired;

    internal void Open(string path)
    {
        var canonical = Canonical(path);
        if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException($"Workspace folder does not exist: {path}");
        RootPath = canonical;
        StartWatching();
        Changed?.Invoke(canonical);
    }

    internal void Close()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_changeGate)
        {
            _changeTimer?.Dispose();
            _changeTimer = null;
            _pendingChanges.Clear();
        }
        RootPath = null;
        Changed?.Invoke(null);
    }

    internal async Task<IReadOnlyList<WorkspaceEntry>> GetChildrenAsync(string path, CancellationToken cancellationToken = default)
    {
        var canonical = RequireTraversableDirectory(path);
        return await Task.Run<IReadOnlyList<WorkspaceEntry>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<WorkspaceEntry>();
            foreach (var item in new DirectoryInfo(canonical).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_ignored.Contains(item.Name)) continue;
                var fullPath = Canonical(item.FullName);
                var isLink = item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint);
                var kind = isLink ? WorkspaceEntryKind.SymbolicLink
                    : item is DirectoryInfo ? WorkspaceEntryKind.Folder
                    : SupportedExtensions.Contains(item.Extension) ? WorkspaceEntryKind.SupportedFile
                    : WorkspaceEntryKind.UnknownFile;
                entries.Add(new(fullPath, item.Name, fullPath, kind));
            }
            return entries.OrderByDescending(entry => entry.IsDirectory)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);
    }

    internal string CreateFile(string parent, string name)
    {
        var path = ChildPath(parent, name);
        using (File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        Changed?.Invoke(parent);
        return path;
    }

    internal string CreateFolder(string parent, string name)
    {
        var path = ChildPath(parent, name);
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException($"'{name}' already exists.");
        Directory.CreateDirectory(path);
        Changed?.Invoke(parent);
        return path;
    }

    internal string Move(string source, string destinationParent, string newName)
    {
        var oldPath = RequireInside(source);
        if (PathEquals(oldPath, RootPath!)) throw new InvalidOperationException("The workspace root cannot be moved.");
        var newPath = ChildPath(destinationParent, newName);
        if (File.Exists(oldPath)) File.Move(oldPath, newPath);
        else if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
        else throw new FileNotFoundException("The item no longer exists.", oldPath);
        Changed?.Invoke(Path.GetDirectoryName(oldPath));
        Changed?.Invoke(Path.GetDirectoryName(newPath));
        return newPath;
    }

    internal void Delete(string path)
    {
        var target = RequireInside(path);
        if (PathEquals(target, RootPath!)) throw new InvalidOperationException("The workspace root cannot be deleted.");
        if (File.Exists(target)) File.Delete(target);
        else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        else throw new FileNotFoundException("The item no longer exists.", target);
        Changed?.Invoke(Path.GetDirectoryName(target));
    }

    internal bool Contains(string path)
    {
        if (RootPath is null) return false;
        var candidate = Canonical(path);
        return PathEquals(candidate, RootPath) || candidate.StartsWith(RootPath + Path.DirectorySeparatorChar, PathComparison);
    }

    private string ChildPath(string parent, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Enter a valid file or folder name.", nameof(name));
        var directory = RequireTraversableDirectory(parent);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        return RequireInside(Path.Combine(directory, name));
    }

    private string RequireInside(string path)
    {
        if (RootPath is null) throw new InvalidOperationException("No workspace is open.");
        var canonical = Canonical(path);
        if (!Contains(canonical)) throw new UnauthorizedAccessException("The path is outside the workspace.");
        return canonical;
    }

    private string RequireTraversableDirectory(string path)
    {
        var canonical = RequireInside(path);
        if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException(canonical);
        var relative = Path.GetRelativePath(RootPath!, canonical);
        var current = RootPath!;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment is "" or ".") continue;
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("Symbolic links are Explorer leaves and cannot be traversed.");
        }
        return canonical;
    }

    private void StartWatching()
    {
        _watcher?.Dispose();
        _watcher = new(RootPath!)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Error += (_, args) => HandleWatcherError(args.GetException());
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (args is RenamedEventArgs renamed)
            QueueChange(Path.GetDirectoryName(renamed.OldFullPath));
        QueueChange(Path.GetDirectoryName(args.FullPath));
    }

    private void QueueChange(string? path)
    {
        if (path is null) return;
        lock (_changeGate)
        {
            _pendingChanges.Add(path);
            _changeTimer ??= new(_ => FlushChanges(), null, Timeout.Infinite, Timeout.Infinite);
            _changeTimer.Change(75, Timeout.Infinite);
        }
    }

    private void FlushChanges()
    {
        string[] paths;
        lock (_changeGate)
        {
            paths = _pendingChanges.ToArray();
            _pendingChanges.Clear();
        }
        foreach (var path in paths) Changed?.Invoke(path);
    }

    internal void HandleWatcherError(Exception exception)
    {
        var recovery = "The Explorer was rescanned.";
        try { if (RootPath is not null) StartWatching(); }
        catch (Exception restartException) when (restartException is IOException or UnauthorizedAccessException)
        {
            recovery = $"Watcher restart failed: {restartException.Message}. Use Refresh after resolving the error.";
        }
        Error?.Invoke($"Filesystem watcher lost changes: {exception.Message}. {recovery}");
        RescanRequired?.Invoke();
    }
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool PathEquals(string left, string right) => string.Equals(left, right, PathComparison);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public void Dispose() => Close();
}
