using System.Text;
using System.Text.Json;
using NovaSharp.Async;
using NovaSharp.Editing;
using NovaSharp.Platform;

namespace NovaSharp.Configuration;

/// <summary>
/// Reads, validates, and writes NovaSharp's settings in their two scopes.
/// </summary>
/// <remarks>
/// Every read and write goes through the bounded background queue and the document file store, so settings are
/// loaded off the UI thread and written by the same replace-in-one-step path that cannot leave a document truncated.
/// See ADR 0002.
/// </remarks>
public sealed class ConfigurationService
{
    /// <summary>The folder a workspace's own settings live in, beside its root.</summary>
    public const string WorkspaceFolderName = ".novasharp";

    /// <summary>The file name used in both scopes.</summary>
    public const string FileName = "settings.json";

    private readonly IApplicationPaths _paths;
    private readonly IDocumentFileStore _store;
    private readonly BoundedWorkQueue _queue;
    private readonly Lock _gate = new();

    private string? _workspaceRoot;
    private SettingsResolution _current = new(WorkbenchSettings.Defaults, []);

    /// <summary>Creates a service over the given seams.</summary>
    public ConfigurationService(IApplicationPaths paths, IDocumentFileStore store, BoundedWorkQueue queue)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(queue);

        _paths = paths;
        _store = store;
        _queue = queue;
    }

    /// <summary>The settings currently in force, with whatever had to be ignored to reach them.</summary>
    public SettingsResolution Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Raised after a load or save changes what is in force.</summary>
    public event Action<SettingsResolution>? Changed;

    /// <summary>The user-scoped file, which applies to every workspace.</summary>
    public string UserFilePath => Path.Combine(_paths.ConfigurationDirectory, FileName);

    /// <summary>The workspace-scoped file, or <see langword="null"/> when no workspace is open.</summary>
    public string? WorkspaceFilePath
    {
        get
        {
            lock (_gate)
            {
                return _workspaceRoot is null
                    ? null
                    : Path.Combine(_workspaceRoot, WorkspaceFolderName, FileName);
            }
        }
    }

    /// <summary>Points the workspace scope at <paramref name="root"/>, or clears it.</summary>
    /// <remarks>Does not reload on its own; the caller decides when that is worth doing.</remarks>
    public void SetWorkspaceRoot(string? root)
    {
        lock (_gate)
        {
            _workspaceRoot = root is null ? null : Path.GetFullPath(root);
        }
    }

    /// <summary>Reads both scopes and publishes the result.</summary>
    public async Task<SettingsResolution> LoadAsync(CancellationToken cancellationToken = default)
    {
        var userPath = UserFilePath;
        var workspacePath = WorkspaceFilePath;

        var resolution = await _queue.EnqueueAsync(async token =>
        {
            var problems = new List<SettingsProblem>();
            var user = await ReadAsync(userPath, SettingsScope.User, problems, token).ConfigureAwait(false);
            var workspace = workspacePath is null
                ? null
                : await ReadAsync(workspacePath, SettingsScope.Workspace, problems, token).ConfigureAwait(false);

            var resolved = SettingsResolver.Resolve(user, userPath, workspace, workspacePath ?? string.Empty);

            // Read problems come first: a file that could not be parsed explains every value that is missing below it.
            return new SettingsResolution(resolved.Settings, [.. problems, .. resolved.Problems]);
        }, cancellationToken).ConfigureAwait(false);

        Publish(resolution);
        return resolution;
    }

    /// <summary>Writes <paramref name="document"/> to the user scope and reloads.</summary>
    public async Task<SettingsResolution> SaveUserAsync(
        SettingsDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        await WriteAsync(UserFilePath, document, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes <paramref name="document"/> to the workspace scope and reloads.</summary>
    /// <exception cref="InvalidOperationException">No workspace is open.</exception>
    public async Task<SettingsResolution> SaveWorkspaceAsync(
        SettingsDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var path = WorkspaceFilePath
            ?? throw new InvalidOperationException("No workspace is open, so there is no workspace scope to write.");

        await WriteAsync(path, document, cancellationToken).ConfigureAwait(false);
        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates only the user-scoped editor font, retaining every other settings key.</summary>
    public async Task<SettingsResolution> SetUserEditorFontAsync(
        EditorFontPreference font,
        CancellationToken cancellationToken = default)
    {
        _ = EditorFonts.Id(font);
        await _queue.EnqueueAsync(async token =>
        {
            var problems = new List<SettingsProblem>();
            var current = await ReadAsync(UserFilePath, SettingsScope.User, problems, token).ConfigureAwait(false);
            if (problems.Count > 0)
            {
                throw new InvalidOperationException(
                    "The user settings file must be repaired before the editor font can be saved.");
            }
            if (current?.SchemaVersion > WorkbenchSettings.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    "The user settings file was written by a newer NovaSharp and cannot be changed by this version.");
            }

            var updated = new SettingsDocument
            {
                SchemaVersion = WorkbenchSettings.CurrentSchemaVersion,
                DefaultEncoding = current?.DefaultEncoding,
                FallbackEncoding = current?.FallbackEncoding,
                DefaultLineEnding = current?.DefaultLineEnding,
                ReloadUnmodifiedFiles = current?.ReloadUnmodifiedFiles,
                WorkspaceIgnoredPaths = current?.WorkspaceIgnoredPaths,
                EditorFont = EditorFonts.Id(font),
                CSharpSuggestions = current?.CSharpSuggestions,
                AdditionalProperties = current?.AdditionalProperties,
            };
            await WriteCoreAsync(UserFilePath, updated, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        return await LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SettingsDocument?> ReadAsync(
        string path,
        SettingsScope scope,
        List<SettingsProblem> problems,
        CancellationToken cancellationToken)
    {
        if (!_store.GetState(path).Exists)
        {
            // A missing file is an empty scope, not a failure. Reporting one would mean every first run started with a
            // problem the user cannot act on.
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = await _store.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add(new SettingsProblem(scope, path, $"The file could not be read: {exception.Message}"));
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SettingsDocument>(bytes, SettingsDocument.SerializerOptions);
        }
        catch (JsonException exception)
        {
            // Kept, not repaired. Rewriting a file somebody hand-edited would throw away the work that broke it.
            var backup = await BackUpAsync(path, bytes, cancellationToken).ConfigureAwait(false);
            problems.Add(new SettingsProblem(
                scope,
                path,
                $"The file is not valid JSON ({exception.Message}). It was left in place"
                + (backup is null ? " and ignored." : $", copied to {Path.GetFileName(backup)}, and ignored.")));
            return null;
        }
    }

    private async Task<string?> BackUpAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var backup = path + ".invalid";
        try
        {
            await _store.WriteAllBytesAsync(backup, bytes, cancellationToken).ConfigureAwait(false);
            return backup;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The original is still there and still the user's. Failing to copy it is not worth failing the load over.
            return null;
        }
    }

    private Task WriteAsync(string path, SettingsDocument document, CancellationToken cancellationToken) =>
        _queue.EnqueueAsync(token => WriteCoreAsync(path, document, token), cancellationToken);

    private async Task<bool> WriteCoreAsync(
        string path,
        SettingsDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stamped = document.SchemaVersion is null
            ? new SettingsDocument
            {
                SchemaVersion = WorkbenchSettings.CurrentSchemaVersion,
                DefaultEncoding = document.DefaultEncoding,
                FallbackEncoding = document.FallbackEncoding,
                DefaultLineEnding = document.DefaultLineEnding,
                ReloadUnmodifiedFiles = document.ReloadUnmodifiedFiles,
                WorkspaceIgnoredPaths = document.WorkspaceIgnoredPaths,
                EditorFont = document.EditorFont,
                CSharpSuggestions = document.CSharpSuggestions,
                AdditionalProperties = document.AdditionalProperties,
            }
            : document;

        var json = JsonSerializer.Serialize(stamped, SettingsDocument.SerializerOptions);
        await _store.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(json + "\n"), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void Publish(SettingsResolution resolution)
    {
        lock (_gate)
        {
            _current = resolution;
        }

        Changed?.Invoke(resolution);
    }
}
