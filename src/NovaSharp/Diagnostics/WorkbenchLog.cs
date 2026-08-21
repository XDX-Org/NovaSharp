namespace NovaSharp.Diagnostics;

/// <summary>How serious a log entry is.</summary>
public enum LogLevel
{
    /// <summary>Detail useful only when something is being investigated.</summary>
    Debug,

    /// <summary>Something worth knowing happened.</summary>
    Information,

    /// <summary>Something is wrong but was handled.</summary>
    Warning,

    /// <summary>Something failed.</summary>
    Error,
}

/// <summary>One entry in the workbench log.</summary>
/// <param name="Timestamp">When it was written.</param>
/// <param name="Level">How serious it is.</param>
/// <param name="Category">Which part of NovaSharp wrote it.</param>
/// <param name="Message">What happened. Already redacted by the time it gets here.</param>
/// <param name="Exception">The failure's type and message, if there was one.</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>Where NovaSharp writes what it did.</summary>
public interface IWorkbenchLog
{
    /// <summary>Writes an entry.</summary>
    /// <param name="level">How serious it is.</param>
    /// <param name="category">Which part of NovaSharp is writing.</param>
    /// <param name="message">What happened. Must already be free of document text and full paths.</param>
    /// <param name="exception">The failure, if there was one.</param>
    void Write(LogLevel level, string category, string message, Exception? exception = null);

    /// <summary>The entries still held, oldest first.</summary>
    IReadOnlyList<LogEntry> Entries { get; }
}

/// <summary>
/// An in-memory log that keeps the most recent entries and discards the rest.
/// </summary>
/// <remarks>
/// Bounded by construction rather than trimmed when someone notices it has grown. A log is background work reporting
/// on foreground work, so it must not be able to consume memory in proportion to how long the application has been
/// running or to how badly something is failing — a retry loop writing an entry per attempt is precisely when an
/// unbounded log would do the most damage.
/// </remarks>
public sealed class BoundedWorkbenchLog : IWorkbenchLog
{
    private readonly Lock _gate = new();
    private readonly Queue<LogEntry> _entries;
    private readonly TimeProvider _time;

    /// <param name="capacity">How many entries are kept before the oldest is dropped.</param>
    /// <param name="timeProvider">Where timestamps come from. Injected so tests are not timing-dependent.</param>
    public BoundedWorkbenchLog(int capacity = 500, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Capacity = capacity;
        _entries = new Queue<LogEntry>(capacity);
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many entries this log holds.</summary>
    public int Capacity { get; }

    /// <summary>How many entries have been dropped to stay within <see cref="Capacity"/>.</summary>
    public int DroppedCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries];
            }
        }
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(message);

        // The type and message, not the stack trace: a trace carries file paths from the machine NovaSharp was built
        // on, which the shipped-binary rule keeps out of the product in the first place.
        var described = exception is null ? null : $"{exception.GetType().Name}: {exception.Message}";
        var entry = new LogEntry(_time.GetUtcNow(), level, category, message, described);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
            {
                _entries.Dequeue();
                DroppedCount++;
            }
        }
    }
}
