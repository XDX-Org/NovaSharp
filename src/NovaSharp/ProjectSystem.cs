using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace NovaSharp;

public enum ProjectNodeKind { Solution, Project, TargetFramework, Folder, File, GeneratedFile, ProjectReference, AssemblyReference, Analyzer }
public sealed record ProjectNodeContextRequest(ProjectNode Node, double X, double Y);
public sealed record ProjectNode(string Id, string Name, ProjectNodeKind Kind, string? Path = null,
    ImmutableArray<ProjectNode> Children = default)
{
    internal ImmutableArray<ProjectNode> Items => Children.IsDefault ? [] : Children;
    internal string? Detail { get; init; }
}

internal sealed record ProjectLoadState(string? Path, ProjectNode? Root, bool IsLoading, string? Progress,
    TimeSpan Elapsed, int ProjectCount, int DocumentCount);
internal sealed record ProjectContextInfo(ProjectId Id, string Name, string? TargetFramework, string? Configuration);

internal enum DiagnosticSource { ProjectSystem, MsBuild }
internal sealed record ProjectDiagnostic(string Id, DiagnosticSource Source, string Context, long Version,
    NotificationSeverity Severity, string Message, DateTime TimestampUtc);

internal sealed class DiagnosticStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ProjectDiagnostic> _entries = new(StringComparer.Ordinal);

    internal IReadOnlyList<ProjectDiagnostic> Entries
    {
        get { lock (_gate) return _entries.Values.OrderByDescending(entry => entry.TimestampUtc).ToArray(); }
    }

    internal void Replace(DiagnosticSource source, string context, long version, IEnumerable<(NotificationSeverity Severity, string Message)> diagnostics)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Where(pair => pair.Value.Source == source && pair.Value.Context == context)
                         .Select(pair => pair.Key).ToArray()) _entries.Remove(key);
            var index = 0;
            foreach (var diagnostic in diagnostics)
            {
                var id = $"{source}:{context}:{version}:{index++}";
                _entries[id] = new(id, source, context, version, diagnostic.Severity, diagnostic.Message, DateTime.UtcNow);
            }
        }
    }

    internal void Clear() { lock (_gate) _entries.Clear(); }
}

internal readonly record struct EditorSnapshot(string Path, string Text, long Version, bool IsDirty);

public sealed class RoslynProjectSystem : IAsyncDisposable
{
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, ImmutableArray<DocumentId>> _documents = new(PathComparer);
    private readonly Dictionary<string, ProjectId> _activeContexts = new(PathComparer);
    private readonly Dictionary<string, EditorDocumentState> _trackedEditors = new(PathComparer);
    private readonly Dictionary<string, CancellationTokenSource> _editorUpdates = new(PathComparer);
    private readonly Dictionary<string, Task> _editorUpdateTasks = new(PathComparer);
    private readonly List<string> _rawMsBuildLog = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _reloadDebounce;
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly object _watcherGate = new();
    private long _loadVersion;
    private long _completedLoadVersion;

    internal RoslynProjectSystem(DiagnosticStore? diagnostics = null) => Diagnostics = diagnostics ?? new();
    internal DiagnosticStore Diagnostics { get; }
    internal ProjectLoadState State { get; private set; } = new(null, null, false, null, TimeSpan.Zero, 0, 0);
    internal IReadOnlyList<string> RawMsBuildLog { get { lock (_rawMsBuildLog) return _rawMsBuildLog.ToArray(); } }
    internal long CompletedLoadVersion => Interlocked.Read(ref _completedLoadVersion);
    internal IReadOnlyList<ProjectContextInfo> Contexts => _solution?.Projects.Select(project =>
    {
        var outputDirectory = project.OutputFilePath is null ? null : Path.GetDirectoryName(project.OutputFilePath);
        var targetFramework = outputDirectory is null ? null : Path.GetFileName(outputDirectory);
        var configurationDirectory = outputDirectory is null ? null : Path.GetDirectoryName(outputDirectory);
        var configuration = configurationDirectory is null ? null : Path.GetFileName(configurationDirectory);
        return new ProjectContextInfo(project.Id, project.Name, targetFramework, configuration);
    }).ToArray() ?? [];
    internal Solution? CurrentSolution => _solution;
    internal int RetainedSolutionSnapshotCount => _solution is null ? 0
        : _workspace is null || ReferenceEquals(_solution, _workspace.CurrentSolution) ? 1 : 2;
    internal bool HasLinkedDocuments => _documents.Values.Any(ids => ids.Length > 1);
    internal event Action? Changed;

