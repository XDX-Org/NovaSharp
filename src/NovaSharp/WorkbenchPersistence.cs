using System.Text.Json;

namespace NovaSharp;

internal sealed record PersistedDocument(string Path, int Group, int SelectionStart, int SelectionEnd);
internal sealed record PersistedBreakpoint(string Path, int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null);
internal sealed record PersistedRunConfiguration(string ProjectPath, string Configuration, string? Framework,
    IReadOnlyList<string> Arguments, string WorkingDirectory);
internal sealed record WorkbenchSnapshot(int SchemaVersion, string? WorkspacePath, IReadOnlyList<PersistedDocument> Documents,
    double SidebarRatio, double PanelRatio, string? ActiveDocumentPath, int RestoreFailures = 0,
    IReadOnlyList<PersistedBreakpoint>? Breakpoints = null, IReadOnlyList<PersistedRunConfiguration>? RunConfigurations = null,
    string? ActivePanel = null, bool SidebarOpen = true, bool PanelOpen = false);
internal sealed record RecoveryBuffer(string OriginalPath, string Content, DateTime CapturedUtc,
    DateTime? OriginalLastWriteUtc = null);
internal sealed record PersistenceDiagnostics(int SchemaVersion, bool StatePresent, bool StateValid,
    int RecoveryBufferCount, long RecoveryBytes);

internal sealed class WorkbenchPersistence(string statePath, string recoveryDirectory)
{
    internal const int CurrentSchemaVersion = 1;

    internal async Task SaveAsync(WorkbenchSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Validate(snapshot);
        await AtomicFile.WriteAsync(statePath, JsonSerializer.SerializeToUtf8Bytes(snapshot), cancellationToken);
    }

    internal async Task<WorkbenchSnapshot?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(statePath)) return null;
        try
        {
            await using var stream = File.OpenRead(statePath);
            var snapshot = await JsonSerializer.DeserializeAsync<WorkbenchSnapshot>(stream, cancellationToken: cancellationToken);
            if (snapshot is null) return null;
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    internal async Task CaptureRecoveryAsync(string originalPath, string content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(recoveryDirectory);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(originalPath))));
        var fullPath = Path.GetFullPath(originalPath);
        var recovery = new RecoveryBuffer(fullPath, content, DateTime.UtcNow,
            File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : null);
        await AtomicFile.WriteAsync(Path.Combine(recoveryDirectory, key + ".json"), JsonSerializer.SerializeToUtf8Bytes(recovery), cancellationToken);
    }

    internal void RemoveRecovery(string originalPath)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(originalPath))));
        var path = Path.Combine(recoveryDirectory, key + ".json");
        if (File.Exists(path)) File.Delete(path);
    }

    internal async Task<IReadOnlyList<RecoveryBuffer>> LoadRecoveryAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(recoveryDirectory)) return [];
        var result = new List<RecoveryBuffer>();
        foreach (var path in Directory.EnumerateFiles(recoveryDirectory, "*.json").Take(256))
        {
            try
            {
                await using var stream = File.OpenRead(path);
                var item = await JsonSerializer.DeserializeAsync<RecoveryBuffer>(stream, cancellationToken: cancellationToken);
                if (item is not null && Path.IsPathFullyQualified(item.OriginalPath) && item.Content.Length <= 16 * 1024 * 1024) result.Add(item);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return result;
    }

    internal async Task ExportDiagnosticsAsync(string destination, CancellationToken cancellationToken = default)
    {
        var recoveries = await LoadRecoveryAsync(cancellationToken);
        var diagnostics = new PersistenceDiagnostics(CurrentSchemaVersion, File.Exists(statePath),
            !File.Exists(statePath) || await LoadAsync(cancellationToken) is not null, recoveries.Count,
            recoveries.Sum(item => (long)System.Text.Encoding.UTF8.GetByteCount(item.Content)));
        await AtomicFile.WriteAsync(destination, JsonSerializer.SerializeToUtf8Bytes(diagnostics), cancellationToken);
    }

    internal void Reset(bool includeRecovery = false)
    {
        if (File.Exists(statePath)) File.Delete(statePath);
        if (!includeRecovery || !Directory.Exists(recoveryDirectory)) return;
        foreach (var path in Directory.EnumerateFiles(recoveryDirectory, "*.json")) File.Delete(path);
    }

    internal static bool RequiresSafeMode(WorkbenchSnapshot? snapshot) => snapshot?.RestoreFailures >= 3;

    private static void Validate(WorkbenchSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported workbench schema.");
        if (snapshot.Documents.Count > 256 || snapshot.SidebarRatio is < 0.1 or > 0.9 || snapshot.PanelRatio is < 0.1 or > 0.9
            || snapshot.RestoreFailures is < 0 or > 100) throw new InvalidDataException("Invalid workbench state.");
        if (snapshot.WorkspacePath is not null && !Path.IsPathFullyQualified(snapshot.WorkspacePath)) throw new InvalidDataException("Workspace path is not absolute.");
        if (snapshot.Documents.Any(item => !Path.IsPathFullyQualified(item.Path) || item.Group is < 0 or > 32
            || item.SelectionStart < 0 || item.SelectionEnd < item.SelectionStart)) throw new InvalidDataException("Invalid document state.");
        if (snapshot.Breakpoints is { Count: > 4096 } || snapshot.Breakpoints?.Any(item =>
                !Path.IsPathFullyQualified(item.Path) || item.Line < 1 || item.Condition?.Length > 4096
                || item.HitCondition?.Length > 256 || item.LogMessage?.Length > 4096) == true)
            throw new InvalidDataException("Invalid breakpoint state.");
        if (snapshot.RunConfigurations is { Count: > 64 } || snapshot.RunConfigurations?.Any(item =>
                !Path.IsPathFullyQualified(item.ProjectPath) || !Path.IsPathFullyQualified(item.WorkingDirectory)
                || string.IsNullOrWhiteSpace(item.Configuration) || item.Arguments.Count > 256
                || item.Arguments.Any(argument => argument.Length > 8192)) == true)
            throw new InvalidDataException("Invalid run configuration state.");
        if (snapshot.ActivePanel is not null && snapshot.ActivePanel is not ("problems" or "output" or "terminal" or "debug"))
            throw new InvalidDataException("Invalid active panel state.");
    }
}

internal sealed class StartupRestoreGuard(string path)
{
    internal async Task<bool> BeginAsync(CancellationToken cancellationToken = default)
    {
        var failures = await ReadAsync(cancellationToken);
        if (failures >= 3) return false;
        await AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(failures + 1), cancellationToken);
        return true;
    }

    internal Task CompleteAsync(CancellationToken cancellationToken = default) =>
        AtomicFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(0), cancellationToken);

    internal async Task ResetAsync(CancellationToken cancellationToken = default) => await CompleteAsync(cancellationToken);

    private async Task<int> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return 0;
        try
        {
            var value = JsonSerializer.Deserialize<int>(await File.ReadAllBytesAsync(path, cancellationToken));
            return Math.Clamp(value, 0, 3);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return 3; }
    }
}
