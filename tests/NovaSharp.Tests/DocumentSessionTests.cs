using Microsoft.AspNetCore.Components;
using NovaSharp.Async;
using NovaSharp.Commands;
using NovaSharp.Configuration;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Text;
using Xunit;

namespace NovaSharp.Tests;

public sealed class DocumentSessionTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _queue = new(capacity: 16, workerCount: 2);
    private readonly DocumentFileStore _store = new();
    private readonly FakeEditorHost _host = new();
    private readonly FakeDocumentWatcher _watcher = new();
    private readonly BoundedWorkbenchLog _log = new();
    private readonly NotificationService _notifications;
    private readonly DocumentSession _session;
    private readonly string _directory = Directory.CreateTempSubdirectory("novasharp-session").FullName;

    public DocumentSessionTests()
    {
        var paths = new WorkspacePaths();
        var codec = new DocumentTextCodec();
        _notifications = new NotificationService(_log);
        _session = new DocumentSession(
            _host,
            new DocumentLoader(paths, _store, codec, _queue),
            new DocumentSaver(paths, _store, codec, _queue),
            _store,
            _watcher,
            _queue,
            _notifications,
            () => _settings);
    }

    private WorkbenchSettings _settings = WorkbenchSettings.Defaults;

    private bool Raised(string id) => _notifications.Active.Any(notification => notification.Id == id);

    private string Path(string name) => System.IO.Path.Combine(_directory, name);

    private async Task<string> OpenAsync(string name, string content)
    {
        var path = Path(name);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);

        await _host.InitializeAsync(
            default(ElementReference),
            new EditorBridge(_session.Replicate, _session.RequestResync, _ => Task.CompletedTask),
            TestContext.Current.CancellationToken);

        await _session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        return path;
    }

    [Fact]
    public async Task OpenAsync_ShowsAnUnmodifiedDocument()
    {
        await OpenAsync("widget.cs", "class Widget;\n");

        var status = _session.Status;
        Assert.True(status.IsOpen);
        Assert.False(status.IsDirty);
        Assert.Equal("widget.cs", status.DisplayName);
        Assert.Equal(LineEndingStyle.Lf, status.LineEnding);
        Assert.Equal("class Widget;\n", _session.Replica?.Snapshot().Text);
    }

    [Fact]
    public async Task OpenAsync_ReportsAFileItCannotRead()
    {
        await _host.InitializeAsync(
            default(ElementReference),
            new EditorBridge(_session.Replicate, _session.RequestResync, _ => Task.CompletedTask),
            TestContext.Current.CancellationToken);

        await _session.OpenAsync(Path("absent.cs"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(_session.Status.IsOpen);
        Assert.True(Raised(NotificationIds.OpenFailed));
    }

    [Fact]
    public async Task Typing_MakesTheDocumentDirtyAndUndoingMakesItCleanAgain()
    {
        await OpenAsync("widget.cs", "class Widget;\n");
        var savedAlternative = _host.AlternativeSequence;

        _host.Type("// more\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        // Undo returns Monaco's alternative version to what it was, which is the whole reason dirty state is compared
        // against that counter rather than against the version identifier.
        _host.UndoTo(14, _host.Text.Length, string.Empty, savedAlternative);
        await WaitForAsync(() => !_session.Status.IsDirty);
    }

    [Fact]
    public async Task SaveAsync_WritesWhatTheEditorHasAndClearsDirtyState()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");

        _host.Type("class Gadget;\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        var result = await _session.SaveAsync();

        Assert.Equal(DocumentSaveStatus.Saved, result?.Status);
        Assert.Equal("class Widget;\nclass Gadget;\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(_session.Status.IsDirty);
    }

    [Fact]
    public async Task SaveAsync_WaitsForTheShadowToCatchUpWithTheEditor()
    {
        // The barrier. Monaco is ahead of the shadow because the batches have not been delivered yet; a save must not
        // write the older text it can currently see.
        var path = await OpenAsync("widget.cs", "class Widget;\n");

        _host.Held = [];
        _host.Type("// typed while saving\n");

        var save = _session.SaveAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(save.IsCompleted, "The save must not write before the shadow has caught up.");

        _host.ReleaseHeld();

        var result = await save.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(DocumentSaveStatus.Saved, result?.Status);
        Assert.Equal("class Widget;\n// typed while saving\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsAsync_ContinuesEditingTheNewFile()
    {
        await OpenAsync("original.cs", "class Widget;\n");
        var target = Path("renamed.cs");

        var result = await _session.SaveAsAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Saved, result?.Status);
        Assert.Equal("renamed.cs", _session.Status.DisplayName);
        Assert.Equal(target, _watcher.Watching);
    }

    [Fact]
    public async Task SaveWithEncodingAsync_RefusesRatherThanLosingCharacters()
    {
        var path = await OpenAsync("music.cs", "// 𝄞\n");

        var result = await _session.SaveWithEncodingAsync(TextEncodings.Latin1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(DocumentSaveStatus.Unrepresentable, result?.Status);
        Assert.True(Raised(NotificationIds.SaveFailed));
        Assert.Equal("// 𝄞\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalChange_ToACleanDocumentIsAdoptedSilently()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "changed elsewhere\n", TestContext.Current.CancellationToken);
        _watcher.Notify();

        await WaitForAsync(() => _session.Replica?.Snapshot().Text == "changed elsewhere\n");
        Assert.False(_session.Status.IsDirty);
        Assert.Equal(ExternalChangeState.None, _session.Status.ExternalChange);
    }

    [Fact]
    public async Task ExternalChange_ToADirtyDocumentAsksInsteadOfOverwritingIt()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "theirs\n", TestContext.Current.CancellationToken);
        _watcher.Notify();

        await WaitForAsync(() => _session.Status.ExternalChange == ExternalChangeState.Modified);
        Assert.Equal("class Widget;\nmine\n", _host.Text);
        Assert.True(_session.Status.IsDirty);
    }

    [Fact]
    public async Task ExternalChange_NoticesADeletedFile()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        File.Delete(path);
        _watcher.Notify();

        await WaitForAsync(() => _session.Status.ExternalChange == ExternalChangeState.Deleted);
    }

    [Fact]
    public async Task ResolveExternalChangeAsync_KeepingStopsTheQuestionWithoutTouchingTheFile()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "theirs\n", TestContext.Current.CancellationToken);
        _watcher.Notify();
        await WaitForAsync(() => _session.Status.ExternalChange == ExternalChangeState.Modified);

        await _session.ResolveExternalChangeAsync(ExternalChangeChoice.Keep, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(ExternalChangeState.None, _session.Status.ExternalChange);
        Assert.Equal("theirs\n", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("class Widget;\nmine\n", _host.Text);
    }

    [Fact]
    public async Task ReloadAsync_ReplacesBothTheEditorAndTheShadowWithoutARoundTrip()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await File.WriteAllTextAsync(path, "from disk\n", TestContext.Current.CancellationToken);
        var snapshotsBefore = _host.SnapshotCount;

        await _session.ReloadAsync();

        Assert.Equal("from disk\n", _host.Text);
        Assert.Equal("from disk\n", _session.Replica?.Snapshot().Text);
        Assert.False(_session.Status.IsDirty);
        Assert.Equal(snapshotsBefore, _host.SnapshotCount);
    }

    [Fact]
    public async Task ReloadAsync_WithAnotherEncodingReReadsTheBytes()
    {
        var path = Path("latin.cs");
        await File.WriteAllBytesAsync(path, [0x2F, 0x2F, 0x20, 0xE9, 0x0A], TestContext.Current.CancellationToken);

        await _host.InitializeAsync(
            default(ElementReference),
            new EditorBridge(_session.Replicate, _session.RequestResync, _ => Task.CompletedTask),
            TestContext.Current.CancellationToken);
        await _session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        // Not valid UTF-8, so the open fell back to the encoding that round-trips every byte.
        Assert.True(_session.Status.DecodedWithFallback);

        await _session.ReloadAsync(TextEncodings.Latin1, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("// é\n", _host.Text);
        Assert.Equal("iso-8859-1", _session.Status.Encoding?.Id);
    }

    [Fact]
    public async Task OpenAsync_ReplacesTheDocumentAndItsShadowTogether()
    {
        await OpenAsync("first.cs", "first\n");
        _host.Type("edited\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        var second = Path("second.cs");
        await File.WriteAllTextAsync(second, "second\n", TestContext.Current.CancellationToken);
        await _session.OpenAsync(second, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("second.cs", _session.Status.DisplayName);
        Assert.False(_session.Status.IsDirty);
        Assert.Equal("second\n", _session.Replica?.Snapshot().Text);
    }

    [Fact]
    public async Task DisposeAsync_StopsWatchingAndDrainsThePump()
    {
        await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");

        await _session.DisposeAsync();

        Assert.False(_session.Replicate([]) is false);
        Assert.Null(_session.Replica);
    }

    [Fact]
    public async Task ExternalChange_RaisesANotificationOfferingTheCommandsThatAnswerIt()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "theirs\n", TestContext.Current.CancellationToken);
        _watcher.Notify();

        await WaitForAsync(() => Raised(NotificationIds.ExternalChange));

        var notification = _notifications.Active.Single(item => item.Id == NotificationIds.ExternalChange);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal(
            [WorkbenchCommands.Compare, WorkbenchCommands.Reload, WorkbenchCommands.KeepEditorText],
            notification.Actions.Select(action => action.CommandId));
    }

    [Fact]
    public async Task ExternalChange_IsNotFollowedAutomaticallyWhenTheUserHasAskedNotTo()
    {
        _settings = WorkbenchSettings.Defaults with { ReloadUnmodifiedFiles = false };
        var path = await OpenAsync("widget.cs", "class Widget;\n");

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "changed elsewhere\n", TestContext.Current.CancellationToken);
        _watcher.Notify();

        await WaitForAsync(() => Raised(NotificationIds.ExternalChange));
        Assert.Equal("class Widget;\n", _session.Replica?.Snapshot().Text);
    }

    [Fact]
    public async Task ResolveExternalChangeAsync_KeepingDismissesTheNotification()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "theirs\n", TestContext.Current.CancellationToken);
        _watcher.Notify();
        await WaitForAsync(() => Raised(NotificationIds.ExternalChange));

        await _session.ResolveExternalChangeAsync(ExternalChangeChoice.Keep, TestContext.Current.CancellationToken);

        Assert.False(Raised(NotificationIds.ExternalChange));
        Assert.Equal(ExternalChangeState.None, _session.Status.ExternalChange);
    }

    [Fact]
    public async Task OpenAsync_SaysSoWhenItHadToGuessTheEncoding()
    {
        var path = Path("latin.cs");
        await File.WriteAllBytesAsync(path, [0x2F, 0x2F, 0x20, 0xE9, 0x0A], TestContext.Current.CancellationToken);

        await _host.InitializeAsync(
            default(ElementReference),
            new EditorBridge(_session.Replicate, _session.RequestResync, _ => Task.CompletedTask),
            TestContext.Current.CancellationToken);
        await _session.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);

        // A document NovaSharp guessed the encoding of is one where "save" means something different from what the
        // user expects, so it is said out loud rather than left in the status bar to be noticed.
        Assert.True(Raised(NotificationIds.EncodingFallback));
        Assert.Equal(
            WorkbenchCommands.ChooseEncoding,
            _notifications.Active.Single(item => item.Id == NotificationIds.EncodingFallback).Actions[0].CommandId);
    }

    [Fact]
    public async Task ReadFileTextAsync_ReturnsTheFileDecodedTheWayTheDocumentWas()
    {
        // The original side of a comparison. It has to be decoded and normalized exactly as the editor's text was, or
        // the diff shows differences that are artefacts of how the two sides were read.
        var path = await OpenAsync("widget.cs", "a\r\nb\r\n");
        _host.Type("edited\n");

        Assert.Equal("a\r\nb\r\n", await _session.ReadFileTextAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_DismissesTheExternalChangeNoticeItSettles()
    {
        var path = await OpenAsync("widget.cs", "class Widget;\n");
        _host.Type("mine\n");
        await WaitForAsync(() => _session.Status.IsDirty);

        await Task.Delay(20, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "theirs\n", TestContext.Current.CancellationToken);
        _watcher.Notify();
        await WaitForAsync(() => Raised(NotificationIds.ExternalChange));

        await _session.SaveAsync(overwriteExternalChange: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(Raised(NotificationIds.ExternalChange));
        Assert.Equal(ExternalChangeState.None, _session.Status.ExternalChange);
        Assert.False(_session.Status.IsDirty);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Fail("The session did not reach the expected state within its deadline.");
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        await _queue.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }
}
