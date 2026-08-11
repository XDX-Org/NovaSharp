using System.Collections.Concurrent;
using System.Text.Json;

namespace NovaSharp;

internal sealed record EditorSettings(int SchemaVersion = 1, bool WordWrap = false, int TabSize = 4,
    string[]? ExplorerIgnoredNames = null, bool AutoCompletion = true, bool SemanticHighlighting = true,
    string Theme = "Rider Dark", int Zoom = 100, bool ReducedMotion = false, bool HighContrast = false,
    bool Ligatures = false);

internal sealed class ConfigurationService(string userPath, string? workspacePath = null)
{
    internal EditorSettings Current { get; private set; } = new();

    internal async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Current = await ReadAsync(workspacePath, cancellationToken)
            ?? await ReadAsync(userPath, cancellationToken) ?? new();
        if (Current.SchemaVersion != 1 || Current.TabSize is < 1 or > 16
            || Current.Zoom is < 50 or > 200 || Current.Theme is not ("Rider Dark" or "Light")
            || Current.ExplorerIgnoredNames?.Any(name => string.IsNullOrWhiteSpace(name)
                || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) == true)
            Current = new();
    }

    internal async Task SaveUserAsync(EditorSettings settings, CancellationToken cancellationToken = default)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings);
        await AtomicFile.WriteAsync(userPath, bytes, cancellationToken);
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
