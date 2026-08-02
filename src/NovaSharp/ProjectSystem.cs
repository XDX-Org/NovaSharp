using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace NovaSharp;

public enum ProjectNodeKind { Solution, Project, TargetFramework, Folder, File, ProjectReference, AssemblyReference, Analyzer }
public sealed record ProjectNode(string Id, string Name, ProjectNodeKind Kind, string? Path = null,
    ImmutableArray<ProjectNode> Children = default)
{
    internal ImmutableArray<ProjectNode> Items => Children.IsDefault ? [] : Children;
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
    private readonly List<string> _rawMsBuildLog = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _reloadDebounce;
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly List<FileSystemWatcher> _watchers = [];
    private long _loadVersion;

    internal RoslynProjectSystem(DiagnosticStore? diagnostics = null) => Diagnostics = diagnostics ?? new();
    internal DiagnosticStore Diagnostics { get; }
    internal ProjectLoadState State { get; private set; } = new(null, null, false, null, TimeSpan.Zero, 0, 0);
    internal IReadOnlyList<string> RawMsBuildLog { get { lock (_rawMsBuildLog) return _rawMsBuildLog.ToArray(); } }
    internal IReadOnlyList<ProjectContextInfo> Contexts => _solution?.Projects.Select(project =>
    {
        var outputDirectory = project.OutputFilePath is null ? null : Path.GetDirectoryName(project.OutputFilePath);
        var targetFramework = outputDirectory is null ? null : Path.GetFileName(outputDirectory);
        var configurationDirectory = outputDirectory is null ? null : Path.GetDirectoryName(outputDirectory);
        var configuration = configurationDirectory is null ? null : Path.GetFileName(configurationDirectory);
        return new ProjectContextInfo(project.Id, project.Name, targetFramework, configuration);
    }).ToArray() ?? [];
    internal Solution? CurrentSolution => _solution;
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
        await OpenAsync(path, cancellationToken);
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

    private async Task LoadCoreAsync(string path, CancellationToken cancellationToken)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var stopwatch = Stopwatch.StartNew();
        var previousState = State;
        var previousContexts = _solution?.Projects.Select(ContextKey).ToHashSet(PathComparer) ?? [];
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
            var progress = new Progress<ProjectLoadProgress>(item =>
            {
                State = State with { Progress = $"{item.Operation}: {Path.GetFileName(item.FilePath)}" };
                Changed?.Invoke();
            });
            var solution = Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                ? (await workspace.OpenProjectAsync(path, progress, cancellationToken)).Solution
                : await workspace.OpenSolutionAsync(path, progress, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
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
                var removedContexts = previousContexts.Except(solution.Projects.Select(ContextKey), PathComparer).ToArray();
                if (removedContexts.Length > 0)
                    Diagnostics.Replace(DiagnosticSource.ProjectSystem, path, version,
                        removedContexts.Select(context => (NotificationSeverity.Warning, $"Project context removed during reload: {context}")));
                State = new(path, BuildTree(solution, path), false, "Loaded", stopwatch.Elapsed,
                    solution.ProjectIds.Count, solution.Projects.Sum(project => project.DocumentIds.Count));
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
        var projects = solution.Projects.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).Select(project =>
        {
            var files = project.Documents.Where(document => document.FilePath is not null)
                .OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)
                .Select(document => new ProjectNode(document.Id.Id.ToString(), document.Name, ProjectNodeKind.File, document.FilePath)).ToImmutableArray();
            var references = project.ProjectReferences.Select(reference => solution.GetProject(reference.ProjectId))
                .Where(reference => reference is not null).Select(reference => new ProjectNode($"p:{reference!.Id.Id}", reference.Name,
                    ProjectNodeKind.ProjectReference, reference.FilePath))
                .Concat(project.MetadataReferences.Select(reference => new ProjectNode($"m:{reference.Display}",
                    Path.GetFileNameWithoutExtension(reference.Display) ?? reference.Display ?? "Assembly", ProjectNodeKind.AssemblyReference, reference.Display)))
                .Concat(project.AnalyzerReferences.Select(reference => new ProjectNode($"a:{reference.FullPath}",
                    Path.GetFileNameWithoutExtension(reference.FullPath) ?? reference.Display ?? "Analyzer", ProjectNodeKind.Analyzer, reference.FullPath)))
                .ToImmutableArray();
            var children = files.Add(new($"r:{project.Id.Id}", "Dependencies", ProjectNodeKind.Folder, null, references));
            var outputDirectory = project.OutputFilePath is null ? null : Path.GetDirectoryName(project.OutputFilePath);
            var targetFramework = outputDirectory is null ? null : Path.GetFileName(outputDirectory);
            var displayName = targetFramework is null ? project.Name : $"{project.Name} [{targetFramework}]";
            return new ProjectNode(project.Id.Id.ToString(), displayName, ProjectNodeKind.Project, project.FilePath, children);
        }).ToImmutableArray();
        return new($"s:{path}", Path.GetFileName(path), ProjectNodeKind.Solution, path, projects);
    }

    private static string ContextKey(Project project) => $"{project.FilePath}|{project.OutputFilePath}";

    private void QueueEditorUpdate(EditorSnapshot snapshot)
    {
        if (!_documents.ContainsKey(snapshot.Path)) return;
        if (_editorUpdates.Remove(snapshot.Path, out var previous)) { previous.Cancel(); previous.Dispose(); }
        var cancellation = new CancellationTokenSource();
        _editorUpdates[snapshot.Path] = cancellation;
        _ = ApplyEditorUpdateAsync(snapshot, cancellation.Token);
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
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
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
            _watchers.Add(watcher);
        }
    }

    private void OnProjectFileChanged(object sender, FileSystemEventArgs args)
    {
        var name = Path.GetFileName(args.FullPath);
        var extension = Path.GetExtension(args.FullPath);
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
        try { await Task.Delay(300, cancellationToken); await ReloadAsync(cancellationToken); }
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
        foreach (var watcher in _watchers) watcher.Dispose();
        _workspace?.Dispose();
        _loadCancellation?.Dispose();
        _reloadDebounce?.Dispose();
        foreach (var cancellation in _editorUpdates.Values) cancellation.Dispose();
        _mutationGate.Dispose();
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
