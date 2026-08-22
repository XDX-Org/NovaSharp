using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;

namespace NovaSharp.Workspace;

public sealed class WorkspaceStateDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string? WorkspacePath { get; init; }
    public string[] ExpandedPaths { get; init; } = [];
    public string? SelectedPath { get; init; }
    public string? ActivePath { get; init; }
    public bool SidebarVisible { get; init; } = true;
    public int SidebarWidth { get; init; } = 280;

    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record WorkspaceStateLoadResult(WorkspaceStateDocument State, string? Problem = null);

public sealed class WorkspacePersistenceService
{
    public const string FileName = "workspace-state.json";

    private readonly IApplicationPaths _paths;
    private readonly IDocumentFileStore _files;
    private readonly BoundedWorkQueue _queue;

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

                return new WorkspaceStateLoadResult(state);
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

    public Task SaveAsync(WorkspaceStateDocument state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        return _queue.EnqueueAsync(async token =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(state, WorkspaceStateDocument.SerializerOptions) + "\n";
            await _files.WriteAllBytesAsync(FilePath, Encoding.UTF8.GetBytes(json), token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }
}
