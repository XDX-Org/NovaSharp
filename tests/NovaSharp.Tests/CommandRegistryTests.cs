using NovaSharp.Commands;
using NovaSharp.Diagnostics;
using Xunit;

namespace NovaSharp.Tests;

public sealed class CommandRegistryTests
{
    private readonly BoundedWorkbenchLog _log = new();
    private readonly CommandRegistry _registry;

    public CommandRegistryTests() => _registry = new CommandRegistry(_log);

    private static CommandDescriptor Describe(string id, params string[] keybindings) =>
        new(id, "Do the thing", "Test", keybindings, ShowInPalette: true);

    [Fact]
    public async Task InvokeAsync_RunsTheHandler()
    {
        var ran = 0;
        _registry.Register(Describe("test.run"), _ =>
        {
            ran++;
            return Task.CompletedTask;
        });

        var result = await _registry.InvokeAsync("test.run", TestContext.Current.CancellationToken);

        Assert.Equal(CommandOutcome.Invoked, result.Outcome);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task InvokeAsync_ReportsAnIdentifierNothingIsRegisteredUnder()
    {
        var result = await _registry.InvokeAsync("test.absent", TestContext.Current.CancellationToken);

        Assert.Equal(CommandOutcome.Unknown, result.Outcome);
        Assert.Contains("test.absent", result.Message);
    }

    [Fact]
    public async Task InvokeAsync_RefusesADisabledCommandWithoutRunningIt()
    {
        var ran = false;
        _registry.Register(Describe("test.disabled"), _ =>
        {
            ran = true;
            return Task.CompletedTask;
        }, () => false);

        var result = await _registry.InvokeAsync("test.disabled", TestContext.Current.CancellationToken);

        Assert.Equal(CommandOutcome.Disabled, result.Outcome);
        Assert.False(ran);
    }

    [Fact]
    public async Task InvokeAsync_TurnsAThrowingHandlerIntoAResult()
    {
        // A command is invoked from a keybinding and from a button. Neither caller can do anything useful with an
        // exception, and one thrown into Monaco's action dispatch takes out every other keybinding with it.
        _registry.Register(Describe("test.throws"), _ => throw new InvalidOperationException("boom"));

        var result = await _registry.InvokeAsync("test.throws", TestContext.Current.CancellationToken);

        Assert.Equal(CommandOutcome.Failed, result.Outcome);
        Assert.Equal("boom", result.Message);
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("test.throws"));
    }

    [Fact]
    public async Task InvokeAsync_DistinguishesCancellationFromFailure()
    {
        _registry.Register(Describe("test.cancels"), async token =>
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        });

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await _registry.InvokeAsync("test.cancels", cancellation.Token);

        Assert.Equal(CommandOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public void IsEnabled_TreatsAThrowingPredicateAsUnavailable()
    {
        _registry.Register(Describe("test.badPredicate"), _ => Task.CompletedTask, () => throw new InvalidOperationException());

        Assert.False(_registry.IsEnabled("test.badPredicate"));
        Assert.Contains(_log.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void Register_NormalizesKeybindingsIntoMonacosVocabulary()
    {
        _registry.Register(Describe("test.bound", "CtrlCmd+S", "ctrlcmd+shift+s", "F5"), _ => Task.CompletedTask);

        var descriptor = _registry.Find("test.bound");

        Assert.Equal(["CtrlCmd+KeyS", "CtrlCmd+Shift+KeyS", "F5"], descriptor?.Keybindings);
    }

    [Fact]
    public void Register_RefusesAKeybindingThatCouldNotBeBound()
    {
        // Rejected here rather than ignored in the browser: a binding that resolves to nothing is a shortcut that
        // silently does nothing, which the user cannot tell from a broken command.
        var failure = Assert.Throws<ArgumentException>(
            () => _registry.Register(Describe("test.bad", "Ctrl+S"), _ => Task.CompletedTask));

        Assert.Contains("Ctrl", failure.Message);
    }

    [Fact]
    public void Register_RefusesTheSameIdentifierTwice()
    {
        _registry.Register(Describe("test.duplicate"), _ => Task.CompletedTask);

        Assert.Throws<InvalidOperationException>(
            () => _registry.Register(Describe("test.duplicate"), _ => Task.CompletedTask));
    }

    [Fact]
    public async Task Dispose_UnregistersTheCommand()
    {
        var registration = _registry.Register(Describe("test.temporary"), _ => Task.CompletedTask);
        Assert.NotNull(_registry.Find("test.temporary"));

        registration.Dispose();

        Assert.Null(_registry.Find("test.temporary"));
        Assert.Equal(
            CommandOutcome.Unknown,
            (await _registry.InvokeAsync("test.temporary", TestContext.Current.CancellationToken)).Outcome);
    }

    [Fact]
    public void Changed_ReportsRegistrationAndRemoval()
    {
        var changes = 0;
        _registry.Changed += () => changes++;

        var registration = _registry.Register(Describe("test.watched"), _ => Task.CompletedTask);
        registration.Dispose();

        Assert.Equal(2, changes);
    }

    [Fact]
    public void WorkbenchCommands_AreAllDescribableAndBindable()
    {
        // Every identifier the workbench uses must have a descriptor whose bindings the registry accepts, or the
        // command exists in code and nowhere the user can reach it.
        foreach (var id in WorkbenchCommands.All)
        {
            var descriptor = WorkbenchCommands.Describe(id);
            Assert.Equal(id, descriptor.Id);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Title));
            _registry.Register(descriptor, _ => Task.CompletedTask);
        }

        Assert.Equal(WorkbenchCommands.All.Count, _registry.Commands.Count);
    }
}
