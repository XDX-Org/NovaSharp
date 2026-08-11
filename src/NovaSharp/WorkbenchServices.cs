using System.Collections.Concurrent;
using System.Text.Json;

namespace NovaSharp;

internal sealed record EditorSettings(int SchemaVersion = 1, bool WordWrap = false, int TabSize = 4,
    string[]? ExplorerIgnoredNames = null, bool AutoCompletion = true, bool SemanticHighlighting = true,
    string Theme = "Rider Dark", int Zoom = 100, bool ReducedMotion = false, bool HighContrast = false,
    bool Ligatures = false, string PopupPlacement = "Contextual", Dictionary<string, string>? Keybindings = null,
    bool BraceGuides = false);

internal sealed class ConfigurationService(string userPath, string? workspacePath = null)
{
    private string? _workspacePath = workspacePath;
    internal EditorSettings Current { get; private set; } = new();

    internal async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var workspace = await ReadAsync(_workspacePath, cancellationToken);
        var user = await ReadAsync(userPath, cancellationToken);
        Current = IsValid(workspace) ? workspace! : IsValid(user) ? user! : new();
    }

    private static bool IsValid(EditorSettings? settings) => settings is not null
            && settings.SchemaVersion == 1 && settings.TabSize is >= 1 and <= 16
            && settings.Zoom is >= 50 and <= 200 && settings.Theme is ("Rider Dark" or "Light")
            && settings.PopupPlacement is ("Contextual" or "TopLeft" or "TopCenter" or "TopRight"
                or "LeftCenter" or "RightCenter" or "BottomLeft" or "BottomCenter" or "BottomRight")
            && settings.ExplorerIgnoredNames?.Any(name => string.IsNullOrWhiteSpace(name)
                || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) != true
            && settings.Keybindings is not { Count: > 256 }
            && settings.Keybindings?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !KeyGesture.IsValid(pair.Value)) != true;

    internal async Task SaveUserAsync(EditorSettings settings, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        await AtomicFile.WriteAsync(userPath, bytes, cancellationToken);
        Current = settings;
    }

    internal async Task UseWorkspaceAsync(string? storagePath, CancellationToken cancellationToken = default)
    {
        _workspacePath = storagePath;
        await LoadAsync(cancellationToken);
    }

    internal async Task SaveWorkspaceAsync(EditorSettings settings, CancellationToken cancellationToken = default)
    {
        if (_workspacePath is null) throw new InvalidOperationException("Open a workspace before saving workspace settings.");
        await AtomicFile.WriteAsync(_workspacePath, JsonSerializer.SerializeToUtf8Bytes(settings), cancellationToken);
        Current = settings;
    }

    private static async Task<EditorSettings?> ReadAsync(string? path, CancellationToken cancellationToken)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<EditorSettings>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}

internal static class KeyGesture
{
    internal static bool IsValid(string value)
    {
        if (value == "LeftShift+LeftShift") return true;
        var parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length is >= 1 and <= 4 && parts[^1].Length is >= 1 and <= 24
            && parts[..^1].All(part => part is "Ctrl" or "Alt" or "Shift" or "Meta")
            && parts[..^1].Distinct(StringComparer.Ordinal).Count() == parts.Length - 1;
    }
}

internal sealed record CommandDescriptor(string Id, string Title, string? Keybinding,
    Func<bool> CanExecute, Func<CancellationToken, Task> Execute);

internal sealed class CommandRegistry
{
    private readonly Dictionary<string, CommandDescriptor> _commands = new(StringComparer.Ordinal);
    internal IReadOnlyCollection<CommandDescriptor> Commands => _commands.Values;
    internal void Register(CommandDescriptor command)
    {
        if (!_commands.TryAdd(command.Id, command)) throw new InvalidOperationException($"Command '{command.Id}' is already registered.");
    }
    internal Task ExecuteAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_commands.TryGetValue(id, out var command)) throw new KeyNotFoundException(id);
        return command.CanExecute() ? command.Execute(cancellationToken) : Task.CompletedTask;
    }
}

internal enum NotificationSeverity { Information, Warning, Error }
internal sealed record Notification(NotificationSeverity Severity, string Code, string Message, DateTime TimestampUtc);

internal sealed class NotificationLog(int capacity = 200)
{
    private readonly Queue<Notification> _entries = new();
    internal IReadOnlyCollection<Notification> Entries => _entries;
    internal void Add(NotificationSeverity severity, string code, string message)
    {
        var safeMessage = Path.IsPathRooted(message) ? "[path redacted]" : message;
        _entries.Enqueue(new(severity, code, safeMessage, DateTime.UtcNow));
        while (_entries.Count > capacity) _entries.Dequeue();
    }
}

internal sealed class LifetimeCoordinator : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<int, Task> _tasks = new();
    private int _nextId;
    internal CancellationToken Token => _shutdown.Token;

    internal Task Run(Func<CancellationToken, Task> operation)
    {
        var id = Interlocked.Increment(ref _nextId);
        var task = operation(_shutdown.Token);
        _tasks[id] = task;
        _ = task.ContinueWith(completed => _tasks.TryRemove(id, out var removed), TaskScheduler.Default);
        return task;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try { await Task.WhenAll(_tasks.Values); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
    }
}
