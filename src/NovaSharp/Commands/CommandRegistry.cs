using NovaSharp.Diagnostics;

namespace NovaSharp.Commands;

/// <summary>What the workbench knows about a command besides how to run it.</summary>
/// <param name="Id">The stable identifier every caller uses.</param>
/// <param name="Title">What the user sees.</param>
/// <param name="Category">Groups related commands in menus and the palette.</param>
/// <param name="Keybindings">Normalized bindings, in Monaco's vocabulary.</param>
/// <param name="ShowInPalette">Whether the command is offered when the user goes looking for it.</param>
public sealed record CommandDescriptor(
    string Id,
    string Title,
    string Category,
    IReadOnlyList<string> Keybindings,
    bool ShowInPalette);

/// <summary>What happened when a command was invoked.</summary>
public enum CommandOutcome
{
    /// <summary>The handler ran to completion.</summary>
    Invoked,

    /// <summary>Nothing is registered under that identifier.</summary>
    Unknown,

    /// <summary>The command exists but is not applicable right now.</summary>
    Disabled,

    /// <summary>The handler threw.</summary>
    Failed,

    /// <summary>The handler was cancelled.</summary>
    Cancelled,
}

/// <summary>The result of invoking a command.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">Why, when it was not <see cref="CommandOutcome.Invoked"/>.</param>
public sealed record CommandResult(CommandOutcome Outcome, string? Message = null);

/// <summary>The one place a command identifier turns into behaviour.</summary>
public interface ICommandRegistry
{
    /// <summary>Registers <paramref name="descriptor"/>. Disposing the result unregisters it.</summary>
    /// <param name="descriptor">What the workbench shows and binds.</param>
    /// <param name="handler">What the command does.</param>
    /// <param name="isEnabled">Whether it applies right now. <see langword="null"/> means always.</param>
    IDisposable Register(CommandDescriptor descriptor, Func<CancellationToken, Task> handler, Func<bool>? isEnabled = null);

    /// <summary>Every registered command, in registration order.</summary>
    IReadOnlyList<CommandDescriptor> Commands { get; }

    /// <summary>Returns the descriptor for <paramref name="id"/>, or <see langword="null"/>.</summary>
    CommandDescriptor? Find(string id);

    /// <summary>Returns whether <paramref name="id"/> is registered and currently applicable.</summary>
    bool IsEnabled(string id);

    /// <summary>Runs <paramref name="id"/>.</summary>
    Task<CommandResult> InvokeAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Raised when a command is registered or unregistered.</summary>
    event Action? Changed;
}

/// <inheritdoc cref="ICommandRegistry"/>
/// <remarks>
/// <see cref="InvokeAsync"/> never throws. A command is invoked from a keybinding, a button, and a notification's
/// action, and none of those callers is in a position to handle an exception usefully — a Monaco keybinding handler
/// that throws takes out the editor's action dispatch. Failures become a result and a log entry instead.
/// </remarks>
public sealed class CommandRegistry : ICommandRegistry
{
    private readonly Lock _gate = new();
    private readonly List<Registration> _registrations = [];
    private readonly IWorkbenchLog _log;

    /// <param name="log">Where invocation failures are recorded.</param>
    public CommandRegistry(IWorkbenchLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public IReadOnlyList<CommandDescriptor> Commands
    {
        get
        {
            lock (_gate)
            {
                return [.. _registrations.Select(registration => registration.Descriptor)];
            }
        }
    }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public IDisposable Register(
        CommandDescriptor descriptor,
        Func<CancellationToken, Task> handler,
        Func<bool>? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentException.ThrowIfNullOrWhiteSpace(descriptor.Id);

        var normalized = new List<string>(descriptor.Keybindings.Count);
        foreach (var keybinding in descriptor.Keybindings)
        {
            // Rejected at registration rather than ignored at binding time. A malformed binding that only fails inside
            // the browser is a shortcut that quietly does nothing, which is indistinguishable from a broken command.
            if (!Keybindings.TryNormalize(keybinding, out var value, out var problem))
            {
                throw new ArgumentException(
                    $"Command '{descriptor.Id}' declares the keybinding '{keybinding}', which cannot be used. {problem}",
                    nameof(descriptor));
            }

            normalized.Add(value);
        }

        var registration = new Registration(descriptor with { Keybindings = normalized }, handler, isEnabled, this);

        lock (_gate)
        {
            if (_registrations.Any(existing => existing.Descriptor.Id == descriptor.Id))
            {
                throw new InvalidOperationException($"The command '{descriptor.Id}' is already registered.");
            }

            _registrations.Add(registration);
        }

        Changed?.Invoke();
        return registration;
    }

    /// <inheritdoc />
    public CommandDescriptor? Find(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return FindRegistration(id)?.Descriptor;
    }

    /// <inheritdoc />
    public bool IsEnabled(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var registration = FindRegistration(id);
        if (registration is null)
        {
            return false;
        }

        try
        {
            return registration.IsEnabled?.Invoke() ?? true;
        }
        catch (Exception exception)
        {
            // An enablement predicate that throws must not take the menu down with it. Treating the command as
            // unavailable is the safe reading: it is at least a state the user can see.
            _log.Write(LogLevel.Error, "commands", $"Enablement for '{id}' threw and the command was hidden.", exception);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<CommandResult> InvokeAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var registration = FindRegistration(id);
        if (registration is null)
        {
            _log.Write(LogLevel.Warning, "commands", $"'{id}' was invoked but nothing is registered under it.");
            return new CommandResult(CommandOutcome.Unknown, $"'{id}' is not a command NovaSharp knows.");
        }

        if (!IsEnabled(id))
        {
            return new CommandResult(CommandOutcome.Disabled, $"{registration.Descriptor.Title} is not available right now.");
        }

        try
        {
            await registration.Handler(cancellationToken).ConfigureAwait(false);
            return new CommandResult(CommandOutcome.Invoked);
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(CommandOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            _log.Write(LogLevel.Error, "commands", $"'{id}' failed.", exception);
            return new CommandResult(CommandOutcome.Failed, exception.Message);
        }
    }

    private Registration? FindRegistration(string id)
    {
        lock (_gate)
        {
            return _registrations.FirstOrDefault(registration => registration.Descriptor.Id == id);
        }
    }

    private void Unregister(Registration registration)
    {
        lock (_gate)
        {
            if (!_registrations.Remove(registration))
            {
                return;
            }
        }

        Changed?.Invoke();
    }

    private sealed class Registration(
        CommandDescriptor descriptor,
        Func<CancellationToken, Task> handler,
        Func<bool>? isEnabled,
        CommandRegistry owner) : IDisposable
    {
        public CommandDescriptor Descriptor { get; } = descriptor;

        public Func<CancellationToken, Task> Handler { get; } = handler;

        public Func<bool>? IsEnabled { get; } = isEnabled;

        public void Dispose() => owner.Unregister(this);
    }
}
