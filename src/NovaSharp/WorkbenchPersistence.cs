using System.Text.Json;

namespace NovaSharp;

internal sealed record PersistedDocument(string Path, int Group, int SelectionStart, int SelectionEnd);
internal sealed record WorkbenchSnapshot(int SchemaVersion, string? WorkspacePath, IReadOnlyList<PersistedDocument> Documents,
    double SidebarRatio, double PanelRatio, string? ActiveDocumentPath, int RestoreFailures = 0);
internal sealed record RecoveryBuffer(string OriginalPath, string Content, DateTime CapturedUtc);

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
        var recovery = new RecoveryBuffer(Path.GetFullPath(originalPath), content, DateTime.UtcNow);
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

    internal static bool RequiresSafeMode(WorkbenchSnapshot? snapshot) => snapshot?.RestoreFailures >= 3;

    private static void Validate(WorkbenchSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion) throw new InvalidDataException("Unsupported workbench schema.");
        if (snapshot.Documents.Count > 256 || snapshot.SidebarRatio is < 0.1 or > 0.9 || snapshot.PanelRatio is < 0.1 or > 0.9
            || snapshot.RestoreFailures is < 0 or > 100) throw new InvalidDataException("Invalid workbench state.");
        if (snapshot.WorkspacePath is not null && !Path.IsPathFullyQualified(snapshot.WorkspacePath)) throw new InvalidDataException("Workspace path is not absolute.");
        if (snapshot.Documents.Any(item => !Path.IsPathFullyQualified(item.Path) || item.Group is < 0 or > 32
            || item.SelectionStart < 0 || item.SelectionEnd < item.SelectionStart)) throw new InvalidDataException("Invalid document state.");
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
