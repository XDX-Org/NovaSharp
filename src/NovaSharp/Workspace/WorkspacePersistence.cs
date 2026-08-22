using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;

namespace NovaSharp.Workspace;

public sealed record WorkspaceStateDocument
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? WorkspacePath { get; init; }
    public string[] ExpandedPaths { get; init; } = [];
    public string? SelectedPath { get; init; }
    public string? ActivePath { get; init; }
    public bool SidebarVisible { get; init; } = true;
    public int SidebarWidth { get; init; } = 280;
    public PersistedDocumentView[] OpenDocuments { get; init; } = [];
    public string? ActiveDocumentId { get; init; }

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record PersistedDocumentView(
    string Id,
    string Path,
    bool WorkspaceRelative,
    bool IsPreview,
    bool IsPinned,
    EditorViewState? ViewState = null);

public sealed record WorkspaceStateLoadResult(WorkspaceStateDocument State, string? Problem = null);

public sealed class WorkspacePersistenceService
{
    public const string FileName = "workspace-state.json";

    private readonly IApplicationPaths _paths;
    private readonly IDocumentFileStore _files;
    private readonly BoundedWorkQueue _queue;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public WorkspacePersistenceService(IApplicationPaths paths, IDocumentFileStore files, BoundedWorkQueue queue)
    {
        _paths = paths;
        _files = files;
        _queue = queue;
    }

    public string FilePath => Path.Combine(_paths.ConfigurationDirectory, FileName);

    public Task<WorkspaceStateLoadResult> LoadAsync(CancellationToken cancellationToken = default) =>
        _queue.EnqueueAsync(async token =>
        {
            if (!_files.GetState(FilePath).Exists)
            {
                return new WorkspaceStateLoadResult(new WorkspaceStateDocument());
            }

            byte[] bytes;
            try
            {
                bytes = await _files.ReadAllBytesAsync(FilePath, token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new WorkspaceStateLoadResult(new WorkspaceStateDocument(), exception.Message);
            }

            try
            {
                var state = JsonSerializer.Deserialize<WorkspaceStateDocument>(bytes, WorkspaceStateDocument.SerializerOptions)
                    ?? new WorkspaceStateDocument();
                if (state.SchemaVersion > WorkspaceStateDocument.CurrentSchemaVersion)
                {
                    return new WorkspaceStateLoadResult(
                        new WorkspaceStateDocument(),
                        $"Workspace state schema {state.SchemaVersion} is newer than this version supports.");
                }

                return new WorkspaceStateLoadResult(state with
                {
                    ExpandedPaths = state.ExpandedPaths ?? [],
                    OpenDocuments = state.OpenDocuments ?? [],
                });
            }
            catch (JsonException exception)
            {
                try
                {
                    await _files.WriteAllBytesAsync(FilePath + ".invalid", bytes, token).ConfigureAwait(false);
                }
                catch (Exception backupException) when (backupException is IOException or UnauthorizedAccessException)
                {
                }

                return new WorkspaceStateLoadResult(new WorkspaceStateDocument(), $"Workspace state was corrupt and ignored: {exception.Message}");
            }
        }, cancellationToken);

    public async Task SaveAsync(WorkspaceStateDocument state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task UpdateAsync(
        Func<WorkspaceStateDocument, WorkspaceStateDocument> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var loaded = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var state = update(loaded.State) with { SchemaVersion = WorkspaceStateDocument.CurrentSchemaVersion };
            await SaveCoreAsync(state, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    private Task SaveCoreAsync(WorkspaceStateDocument state, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(async token =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(state, WorkspaceStateDocument.SerializerOptions) + "\n";
            await _files.WriteAllBytesAsync(FilePath, Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
}
