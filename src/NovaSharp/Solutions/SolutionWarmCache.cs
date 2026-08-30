using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;

namespace NovaSharp.Solutions;

public interface ISolutionWarmCache
{
    Task<SolutionWarmCacheEntry?> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default);

    Task SaveAsync(
        string workspaceRoot,
        SolutionWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed record SolutionWarmCacheEntry(
    string Path,
    string Name,
    IReadOnlyList<ProjectContextSnapshot> Projects,
    TimeSpan RestoreDuration);

public sealed class SolutionWarmCache : ISolutionWarmCache
{
    public const int CurrentSchemaVersion = 1;
    public const string FileName = "solution-warm-cache.json";
    public const int MaxProjects = 2_048;
    public const int MaxDocuments = 100_000;
    public const int MaxReferences = 100_000;
    public const int MaxInputs = 25_000;
    public const long MaxFileBytes = 32 * 1024 * 1024;

    private static readonly string[] EvaluationInputs =
    [
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "global.json",
        "NuGet.config",
    ];

    private readonly IApplicationPaths _applicationPaths;
    private readonly IWorkspacePaths _workspacePaths;
    private readonly IDocumentFileStore _files;
    private readonly BoundedWorkQueue _background;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public SolutionWarmCache(
        IApplicationPaths applicationPaths,
        IWorkspacePaths workspacePaths,
        IDocumentFileStore files,
        BoundedWorkQueue background)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(workspacePaths);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(background);
        _applicationPaths = applicationPaths;
        _workspacePaths = workspacePaths;
        _files = files;
        _background = background;
    }

    public string FilePath => Path.Combine(_applicationPaths.ConfigurationDirectory, FileName);

    public Task<SolutionWarmCacheEntry?> LoadAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return _background.EnqueueAsync(token => LoadCoreAsync(workspaceRoot, token), cancellationToken);
    }

    public async Task SaveAsync(
        string workspaceRoot,
        SolutionWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != SolutionLoadState.Ready || snapshot.Path is null)
        {
            return;
        }

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _background.EnqueueAsync(
                token => SaveCoreAsync(workspaceRoot, snapshot, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _background.EnqueueAsync(token =>
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
                return Task.FromResult(true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    private async Task<SolutionWarmCacheEntry?> LoadCoreAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var state = _files.GetState(FilePath);
        if (!state.Exists || state.Length > MaxFileBytes)
        {
            return null;
        }

        WarmCacheDocument cache;
        try
        {
            var bytes = await _files.ReadAllBytesAsync(FilePath, cancellationToken).ConfigureAwait(false);
            cache = JsonSerializer.Deserialize<WarmCacheDocument>(bytes, SerializerOptions)
                ?? throw new JsonException("The warm cache was empty.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        var canonicalRoot = _workspacePaths.Canonicalize(workspaceRoot);
        if (!HasValidShape(cache)
            || cache.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(cache.WorkspaceUri, _workspacePaths.ToDocumentUri(canonicalRoot).AbsoluteUri, StringComparison.Ordinal)
            || cache.Projects.Length > MaxProjects
            || cache.Projects.Sum(static project => (long)project.Documents.Length) > MaxDocuments
            || cache.Projects.Sum(static project => (long)project.References.Length) > MaxReferences
            || cache.Inputs.Length > MaxInputs)
        {
            return null;
        }

        try
        {
            var solutionPath = DecodePath(canonicalRoot, cache.SolutionPath);
            if (!_workspacePaths.IsDescendantOrSelf(canonicalRoot, solutionPath)
                || !SolutionDiscovery.IsSupported(solutionPath)
                || !_files.GetState(solutionPath).Exists
                || !InputsMatch(canonicalRoot, cache.Inputs))
            {
                return null;
            }

            var projects = cache.Projects.Select(project => new ProjectContextSnapshot(
                project.Id,
                project.Name,
                DecodePath(canonicalRoot, project.Path),
                project.TargetFramework,
                project.IsActive,
                project.Documents.Select(document =>
                {
                    var path = DecodePath(canonicalRoot, document.Path);
                    return new SolutionDocumentSnapshot(
                        document.Id,
                        document.Name,
                        path,
                        _workspacePaths.ToDocumentUri(path).AbsoluteUri,
                        document.IsGenerated);
                }).ToArray(),
                project.References.Select(reference => new SolutionReferenceSnapshot(
                    reference.Kind,
                    reference.Name,
                    reference.Path is null ? null : DecodePath(canonicalRoot, reference.Path))).ToArray(),
                project.Defines,
                project.LanguageVersion,
                project.Nullable,
                project.DocumentsTruncated)).ToArray();

            return new SolutionWarmCacheEntry(
                solutionPath,
                cache.Name,
                projects,
                Stopwatch.GetElapsedTime(started));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private async Task<bool> SaveCoreAsync(
        string workspaceRoot,
        SolutionWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var canonicalRoot = _workspacePaths.Canonicalize(workspaceRoot);
        if (!_workspacePaths.IsDescendantOrSelf(canonicalRoot, snapshot.Path!))
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
            return false;
        }

        var documentCount = snapshot.Projects.Sum(static project => (long)project.Documents.Count);
        var referenceCount = snapshot.Projects.Sum(static project => (long)project.References.Count);
        if (snapshot.Projects.Count > MaxProjects || documentCount > MaxDocuments || referenceCount > MaxReferences)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
            return false;
        }

        var inputs = CollectInputs(canonicalRoot, snapshot);
        if (inputs.Length > MaxInputs)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
            return false;
        }
        var cache = new WarmCacheDocument(
            CurrentSchemaVersion,
            _workspacePaths.ToDocumentUri(canonicalRoot).AbsoluteUri,
            EncodePath(canonicalRoot, snapshot.Path!),
            snapshot.Name ?? Path.GetFileName(snapshot.Path) ?? "Solution",
            snapshot.Projects.Select(project => new WarmProject(
                project.Id,
                project.Name,
                EncodePath(canonicalRoot, project.Path),
                project.TargetFramework,
                project.IsActive,
                project.Documents.Select(document => new WarmDocument(
                    document.Id,
                    document.Name,
                    EncodePath(canonicalRoot, document.Path),
                    document.IsGenerated)).ToArray(),
                project.References.Select(reference => new WarmReference(
                    reference.Kind,
                    reference.Name,
                    reference.Path is null ? null : EncodePath(canonicalRoot, reference.Path))).ToArray(),
                [.. project.Defines],
                project.LanguageVersion,
                project.Nullable,
                project.DocumentsTruncated)).ToArray(),
            inputs);

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cache, SerializerOptions) + "\n");
        if (bytes.LongLength > MaxFileBytes)
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
            return false;
        }

        await _files.WriteAllBytesAsync(FilePath, bytes, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private WarmInput[] CollectInputs(string workspaceRoot, SolutionWorkspaceSnapshot snapshot)
    {
        var candidates = new Dictionary<string, WarmPath>(StringComparer.Ordinal);
        Add(snapshot.Path!);
        foreach (var project in snapshot.Projects)
        {
            Add(project.Path);
            var projectDirectory = Path.GetDirectoryName(project.Path);
            if (projectDirectory is not null)
            {
                Add(Path.Combine(projectDirectory, "obj", "project.assets.json"));
                Add(Path.Combine(projectDirectory, "packages.lock.json"));
                for (var directory = projectDirectory;
                     _workspacePaths.IsDescendantOrSelf(workspaceRoot, directory);
                     directory = Path.GetDirectoryName(directory)!)
                {
                    foreach (var name in EvaluationInputs)
                    {
                        Add(Path.Combine(directory, name));
                    }
                    if (_workspacePaths.IsSamePath(directory, workspaceRoot)
                        || Path.GetDirectoryName(directory) is null)
                    {
                        break;
                    }
                }
            }

            foreach (var analyzer in project.References.Where(static reference => reference.Kind == SolutionReferenceKind.Analyzer))
            {
                if (analyzer.Path is not null)
                {
                    Add(analyzer.Path);
                }
            }
        }

        return candidates.Values.Select(path =>
        {
            var decoded = DecodePath(workspaceRoot, path);
            var state = _files.GetState(decoded);
            return new WarmInput(path, state.Exists, state.Length, state.LastWriteTimeUtc.UtcTicks);
        }).ToArray();

        void Add(string path)
        {
            var encoded = EncodePath(workspaceRoot, path);
            var identity = _workspacePaths.ToDocumentUri(DecodePath(workspaceRoot, encoded)).AbsoluteUri;
            candidates.TryAdd(identity, encoded);
        }
    }

    private bool InputsMatch(string workspaceRoot, IReadOnlyList<WarmInput> inputs)
    {
        foreach (var input in inputs)
        {
            var state = _files.GetState(DecodePath(workspaceRoot, input.Path));
            if (state.Exists != input.Exists
                || state.Length != input.Length
                || state.LastWriteTimeUtc.UtcTicks != input.LastWriteTimeUtcTicks)
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasValidShape(WarmCacheDocument cache)
    {
        if (string.IsNullOrWhiteSpace(cache.WorkspaceUri)
            || string.IsNullOrWhiteSpace(cache.Name)
            || cache.SolutionPath is not { Value.Length: > 0 }
            || cache.Projects is null
            || cache.Inputs is null
            || cache.Inputs.Any(static input => input is null || input.Path is not { Value.Length: > 0 }))
        {
            return false;
        }

        return cache.Projects.All(static project => project is not null
            && !string.IsNullOrWhiteSpace(project.Id)
            && !string.IsNullOrWhiteSpace(project.Name)
            && project.Path is { Value.Length: > 0 }
            && project.Documents is not null
            && project.References is not null
            && project.Defines is not null
            && !string.IsNullOrWhiteSpace(project.LanguageVersion)
            && !string.IsNullOrWhiteSpace(project.Nullable)
            && project.Documents.All(static document => document is not null
                && !string.IsNullOrWhiteSpace(document.Id)
                && !string.IsNullOrWhiteSpace(document.Name)
                && document.Path is { Value.Length: > 0 })
            && project.References.All(static reference => reference is not null
                && Enum.IsDefined(reference.Kind)
                && !string.IsNullOrWhiteSpace(reference.Name)
                && (reference.Path is null || reference.Path.Value.Length > 0)));
    }

    private WarmPath EncodePath(string workspaceRoot, string path)
    {
        var canonical = _workspacePaths.Canonicalize(path);
        return _workspacePaths.IsDescendantOrSelf(workspaceRoot, canonical)
            ? new WarmPath(_workspacePaths.ToWorkspaceRelativePath(workspaceRoot, canonical), true)
            : new WarmPath(canonical, false);
    }

    private string DecodePath(string workspaceRoot, WarmPath path) => path.WorkspaceRelative
        ? _workspacePaths.ResolveWorkspaceRelativePath(workspaceRoot, path.Value)
        : _workspacePaths.Canonicalize(path.Value);

    private static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record WarmCacheDocument(
        int SchemaVersion,
        string WorkspaceUri,
        WarmPath SolutionPath,
        string Name,
        WarmProject[] Projects,
        WarmInput[] Inputs);

    private sealed record WarmPath(string Value, bool WorkspaceRelative);

    private sealed record WarmInput(WarmPath Path, bool Exists, long Length, long LastWriteTimeUtcTicks);

    private sealed record WarmProject(
        string Id,
        string Name,
        WarmPath Path,
        string? TargetFramework,
        bool IsActive,
        WarmDocument[] Documents,
        WarmReference[] References,
        string[] Defines,
        string LanguageVersion,
        string Nullable,
        bool DocumentsTruncated);

    private sealed record WarmDocument(string Id, string Name, WarmPath Path, bool IsGenerated);

    private sealed record WarmReference(SolutionReferenceKind Kind, string Name, WarmPath? Path);
}

internal sealed class NullSolutionWarmCache : ISolutionWarmCache
{
    public static NullSolutionWarmCache Instance { get; } = new();

    public Task<SolutionWarmCacheEntry?> LoadAsync(string workspaceRoot, CancellationToken cancellationToken = default) =>
        Task.FromResult<SolutionWarmCacheEntry?>(null);

    public Task SaveAsync(
        string workspaceRoot,
        SolutionWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
