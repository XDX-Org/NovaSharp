namespace NovaSharp.Diagnostics;

/// <summary>How much attention a notification deserves.</summary>
public enum NotificationSeverity
{
    /// <summary>Something completed that the user asked for.</summary>
    Information,

    /// <summary>Something needs a decision, but nothing has been lost.</summary>
    Warning,

    /// <summary>Something the user asked for did not happen.</summary>
    Error,
}

/// <summary>
/// Something the user can do about a notification, named by command rather than by callback.
/// </summary>
/// <remarks>
/// A command identifier rather than a delegate, so the action a notification offers is the same action the toolbar
/// and the keybinding invoke, with the same enablement. It also keeps notifications serializable, which is what lets
/// a later phase persist or forward them.
/// </remarks>
/// <param name="CommandId">The command to run.</param>
/// <param name="Title">What the button says.</param>
public sealed record NotificationAction(string CommandId, string Title);

/// <summary>One thing NovaSharp needs to tell the user.</summary>
/// <param name="Id">Identifies this notification so a later one about the same thing replaces it.</param>
/// <param name="Severity">How much attention it deserves.</param>
/// <param name="Message">What happened, in the user's terms.</param>
/// <param name="Actions">What they can do about it.</param>
/// <param name="Raised">When it was raised.</param>
public sealed record Notification(
    string Id,
    NotificationSeverity Severity,
    string Message,
    IReadOnlyList<NotificationAction> Actions,
    DateTimeOffset Raised);

/// <summary>Where NovaSharp tells the user something.</summary>
public interface INotificationService
{
    /// <summary>Raises <paramref name="notification"/>, replacing any earlier one with the same identifier.</summary>
    void Raise(Notification notification);

    /// <summary>Raises a message with no actions.</summary>
    void Raise(string id, NotificationSeverity severity, string message);

    /// <summary>Removes the notification with <paramref name="id"/>, if it is showing.</summary>
    void Dismiss(string id);

    /// <summary>Everything currently showing, oldest first.</summary>
    IReadOnlyList<Notification> Active { get; }

    /// <summary>Raised whenever <see cref="Active"/> changes.</summary>
    event Action<IReadOnlyList<Notification>>? Changed;
}

/// <inheritdoc cref="INotificationService"/>
/// <remarks>
/// Identity-keyed and bounded. Keyed, because the same condition re-detected — a watcher firing three times for one
/// save — is one thing to tell the user, not three. Bounded, because a failure that repeats must not be able to fill
/// the screen or the heap with copies of itself.
/// </remarks>
public sealed class NotificationService : INotificationService
{
    private readonly Lock _gate = new();
    private readonly List<Notification> _active = [];
    private readonly IWorkbenchLog _log;
    private readonly TimeProvider _time;

    /// <param name="log">Every notification is logged as well as shown.</param>
    /// <param name="capacity">How many notifications may be showing before the oldest is dropped.</param>
    /// <param name="timeProvider">Where timestamps come from.</param>
    public NotificationService(IWorkbenchLog log, int capacity = 20, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _log = log;
        Capacity = capacity;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many notifications may be showing at once.</summary>
    public int Capacity { get; }

    /// <inheritdoc />
    public IReadOnlyList<Notification> Active
    {
        get
        {
            lock (_gate)
            {
                return [.. _active];
            }
        }
    }

    /// <inheritdoc />
    public event Action<IReadOnlyList<Notification>>? Changed;

    /// <inheritdoc />
    public void Raise(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);

        IReadOnlyList<Notification> snapshot;
        lock (_gate)
        {
            _active.RemoveAll(existing => existing.Id == notification.Id);
            _active.Add(notification);

            while (_active.Count > Capacity)
            {
                _active.RemoveAt(0);
            }

            snapshot = [.. _active];
        }

        // Logged whether or not anyone is watching the workbench, and already redacted: a notification is written for
        // the user, so it names the file rather than quoting the document.
        _log.Write(ToLevel(notification.Severity), "notification", notification.Message);
        Changed?.Invoke(snapshot);
    }

    /// <inheritdoc />
    public void Raise(string id, NotificationSeverity severity, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        Raise(new Notification(id, severity, message, [], _time.GetUtcNow()));
    }

    /// <inheritdoc />
    public void Dismiss(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        IReadOnlyList<Notification> snapshot;
        lock (_gate)
        {
            if (_active.RemoveAll(existing => existing.Id == id) == 0)
            {
                return;
            }

            snapshot = [.. _active];
        }

        Changed?.Invoke(snapshot);
    }

    private static LogLevel ToLevel(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => LogLevel.Error,
        NotificationSeverity.Warning => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}

/// <summary>The notification identifiers phase 2 raises.</summary>
/// <remarks>
/// Constants rather than literals at the call sites, because the identifier is what makes a repeated condition
/// replace itself instead of stacking up, and two spellings of the same intent would defeat that silently.
/// </remarks>
public static class NotificationIds
{
    /// <summary>A document could not be opened.</summary>
    public const string OpenFailed = "novasharp.document.openFailed";

    /// <summary>A document could not be saved.</summary>
    public const string SaveFailed = "novasharp.document.saveFailed";

    /// <summary>A document could not be reloaded.</summary>
    public const string ReloadFailed = "novasharp.document.reloadFailed";

    /// <summary>The file behind the open document changed underneath it.</summary>
    public const string ExternalChange = "novasharp.document.externalChange";

    /// <summary>The editor and its shadow could not be brought back into step.</summary>
    public const string ResyncFailed = "novasharp.document.resyncFailed";

    /// <summary>A document was opened with an encoding NovaSharp had to guess at.</summary>
    public const string EncodingFallback = "novasharp.document.encodingFallback";

    /// <summary>A settings file had something in it NovaSharp could not use.</summary>
    public const string SettingsProblem = "novasharp.configuration.problem";
}