    internal async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical)) throw new FileNotFoundException("Solution or project does not exist.", canonical);
        var extension = Path.GetExtension(canonical);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Open a .sln, .slnx, or .csproj file.");

        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await LoadCoreAsync(canonical, _loadCancellation.Token);
    }

    internal async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (State.Path is not { } path) return;
        StopWatching();
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = null;
        await OpenAsync(path, cancellationToken);
    }

    internal void StopWatching()
    {
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
        }
    }

    internal void Track(EditorDocumentState document)
    {
        if (document.FilePath is not { } path) return;
        path = Path.GetFullPath(path);
        if (_trackedEditors.TryGetValue(path, out var existing) && ReferenceEquals(existing, document)) return;
        if (existing is not null) existing.ContentChanged -= OnEditorChanged;
        _trackedEditors[path] = document;
        document.ContentChanged += OnEditorChanged;
        QueueEditorUpdate(document.CreateSnapshot());
    }

    internal void Untrack(EditorDocumentState document)
    {
        var pair = _trackedEditors.FirstOrDefault(item => ReferenceEquals(item.Value, document));
        if (pair.Value is null) return;
        pair.Value.ContentChanged -= OnEditorChanged;
        _trackedEditors.Remove(pair.Key);
    }

    internal IReadOnlyList<(ProjectId Id, string Name)> GetContexts(string path)
    {
        if (_solution is null || !_documents.TryGetValue(Path.GetFullPath(path), out var ids)) return [];
        return ids.Select(id => _solution.GetDocument(id)?.Project)
            .Where(project => project is not null).DistinctBy(project => project!.Id)
            .Select(project => (project!.Id, project.Name)).ToArray();
    }

    internal bool SelectContext(string path, ProjectId projectId)
    {
        path = Path.GetFullPath(path);
        if (!_documents.TryGetValue(path, out var ids)
            || !ids.Any(id => _solution?.GetDocument(id)?.Project.Id == projectId)) return false;
        _activeContexts[path] = projectId;
        Changed?.Invoke();
        return true;
    }

    internal Document? GetActiveDocument(string path)
    {
        path = Path.GetFullPath(path);
        if (_solution is null || !_documents.TryGetValue(path, out var ids)) return null;
        var preferred = _activeContexts.GetValueOrDefault(path);
        return ids.Select(id => _solution.GetDocument(id))
            .FirstOrDefault(document => document?.Project.Id == preferred)
            ?? ids.Select(id => _solution.GetDocument(id)).FirstOrDefault(document => document is not null);
    }

    internal EditorSnapshot? GetTrackedSnapshot(string path) =>
        _trackedEditors.GetValueOrDefault(Path.GetFullPath(path))?.CreateSnapshot();

    internal async Task<Document?> GetLanguageDocumentAsync(string path, string? projectContext, long version,
        CancellationToken cancellationToken)
    {
        path = Path.GetFullPath(path);
        if (_editorUpdateTasks.GetValueOrDefault(path) is { } update) await update.WaitAsync(cancellationToken);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            if (_trackedEditors.GetValueOrDefault(path)?.Version != version || _solution is null
                || !_documents.TryGetValue(path, out var ids)) return null;
            return ids.Select(id => _solution.GetDocument(id)).FirstOrDefault(document => document is not null
                && (projectContext is null || document.Project.Id.Id.ToString() == projectContext))
                ?? ids.Select(id => _solution.GetDocument(id)).FirstOrDefault(document => document is not null);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var stopwatch = Stopwatch.StartNew();
        var previousState = State;
        var previousContexts = ContextsByProject(_solution);
        State = new(path, previousState.Path == path ? previousState.Root : null, true, "Discovering MSBuild",
            TimeSpan.Zero, previousState.Path == path ? previousState.ProjectCount : 0,
            previousState.Path == path ? previousState.DocumentCount : 0);
        Diagnostics.Clear();
        lock (_rawMsBuildLog) _rawMsBuildLog.Clear();
        Changed?.Invoke();
        MSBuildWorkspace? candidate = null;
        try
        {
            EnsureMsBuildRegistered();
            var workspace = candidate = MSBuildWorkspace.Create();
            workspace.LoadMetadataForReferencedProjects = true;
            workspace.RegisterWorkspaceFailedHandler(args => OnWorkspaceFailed(path, version, args));
            var progress = new ThrottledProgress<ProjectLoadProgress>(TimeSpan.FromMilliseconds(100), item =>
            {
                State = State with { Progress = $"{item.Operation}: {Path.GetFileName(item.FilePath)}" };
                Changed?.Invoke();
            });
            var solution = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, progress, cancellationToken)).Solution
                : await workspace.OpenSolutionAsync(path, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var tree = await Task.Run(() => BuildTree(solution, path), cancellationToken);
            var projectCount = solution.ProjectIds.Count;
            var documentCount = solution.Projects.Sum(project => project.DocumentIds.Count);
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                if (version != _loadVersion) return;
                if (_workspace is not null)
                {
                    _workspace.Dispose();
                }
                _workspace = workspace;
                candidate = null;
                _solution = solution;
                RebuildMappings(solution);
                var currentContexts = ContextsByProject(solution);
                var contextDiagnostics = previousContexts.Keys.Except(currentContexts.Keys, PathComparer)
                    .Select(project => (NotificationSeverity.Warning, $"Project context removed during reload: {project}"))
                    .Concat(previousContexts.Keys.Intersect(currentContexts.Keys, PathComparer)
                        .Where(project => !previousContexts[project].SetEquals(currentContexts[project]))
                        .Select(project => (NotificationSeverity.Warning, $"Project contexts changed during reload: {project}")))
                    .ToArray();
                if (contextDiagnostics.Length > 0)
                    Diagnostics.Replace(DiagnosticSource.ProjectSystem, path, version,
                        contextDiagnostics);
                State = new(path, tree, false, "Loaded", stopwatch.Elapsed, projectCount, documentCount);
                Interlocked.Exchange(ref _completedLoadVersion, version);
                StartWatching(path, solution);
            }
            finally { _mutationGate.Release(); }
            foreach (var editor in _trackedEditors.Values.Where(editor => editor.IsDirty)) QueueEditorUpdate(editor.CreateSnapshot());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (version == _loadVersion) State = previousState with { IsLoading = false, Progress = "Load cancelled", Elapsed = stopwatch.Elapsed };
        }
        catch (Exception exception)
        {
            var message = $"Project load failed: {exception.Message}";
            Diagnostics.Replace(DiagnosticSource.ProjectSystem, path, version, [(NotificationSeverity.Error, message)]);
            State = previousState with { IsLoading = false, Progress = message, Elapsed = stopwatch.Elapsed };
        }
        finally { candidate?.Dispose(); Changed?.Invoke(); }
    }

    private void OnWorkspaceFailed(string path, long version, WorkspaceDiagnosticEventArgs args)
    {
        if (version != _loadVersion) return;
        lock (_rawMsBuildLog) _rawMsBuildLog.Add(args.Diagnostic.Message);
        var severity = args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure ? NotificationSeverity.Error : NotificationSeverity.Warning;
        Diagnostics.Replace(DiagnosticSource.MsBuild, path, version,
            RawMsBuildLog.Select(message => (severity, message)));
        Changed?.Invoke();
    }

    private void RebuildMappings(Solution solution)
    {
        _documents.Clear();
        foreach (var group in solution.Projects.SelectMany(project => project.Documents)
                     .Where(document => document.FilePath is not null).GroupBy(document => Path.GetFullPath(document.FilePath!), PathComparer))
            _documents[group.Key] = group.Select(document => document.Id).ToImmutableArray();
        foreach (var path in _activeContexts.Keys.Where(path => !_documents.ContainsKey(path)).ToArray()) _activeContexts.Remove(path);
    }

    private static ProjectNode BuildTree(Solution solution, string path)
    {
        var projectGroups = solution.Projects.Where(project => project.FilePath is not null)
            .GroupBy(project => Path.GetFullPath(project.FilePath!), PathComparer)
            .OrderBy(group => group.First().Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var projects = projectGroups.Select(group =>
        {
            var project = group.First();
            var files = EnumerateProjectFiles(group);
            var generated = EnumerateGeneratedRazorFiles(Path.GetDirectoryName(project.FilePath)!);
            var references = group.SelectMany(context => context.ProjectReferences).Select(reference => solution.GetProject(reference.ProjectId))
                .Where(reference => reference is not null).Select(reference => new ProjectNode($"p:{reference!.Id.Id}", reference.Name,
                    ProjectNodeKind.ProjectReference, reference.FilePath))
                .Concat(group.SelectMany(context => context.MetadataReferences).Select(reference => new ProjectNode($"m:{reference.Display}",
                    Path.GetFileNameWithoutExtension(reference.Display) ?? reference.Display ?? "Assembly", ProjectNodeKind.AssemblyReference, reference.Display)))
                .Concat(group.SelectMany(context => context.AnalyzerReferences).Select(reference => new ProjectNode($"a:{reference.FullPath}",
                    Path.GetFileNameWithoutExtension(reference.FullPath) ?? reference.Display ?? "Analyzer", ProjectNodeKind.Analyzer, reference.FullPath)))
                .DistinctBy(reference => $"{reference.Kind}:{reference.Path}", PathComparer)
                .ToImmutableArray();
            var children = ImmutableArray.Create(new ProjectNode($"r:{project.FilePath}", "Dependencies", ProjectNodeKind.Folder, null, references));
            if (generated.Length > 0)
                children = children.Add(new($"g:{project.FilePath}", "Generated Documents", ProjectNodeKind.Folder, null, generated));
            children = children.AddRange(files);
            return new ProjectNode($"p:{project.FilePath}", project.Name, ProjectNodeKind.Project, project.FilePath, children);
        }).ToImmutableArray();
        return new($"s:{path}", Path.GetFileNameWithoutExtension(path), ProjectNodeKind.Solution, path, projects)
            { Detail = $"{projects.Length} project{(projects.Length == 1 ? "" : "s")}" };
    }

    private sealed class ThrottledProgress<T>(TimeSpan interval, Action<T> handler) : IProgress<T>
    {
        private readonly long _intervalTicks = Math.Max(1, (long)(interval.TotalSeconds * Stopwatch.Frequency));
        private long _lastReport;

        public void Report(T value)
        {
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastReport);
            if (now - previous < _intervalTicks || Interlocked.CompareExchange(ref _lastReport, now, previous) != previous) return;
            handler(value);
        }
    }

    private static ImmutableArray<ProjectNode> EnumerateProjectFiles(IEnumerable<Project> contexts)
    {
        var project = contexts.First();
        var root = Path.GetDirectoryName(project.FilePath)!;
        var paths = new HashSet<string>(PathComparer);
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(segment => segment is "bin" or "obj" or ".git" or ".vs")
                    || PathComparer.Equals(file, project.FilePath!)) continue;
                paths.Add(Path.GetFullPath(file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        foreach (var file in contexts.SelectMany(context => context.Documents).Select(document => document.FilePath)
                     .Where(file => file is not null)) paths.Add(Path.GetFullPath(file!));
        var entries = paths.Select(file =>
        {
            var relative = Path.GetRelativePath(root, file);
            return new ProjectFile(relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(file) : relative, file);
        }).ToArray();
        return BuildFolder(entries, "", root);
    }

    private static ImmutableArray<ProjectNode> EnumerateGeneratedRazorFiles(string projectRoot)
    {
        var root = Path.Combine(projectRoot, "obj");
        if (!Directory.Exists(root)) return [];
        try
        {
            return Directory.EnumerateFiles(root, "*.g.cs", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains(".razor.", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(path).Contains(".cshtml.", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, PathComparer).Select(path => new ProjectNode($"g:{path}",
                    Path.GetRelativePath(root, path), ProjectNodeKind.GeneratedFile, path)).ToImmutableArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static ImmutableArray<ProjectNode> BuildFolder(IEnumerable<ProjectFile> entries, string prefix, string root)
    {
        var children = new List<ProjectNode>();
        foreach (var group in entries.GroupBy(entry => FirstSegment(entry.RelativePath), PathComparer)
                     .OrderBy(group => group.Any(entry => HasDirectory(entry.RelativePath)) ? 0 : 1)
                     .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!group.Any(entry => HasDirectory(entry.RelativePath)))
            {
                var file = group.First();
                children.Add(new($"f:{file.FullPath}", group.Key, ProjectNodeKind.File, file.FullPath));
                continue;
            }
            var nested = group.Where(entry => HasDirectory(entry.RelativePath))
                .Select(entry => entry with { RelativePath = Remainder(entry.RelativePath) });
            var folderPath = string.IsNullOrEmpty(prefix) ? group.Key : Path.Combine(prefix, group.Key);
            children.Add(new($"d:{folderPath}", group.Key, ProjectNodeKind.Folder, Path.Combine(root, folderPath),
                BuildFolder(nested, folderPath, root)));
        }
        return children.ToImmutableArray();
    }

    private static bool HasDirectory(string path) => path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0;
    private static string FirstSegment(string path) { var index = path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]); return index < 0 ? path : path[..index]; }
    private static string Remainder(string path) { var index = path.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]); return path[(index + 1)..]; }
    private sealed record ProjectFile(string RelativePath, string FullPath);

    private static string ContextKey(Project project) => $"{project.FilePath}|{project.OutputFilePath}";

    private static Dictionary<string, HashSet<string>> ContextsByProject(Solution? solution) => solution?.Projects
        .Where(project => project.FilePath is not null)
        .GroupBy(project => Path.GetFullPath(project.FilePath!), PathComparer)
        .ToDictionary(group => group.Key, group => group.Select(ContextKey).ToHashSet(PathComparer), PathComparer) ?? new(PathComparer);

    private void QueueEditorUpdate(EditorSnapshot snapshot)
    {
        if (!_documents.ContainsKey(snapshot.Path)) return;
        if (_editorUpdates.Remove(snapshot.Path, out var previous)) { previous.Cancel(); previous.Dispose(); }
        var cancellation = new CancellationTokenSource();
        _editorUpdates[snapshot.Path] = cancellation;
        _editorUpdateTasks[snapshot.Path] = ApplyEditorUpdateAsync(snapshot, cancellation.Token);
    }

    private async Task ApplyEditorUpdateAsync(EditorSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            await _mutationGate.WaitAsync(cancellationToken);
            try
            {
                if (_workspace is null || !_documents.TryGetValue(snapshot.Path, out var ids)) return;
                if (_trackedEditors.GetValueOrDefault(snapshot.Path)?.Version != snapshot.Version) return;
                var solution = _solution!;
                var text = SourceText.From(snapshot.Text);
                foreach (var id in ids) solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
                _solution = solution;
            }
            finally { _mutationGate.Release(); }
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void OnEditorChanged(EditorSnapshot snapshot)
    {
        if (!_trackedEditors.ContainsKey(snapshot.Path)
            && _trackedEditors.FirstOrDefault(pair => PathComparer.Equals(pair.Value.FilePath, snapshot.Path)) is { Value: { } editor } pair)
        {
            _trackedEditors.Remove(pair.Key);
            _trackedEditors[snapshot.Path] = editor;
        }
        QueueEditorUpdate(snapshot);
    }

    private void StartWatching(string path, Solution solution)
    {
        var watchers = new List<FileSystemWatcher>();
        var roots = solution.Projects.Select(project => Path.GetDirectoryName(project.FilePath))
            .Append(Path.GetDirectoryName(path)).Where(root => root is not null).Distinct(PathComparer);
        foreach (var root in roots)
        {
            var watcher = new FileSystemWatcher(root!) { IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite, EnableRaisingEvents = true };
            watcher.Changed += OnProjectFileChanged;
            watcher.Created += OnProjectFileChanged;
            watcher.Deleted += OnProjectFileChanged;
            watcher.Renamed += OnProjectFileChanged;
            watchers.Add(watcher);
        }
        lock (_watcherGate)
        {
            foreach (var watcher in _watchers) watcher.Dispose();
            _watchers.Clear();
            _watchers.AddRange(watchers);
        }
    }

    private void OnProjectFileChanged(object sender, FileSystemEventArgs args) => NotifyProjectInputChanged(args.FullPath);

    internal void NotifyProjectInputChanged(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return;
        _reloadDebounce?.Cancel();
        _reloadDebounce?.Dispose();
        _reloadDebounce = new();
        _ = DebouncedReloadAsync(_reloadDebounce.Token);
    }

    private async Task DebouncedReloadAsync(CancellationToken cancellationToken)
    {
        try { await Task.Delay(300, cancellationToken); await OpenAsync(State.Path!, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    public async ValueTask DisposeAsync()
    {
        _loadCancellation?.Cancel();
        _reloadDebounce?.Cancel();
        foreach (var cancellation in _editorUpdates.Values) cancellation.Cancel();
        foreach (var editor in _trackedEditors.Values) editor.ContentChanged -= OnEditorChanged;
        await _mutationGate.WaitAsync();
        _mutationGate.Release();
        StopWatching();
        _workspace?.Dispose();
        _loadCancellation?.Dispose();
        _reloadDebounce?.Dispose();
        foreach (var cancellation in _editorUpdates.Values) cancellation.Dispose();
        _mutationGate.Dispose();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
