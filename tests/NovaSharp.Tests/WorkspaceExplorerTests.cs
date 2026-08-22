using System.Diagnostics;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Workspace;
using Xunit;

namespace NovaSharp.Tests;

public sealed class WorkspaceExplorerTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-workspace").FullName;
    private readonly BoundedWorkQueue _background = new(32, 2);
    private readonly DocumentFileStore _store = new();
    private readonly WorkspacePaths _paths = new();
    private readonly FakeWorkspaceWatcher _watcher = new();
    private readonly NotificationService _notifications = new(new BoundedWorkbenchLog());
    private readonly WorkspaceExplorerService _explorer;

    public WorkspaceExplorerTests()
    {
        var stateRoot = Path.Combine(_root, ".state");
        _explorer = new WorkspaceExplorerService(
            _paths,
            new WorkspaceFileSystem(_paths, _background),
            _watcher,
            new WorkspacePersistenceService(new FakeApplicationPaths(stateRoot), _store, _background),
            _notifications);
    }

    private sealed class FakeApplicationPaths(string path) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = path;
    }

    [Fact]
    public async Task OpenAsync_LoadsOnlyTheRootAndHonoursDefaultIgnores()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        await File.WriteAllTextAsync(Path.Combine(_root, "Widget.cs"), "class Widget;", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.data"), "unknown", TestContext.Current.CancellationToken);

        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);

        var root = _explorer.Snapshot.Root!;
        Assert.Equal(_paths.Canonicalize(_root), _explorer.Snapshot.RootPath);
        Assert.True(root.IsExpanded);
        Assert.DoesNotContain(root.Children!, node => node.Name is ".git" or "bin");
        Assert.Equal(WorkspaceNodeKind.Directory, root.Children!.Single(node => node.Name == "src").Kind);
        Assert.Null(root.Children!.Single(node => node.Name == "src").Children);
        Assert.Equal(WorkspaceNodeKind.SupportedFile, root.Children!.Single(node => node.Name == "Widget.cs").Kind);
        Assert.Equal(WorkspaceNodeKind.UnknownFile, root.Children!.Single(node => node.Name == "notes.data").Kind);
    }

    [Fact]
    public async Task ExpandAsync_IsLazyAndCollapseRetainsItsLoadedState()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        await File.WriteAllTextAsync(Path.Combine(directory, "Nested.cs"), "class Nested;", TestContext.Current.CancellationToken);
        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);

        await _explorer.ExpandAsync(directory, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(Find(directory)!.Children!, child => child.Name == "Nested.cs");

        await _explorer.CollapseAsync(directory, TestContext.Current.CancellationToken);
        Assert.False(Find(directory)!.IsExpanded);
        Assert.Contains(Find(directory)!.Children!, child => child.Name == "Nested.cs");
    }

    [Fact]
    public async Task WatcherChange_RefreshesOnlyAnExpandedAffectedBranchAndPreservesSelection()
    {
        var left = Directory.CreateDirectory(Path.Combine(_root, "left")).FullName;
        var right = Directory.CreateDirectory(Path.Combine(_root, "right")).FullName;
        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);
        await _explorer.ExpandAsync(left, cancellationToken: TestContext.Current.CancellationToken);
        await _explorer.ExpandAsync(right, cancellationToken: TestContext.Current.CancellationToken);
        await _explorer.SelectAsync(right, TestContext.Current.CancellationToken);
        var selected = _explorer.Snapshot.SelectedId;

        var created = Path.Combine(left, "external.cs");
        await File.WriteAllTextAsync(created, "class External;", TestContext.Current.CancellationToken);
        await _watcher.NotifyAsync(new WorkspaceChangeBatch([new WorkspaceChange(WorkspaceChangeKind.Created, created)]));

        Assert.Contains(Find(left)!.Children!, child => child.Name == "external.cs");
        Assert.Equal(selected, _explorer.Snapshot.SelectedId);
    }

    [Fact]
    public async Task WatcherOverflow_RescansExpandedBranchesAndReportsRecovery()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "src")).FullName;
        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);
        await _explorer.ExpandAsync(directory, cancellationToken: TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "after-overflow.cs"), "class A;", TestContext.Current.CancellationToken);

        await _watcher.NotifyAsync(new WorkspaceChangeBatch([], Overflowed: true));

        Assert.Contains(Find(directory)!.Children!, child => child.Name == "after-overflow.cs");
        Assert.Equal(1, _explorer.Snapshot.Metrics.WatcherOverflows);
        Assert.Contains(_notifications.Active, notice => notice.Id == "novasharp.workspace.watcherOverflow");
    }

    [Fact]
    public async Task CreateRenameMoveAndDelete_AreSerializedAndPublishRelocation()
    {
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);
        await _explorer.ExpandAsync(source, cancellationToken: TestContext.Current.CancellationToken);
        await _explorer.ExpandAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        await _explorer.CreateAsync(source, "Widget.cs", directory: false, TestContext.Current.CancellationToken);
        var original = Path.Combine(source, "Widget.cs");
        Assert.True(File.Exists(original));

        WorkspaceRelocation? relocation = null;
        _explorer.Relocated += value => { relocation = value; return Task.CompletedTask; };
        await _explorer.RenameAsync(original, "Renamed.cs", TestContext.Current.CancellationToken);
        var renamed = Path.Combine(source, "Renamed.cs");
        Assert.Equal(_paths.Canonicalize(renamed), relocation?.NewPath);

        await _explorer.MoveAsync(renamed, target, TestContext.Current.CancellationToken);
        var moved = Path.Combine(target, "Renamed.cs");
        Assert.True(File.Exists(moved));
        await _explorer.DeleteAsync(moved, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(moved));
    }

    [Fact]
    public async Task RevealAsync_ShowsAnExplicitlyOpenedIgnoredFile()
    {
        var ignored = Directory.CreateDirectory(Path.Combine(_root, "obj")).FullName;
        var file = Path.Combine(ignored, "Generated.cs");
        await File.WriteAllTextAsync(file, "class Generated;", TestContext.Current.CancellationToken);
        await _explorer.OpenAsync(_root, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(_explorer.Snapshot.Root!.Children!, child => child.Name == "obj");

        await _explorer.RevealAsync(file, TestContext.Current.CancellationToken);

        Assert.NotNull(Find(ignored));
        Assert.NotNull(Find(file));
        Assert.Equal(_paths.ToDocumentUri(file).AbsoluteUri, _explorer.Snapshot.SelectedId);
    }

    [Fact]
    public async Task SymbolicLinkNode_IsVisibleButNeverTraversed()
    {
        var fakeFiles = new RecordingFileSystem([
            new WorkspaceEntry(Path.Combine(_root, "loop"), "loop", WorkspaceNodeKind.SymbolicLink),
        ]);
        await using var explorer = new WorkspaceExplorerService(
            _paths,
            fakeFiles,
            new FakeWorkspaceWatcher(),
            new WorkspacePersistenceService(new FakeApplicationPaths(Path.Combine(_root, ".state2")), _store, _background),
            _notifications);

        await explorer.OpenAsync(_root, TestContext.Current.CancellationToken);
        var link = explorer.Snapshot.Root!.Children!.Single();
        await explorer.ExpandAsync(link.Path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, fakeFiles.EnumerationCount);
        Assert.False(link.CanExpand);
    }

    [Fact]
    public async Task TwentyThousandEntries_AreEnumeratedWithinTheNamedBudget()
    {
        var entries = Enumerable.Range(0, 20_000)
            .Select(index => new WorkspaceEntry(Path.Combine(_root, $"file-{index:D5}.cs"), $"file-{index:D5}.cs", WorkspaceNodeKind.SupportedFile))
            .ToArray();
        var fakeFiles = new RecordingFileSystem(entries);
        await using var explorer = new WorkspaceExplorerService(
            _paths,
            fakeFiles,
            new FakeWorkspaceWatcher(),
            new WorkspacePersistenceService(new FakeApplicationPaths(Path.Combine(_root, ".state3")), _store, _background),
            _notifications);
        var timer = Stopwatch.StartNew();

        await explorer.OpenAsync(_root, TestContext.Current.CancellationToken);

        timer.Stop();
        Assert.Equal(20_000, explorer.Snapshot.Root!.Children!.Count);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Enumeration took {timer.Elapsed}.");
    }

    [Fact]
    public async Task RapidExpansion_CancelsTheStaleReadAndPublishesOnlyTheLatestResult()
    {
        var child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        var files = new SupersedingFileSystem(_root, child);
        await using var explorer = new WorkspaceExplorerService(
            _paths,
            files,
            new FakeWorkspaceWatcher(),
            new WorkspacePersistenceService(new FakeApplicationPaths(Path.Combine(_root, ".state4")), _store, _background),
            _notifications);
        await explorer.OpenAsync(_root, TestContext.Current.CancellationToken);

        var stale = explorer.ExpandAsync(child, cancellationToken: TestContext.Current.CancellationToken);
        await files.FirstChildReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var latest = explorer.ExpandAsync(child, cancellationToken: TestContext.Current.CancellationToken);
        await Task.WhenAll(stale, latest);

        var childNode = Find(explorer.Snapshot.Root!, _paths.ToDocumentUri(child).AbsoluteUri)!;
        Assert.Contains(childNode.Children!, node => node.Name == "latest.cs");
        Assert.True(explorer.Snapshot.Metrics.CanceledEnumerations >= 1);
        Assert.Equal(0, explorer.Snapshot.Metrics.ActiveEnumerations);
    }

    [Fact]
    public async Task EnumerationFailure_IsRecoverableAndReported()
    {
        await using var explorer = new WorkspaceExplorerService(
            _paths,
            new FailingFileSystem(),
            new FakeWorkspaceWatcher(),
            new WorkspacePersistenceService(new FakeApplicationPaths(Path.Combine(_root, ".state5")), _store, _background),
            _notifications);

        await explorer.OpenAsync(_root, TestContext.Current.CancellationToken);

        Assert.NotNull(explorer.Snapshot.Root?.Error);
        Assert.Contains(_notifications.Active, notice => notice.Id == "novasharp.workspace.enumerate");
        await explorer.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Null(explorer.Snapshot.Root);
    }

    private WorkspaceNode? Find(string path) => Find(_explorer.Snapshot.Root!, _paths.ToDocumentUri(path).AbsoluteUri);

    private static WorkspaceNode? Find(WorkspaceNode node, string id)
    {
        if (node.Id == id) return node;
        if (node.Children is null) return null;
        foreach (var child in node.Children)
        {
            var found = Find(child, id);
            if (found is not null) return found;
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _explorer.DisposeAsync();
        await _background.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}

internal sealed class FakeWorkspaceWatcher : IWorkspaceWatcher
{
    public event Func<WorkspaceChangeBatch, Task>? Changed;
    public int Capacity => 8;
    public int PendingCount => 0;
    public string? Root { get; private set; }
    public void Watch(string? root) => Root = root;
    public Task NotifyAsync(WorkspaceChangeBatch batch) => Changed?.Invoke(batch) ?? Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class RecordingFileSystem(IReadOnlyList<WorkspaceEntry> entries) : IWorkspaceFileSystem
{
    public int EnumerationCount { get; private set; }
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(string root, string directory, IReadOnlyList<string> ignoredPaths, string? explicitlyVisiblePath, CancellationToken cancellationToken)
    {
        EnumerationCount++;
        return Task.FromResult(entries);
    }
    public Task CreateAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task MoveAsync(string source, string target, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class SupersedingFileSystem(string root, string child) : IWorkspaceFileSystem
{
    private int _childReads;
    public TaskCompletionSource FirstChildReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(false);
    public async Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(string workspaceRoot, string directory, IReadOnlyList<string> ignoredPaths, string? explicitlyVisiblePath, CancellationToken cancellationToken)
    {
        if (directory == root) return [new WorkspaceEntry(child, "child", WorkspaceNodeKind.Directory)];
        if (Interlocked.Increment(ref _childReads) == 1)
        {
            FirstChildReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        return [new WorkspaceEntry(Path.Combine(child, "latest.cs"), "latest.cs", WorkspaceNodeKind.SupportedFile)];
    }
    public Task CreateAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task MoveAsync(string source, string target, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class FailingFileSystem : IWorkspaceFileSystem
{
    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(true);
    public Task<bool> PathExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<IReadOnlyList<WorkspaceEntry>> EnumerateAsync(string root, string directory, IReadOnlyList<string> ignoredPaths, string? explicitlyVisiblePath, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<WorkspaceEntry>>(new UnauthorizedAccessException("Access denied by the fixture."));
    public Task CreateAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task MoveAsync(string source, string target, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task DeleteAsync(string path, bool directory, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class WorkspacePersistenceTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("novasharp-state").FullName;
    private readonly BoundedWorkQueue _queue = new(8, 1);
    private readonly DocumentFileStore _store = new();

    private WorkspacePersistenceService Create() =>
        new(new FakePaths(_root), _store, _queue);

    private sealed class FakePaths(string path) : IApplicationPaths
    {
        public string ConfigurationDirectory { get; } = path;
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsVersionedPortableState()
    {
        var service = Create();
        var state = new WorkspaceStateDocument
        {
            WorkspacePath = _root,
            ExpandedPaths = ["", "src", "src/generated"],
            SelectedPath = "src/Widget.cs",
            SidebarWidth = 340,
        };

        await service.SaveAsync(state, TestContext.Current.CancellationToken);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Null(loaded.Problem);
        Assert.Equal(WorkspaceStateDocument.CurrentSchemaVersion, loaded.State.SchemaVersion);
        Assert.Equal(state.ExpandedPaths, loaded.State.ExpandedPaths);
        Assert.Equal(340, loaded.State.SidebarWidth);
    }

    [Fact]
    public async Task LoadAsync_BacksUpCorruptionAndFallsBack()
    {
        var service = Create();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(service.FilePath, "{ broken", TestContext.Current.CancellationToken);

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(loaded.Problem);
        Assert.True(File.Exists(service.FilePath));
        Assert.True(File.Exists(service.FilePath + ".invalid"));
        Assert.Null(loaded.State.WorkspacePath);
    }

    public async ValueTask DisposeAsync()
    {
        await _queue.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}

public sealed class FileSystemWorkspaceWatcherTests
{
    [Fact]
    public async Task ReportsAnExternalCreateThroughTheBoundedConsumer()
    {
        var root = Directory.CreateTempSubdirectory("novasharp-watcher").FullName;
        try
        {
            await using var watcher = new FileSystemWorkspaceWatcher(new WorkspacePaths(), capacity: 8);
            var received = new TaskCompletionSource<WorkspaceChangeBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
            watcher.Changed += batch => { received.TrySetResult(batch); return Task.CompletedTask; };
            watcher.Watch(root);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            var path = Path.Combine(root, "external.cs");

            await File.WriteAllTextAsync(path, "class External;", TestContext.Current.CancellationToken);
            var batch = await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Contains(batch.Changes, change => change.Path == path);
            Assert.InRange(watcher.PendingCount, 0, watcher.Capacity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
