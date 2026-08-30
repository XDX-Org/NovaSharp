using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using NovaSharp.Workspace;
using Xunit;

namespace NovaSharp.Tests;

public sealed class SolutionWorkspaceServiceTests : IAsyncDisposable
{
    private readonly BoundedWorkQueue _background = new(capacity: 8, workerCount: 2);
    private readonly BoundedWorkbenchLog _log = new();
    private readonly NotificationService _notifications;
    private readonly WorkspacePaths _paths = new();
    private SolutionWorkspaceService? _service;

    public SolutionWorkspaceServiceTests()
    {
        _notifications = new NotificationService(_log);
    }

    [Fact]
    public async Task Load_MapsLinkedDocumentsAndCompilationSettings()
    {
        _service = Create(new AdhocSolutionLoader());
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);

        var uri = _paths.ToDocumentUri(ProjectPath("Shared.cs"));
        var contexts = _service.GetDocumentContexts(uri);

        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
        Assert.Equal(2, contexts.Count);
        Assert.All(_service.Snapshot.Projects, project => Assert.Contains("TRACE", project.Defines));
        Assert.All(_service.Snapshot.Projects, project => Assert.Equal("13.0", project.LanguageVersion));
        Assert.All(_service.Snapshot.Projects, project => Assert.Equal("Enable", project.Nullable));
        Assert.Equal(1, _service.CurrentMetrics.RetainedRoslynSnapshots);
    }

    [Fact]
    public async Task RestoreAsync_PublishesCachedTreeBeforeLiveRoslynValidation()
    {
        var loader = new GatedInitialLoader();
        var path = ProjectPath("Workspace.slnx");
        var project = new ProjectContextSnapshot(
            "cached-project",
            "Cached",
            ProjectPath("Cached.csproj"),
            "net10.0",
            true,
            [],
            [],
            [],
            "13.0",
            "Enable");
        var cache = new RecordingWarmCache(new SolutionWarmCacheEntry(
            path,
            "Workspace.slnx",
            [project],
            TimeSpan.FromMilliseconds(12)));
        _service = new SolutionWorkspaceService(
            _paths,
            loader,
            _background,
            new DiagnosticStore(),
            _notifications,
            _log,
            warmCache: cache,
            workspaceRoot: () => Path.GetDirectoryName(path));

        var restore = _service.RestoreAsync(Path.GetDirectoryName(path)!, TestContext.Current.CancellationToken);
        await loader.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SolutionLoadState.Loading, _service.Snapshot.State);
        Assert.True(_service.Snapshot.RestoredFromWarmCache);
        Assert.Single(_service.Snapshot.Projects, candidate => candidate.Name == "Cached");
        Assert.Null(_service.CurrentSolution);

        loader.Release.TrySetResult();
        Assert.True(await restore);

        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
        Assert.False(_service.Snapshot.RestoredFromWarmCache);
        Assert.True(_service.CurrentMetrics.WarmCacheHit);
        Assert.Equal(TimeSpan.FromMilliseconds(12), _service.CurrentMetrics.WarmCacheRestoreDuration);
        Assert.Equal(1, cache.SaveCount);

        await _service.CloseAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, cache.ClearCount);
    }

    [Fact]
    public async Task DirtyReplica_UpdatesEveryLinkedRoslynDocumentWithoutDiskIO()
    {
        _service = Create(new AdhocSolutionLoader());
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var path = ProjectPath("Shared.cs");
        var uri = _paths.ToDocumentUri(path);
        var replica = new DocumentReplica("internal sealed class Shared { public int Value => 42; }", 7, 7);

        _service.QueueReplica(new DocumentReplicaChange(uri, path, replica, 7));
        await _service.WaitForReplicaAsync(uri, 7, TestContext.Current.CancellationToken);

        var texts = await Task.WhenAll(_service.GetDocumentContexts(uri).Select(async context =>
            (await _service.CurrentSolution!.GetDocument(context.DocumentId)!.GetTextAsync(TestContext.Current.CancellationToken)).ToString()));
        Assert.Equal(2, texts.Length);
        Assert.All(texts, text => Assert.Contains("42", text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousDocument_UsesAnExplicitActiveProjectContext()
    {
        _service = Create(new AdhocSolutionLoader());
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var uri = _paths.ToDocumentUri(ProjectPath("Shared.cs"));
        var contexts = _service.GetDocumentContexts(uri);
        var selected = contexts.Single(context => !context.IsActive);

        await _service.SetActiveContextAsync(uri, selected.ProjectId, TestContext.Current.CancellationToken);

        var updated = _service.GetDocumentContexts(uri);
        Assert.Single(updated, context => context.IsActive && context.ProjectId == selected.ProjectId);
    }

    [Fact]
    public async Task Reload_ReportsRemovedProjectContextsWithoutClosingReplica()
    {
        var loader = new RemovingContextLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var uri = _paths.ToDocumentUri(ProjectPath("Shared.cs"));
        Assert.Equal(2, _service.GetDocumentContexts(uri).Count);

        await _service.ReloadAsync(TestContext.Current.CancellationToken);

        Assert.Single(_service.GetDocumentContexts(uri));
        Assert.Contains(_service.Snapshot.ContextChanges, change => change.Kind == "removed");
    }

    [Fact]
    public async Task LoadFailure_IsStructuredAndKeepsRawExceptionOutOfWorkbenchLogMessage()
    {
        _service = Create(new FailingSolutionLoader());
        await _service.OpenAsync(ProjectPath("Broken.csproj"), TestContext.Current.CancellationToken);

        Assert.Equal(SolutionLoadState.Failed, _service.Snapshot.State);
        Assert.Single(_service.Snapshot.LoadDiagnostics);
        Assert.Contains(_notifications.Active, notice => notice.Id == NotificationIds.SolutionLoadFailed);
        Assert.Contains(_log.Entries, entry => entry.Category == "solution" && !entry.Message.Contains(Path.GetTempPath(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reload_OverlaysAnEditMadeWhileProjectEvaluationIsRunning()
    {
        var loader = new GatedReloadLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);

        var reload = _service.ReloadAsync(TestContext.Current.CancellationToken);
        await loader.ReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var path = ProjectPath("Shared.cs");
        var uri = _paths.ToDocumentUri(path);
        var replica = new DocumentReplica("internal sealed class Shared { public string Value => \"new\"; }", 9, 9);
        _service.QueueReplica(new DocumentReplicaChange(uri, path, replica, 9));
        loader.ReleaseReload.TrySetResult();
        await reload;

        var context = _service.GetDocumentContexts(uri)[0];
        var text = await _service.CurrentSolution!.GetDocument(context.DocumentId)!.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("new", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupersededLoad_CannotPublishStaleProgressOrState()
    {
        var loader = new SupersedingLoader();
        _service = Create(loader);
        var first = _service.OpenAsync(ProjectPath("First.slnx"), TestContext.Current.CancellationToken);
        await loader.FirstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.OpenAsync(ProjectPath("Second.slnx"), TestContext.Current.CancellationToken);
        await first;

        Assert.EndsWith("Second.slnx", _service.Snapshot.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(_service.Snapshot.Progress, item => item.Operation == "stale");
        Assert.True(_service.CurrentMetrics.CanceledLoads >= 1);
    }

    [Fact]
    public async Task CompletedLoad_DiscardsLateProgressWithoutPublishingAnotherSnapshot()
    {
        var loader = new DeferredProgressLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var completed = _service.Snapshot;

        loader.ReportLate();

        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
        Assert.Equal(completed.Version, _service.Snapshot.Version);
        Assert.DoesNotContain(_service.Snapshot.Progress, item => item.Operation == "late");
    }

    [Fact]
    public async Task CancelLoadAsync_StopsReloadWithoutDiscardingTheCurrentRoslynWorkspace()
    {
        var loader = new GatedReloadLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var original = _service.CurrentSolution;
        var reload = _service.ReloadAsync(TestContext.Current.CancellationToken);
        await loader.ReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.CancelLoadAsync(TestContext.Current.CancellationToken);
        await reload.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
        Assert.Same(original, _service.CurrentSolution);
        Assert.Equal(1, _service.CurrentMetrics.CanceledLoads);
    }

    [Fact]
    public async Task CancelLoadAsync_RestoresThePublishedPathForTheCurrentWorkspace()
    {
        var loader = new GatedReloadLoader();
        _service = Create(loader);
        var firstPath = ProjectPath("First.slnx");
        await _service.OpenAsync(firstPath, TestContext.Current.CancellationToken);
        var second = _service.OpenAsync(ProjectPath("Second.slnx"), TestContext.Current.CancellationToken);
        await loader.ReloadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.CancelLoadAsync(TestContext.Current.CancellationToken);
        await second;

        Assert.Equal(firstPath, _service.Snapshot.Path);
        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
    }

    [Fact]
    public async Task DisposeAsync_CancelsRoslynEvaluationAndWaitsForItsCleanup()
    {
        var loader = new CancellationCleanupLoader();
        _service = Create(loader);
        var load = _service.OpenAsync(ProjectPath("Closing.slnx"), TestContext.Current.CancellationToken);
        await loader.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(loader.CleanupCompleted.Task.IsCompleted,
            "Roslyn evaluation must finish cancellation cleanup before its workspace service is disposed.");
        await load.WaitAsync(TestContext.Current.CancellationToken);
        _service = null;
    }

    [Fact]
    public async Task CloseAsync_CancelsRoslynEvaluationAndWaitsForItsCleanup()
    {
        var loader = new CancellationCleanupLoader();
        _service = Create(loader);
        var load = _service.OpenAsync(ProjectPath("Closing.slnx"), TestContext.Current.CancellationToken);
        await loader.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.CloseAsync(TestContext.Current.CancellationToken);

        Assert.True(loader.CleanupCompleted.Task.IsCompleted);
        Assert.Equal(SolutionLoadState.Closed, _service.Snapshot.State);
        await load.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CloseAsync_WaitsForCleanupFromASupersededEvaluation()
    {
        var loader = new SupersededCleanupLoader();
        _service = Create(loader);
        var first = _service.OpenAsync(ProjectPath("First.slnx"), TestContext.Current.CancellationToken);
        await loader.FirstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await _service.OpenAsync(ProjectPath("Second.slnx"), TestContext.Current.CancellationToken);
        await loader.FirstCleanupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var close = _service.CloseAsync(TestContext.Current.CancellationToken);

        Assert.False(close.IsCompleted);
        loader.ReleaseFirstCleanup.TrySetResult();
        await Task.WhenAll(first, close).WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SolutionLoadState.Closed, _service.Snapshot.State);
    }

    [Fact]
    public async Task WatcherChangesDuringEvaluation_DoNotStartAReloadLoop()
    {
        var loader = new GatedInitialLoader();
        _service = Create(loader);
        var load = _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        await loader.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        _service.ObserveWorkspaceChanges(new WorkspaceChangeBatch(
            [new WorkspaceChange(NovaSharp.Workspace.WorkspaceChangeKind.Changed, ProjectPath("Generated.cs"))]));
        loader.Release.TrySetResult();
        await load;
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
    }

    [Fact]
    public async Task ChangedSourceContent_UpdatesMappedDocumentsWithoutReloadingTheSolution()
    {
        var loader = new CountingSolutionLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var path = ProjectPath("Shared.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "internal sealed class Shared { public int Value => 7; }",
            TestContext.Current.CancellationToken);

        _service.ObserveWorkspaceChanges(new WorkspaceChangeBatch(
            [new WorkspaceChange(NovaSharp.Workspace.WorkspaceChangeKind.Changed, path,
                ObservedTimestamp: Stopwatch.GetTimestamp())]));

        await WaitUntilAsync(async () =>
        {
            var uri = _paths.ToDocumentUri(path);
            var texts = await Task.WhenAll(_service.GetDocumentContexts(uri).Select(async context =>
                await _service.CurrentSolution!.GetDocument(context.DocumentId)!.GetTextAsync(TestContext.Current.CancellationToken)));
            return texts.Length > 0 && texts.All(text => text.ToString().Contains("Value => 7", StringComparison.Ordinal));
        });
        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task LateWatcherNotificationForLoadedSource_DoesNotStartAnotherEvaluation()
    {
        var loader = new CountingSolutionLoader();
        _service = Create(loader);
        var path = ProjectPath("Shared.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "internal sealed class Shared { }", TestContext.Current.CancellationToken);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);

        _service.ObserveWorkspaceChanges(new WorkspaceChangeBatch(
            [new WorkspaceChange(NovaSharp.Workspace.WorkspaceChangeKind.Changed, path,
                ObservedTimestamp: Stopwatch.GetTimestamp())]));
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(SolutionLoadState.Ready, _service.Snapshot.State);
    }

    [Fact]
    public async Task CompletedWatcherReload_CanBeFollowedByAnotherEditorCloseReload()
    {
        var loader = new CountingSolutionLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);

        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.Changed += snapshot =>
        {
            if (snapshot.State != SolutionLoadState.Ready) return;
            if (loader.LoadCount >= 2) first.TrySetResult();
            if (loader.LoadCount >= 3) second.TrySetResult();
        };

        QueueAndCloseReplica("First.cs");
        await first.Task.WaitAsync(TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        QueueAndCloseReplica("Second.cs");
        await second.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, loader.LoadCount);

        void QueueAndCloseReplica(string name)
        {
            var path = ProjectPath(name);
            var uri = _paths.ToDocumentUri(path);
            _service.QueueReplica(new DocumentReplicaChange(uri, path, new DocumentReplica(string.Empty, 1, 1), 1));
            _service.RemoveReplica(uri);
        }
    }

    [Fact]
    public async Task DelayedWatcherEventsObservedDuringLoad_DoNotReloadTheCompletedSolution()
    {
        var loader = new GatedInitialLoader();
        _service = Create(loader);
        var load = _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        await loader.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var delayed = new WorkspaceChangeBatch(
            [new WorkspaceChange(NovaSharp.Workspace.WorkspaceChangeKind.Changed, ProjectPath("Generated.cs"),
                ObservedTimestamp: Stopwatch.GetTimestamp())]);

        loader.Release.TrySetResult();
        await load;
        _service.ObserveWorkspaceChanges(delayed);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task ClosingACleanEditorReplica_DoesNotReloadTheSolution()
    {
        var loader = new CountingSolutionLoader();
        _service = Create(loader);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var path = ProjectPath("Clean.cs");
        var uri = _paths.ToDocumentUri(path);
        _service.QueueReplica(new DocumentReplicaChange(uri, path, new DocumentReplica(string.Empty, 1, 1), 1));

        _service.RemoveReplica(uri, reloadFromDisk: false);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);

        Assert.Equal(1, loader.LoadCount);
    }

    [Fact]
    public async Task MutationSignals_StayBoundedAndNewestReplicaCanBeForcedThroughBarrier()
    {
        _service = Create(new AdhocSolutionLoader(), mutationCapacity: 1);
        await _service.OpenAsync(ProjectPath("Workspace.slnx"), TestContext.Current.CancellationToken);
        var path = ProjectPath("Shared.cs");
        var uri = _paths.ToDocumentUri(path);
        var replica = new DocumentReplica("initial", 0, 0);

        for (var sequence = 1; sequence <= 1_000; sequence++)
        {
            replica.Resync($"internal sealed class Shared {{ public int Value => {sequence}; }}", sequence, sequence);
            _service.QueueReplica(new DocumentReplicaChange(uri, path, replica, sequence));
        }
        await _service.WaitForReplicaAsync(uri, 1_000, TestContext.Current.CancellationToken);

        Assert.InRange(_service.CurrentMetrics.PendingMutations, 0, _service.MutationCapacity);
        var context = _service.GetDocumentContexts(uri)[0];
        var text = await _service.CurrentSolution!.GetDocument(context.DocumentId)!.GetTextAsync(TestContext.Current.CancellationToken);
        Assert.Contains("1000", text.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReplicaSources_AreBoundedAndClosedSourcesAreReleased()
    {
        _service = new SolutionWorkspaceService(
            _paths,
            new AdhocSolutionLoader(),
            _background,
            new DiagnosticStore(),
            _notifications,
            _log,
            mutationCapacity: 2,
            replicaCapacity: 2);
        var first = _paths.ToDocumentUri(ProjectPath("First.cs"));
        var second = _paths.ToDocumentUri(ProjectPath("Second.cs"));
        var third = _paths.ToDocumentUri(ProjectPath("Third.cs"));

        _service.QueueReplica(new DocumentReplicaChange(first, first.LocalPath, new DocumentReplica("1", 1, 1), 1));
        _service.QueueReplica(new DocumentReplicaChange(second, second.LocalPath, new DocumentReplica("2", 1, 1), 1));
        _service.QueueReplica(new DocumentReplicaChange(third, third.LocalPath, new DocumentReplica("3", 1, 1), 1));

        Assert.Equal(2, _service.CurrentMetrics.RetainedReplicas);
        Assert.Equal(1, _service.CurrentMetrics.DroppedReplicaSources);
        _service.RemoveReplica(third);
        Assert.Equal(1, _service.CurrentMetrics.RetainedReplicas);
    }

    private SolutionWorkspaceService Create(ISolutionLoader loader, int mutationCapacity = 8)
    {
        return new(
        _paths,
        loader,
        _background,
        new DiagnosticStore(),
        _notifications,
        _log,
        mutationCapacity);
    }

    private static string ProjectPath(string name)
    {
        return Path.Combine(Path.GetTempPath(), "NovaSharp.Phase6.Tests", name);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!await condition())
        {
            await Task.Delay(10, deadline.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.DisposeAsync();
        }

        await _background.DisposeAsync();
    }
}

internal class AdhocSolutionLoader : ISolutionLoader
{
    public virtual Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new ProjectLoadStatusSnapshot(path, "Evaluate", null, TimeSpan.FromMilliseconds(1)));
        return Task.FromResult(Create(path));
    }

    protected static LoadedSolutionWorkspace Create(string path)
    {
        var workspace = new AdhocWorkspace();
        var shared = Path.Combine(Path.GetDirectoryName(path)!, "Shared.cs");
        var first = ProjectId.CreateNewId("First");
        var second = ProjectId.CreateNewId("Second");
        var parse = new CSharpParseOptions(LanguageVersion.CSharp13, preprocessorSymbols: ["TRACE"]);
        var compilation = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable);
        var solution = workspace.CurrentSolution
            .AddProject(ProjectInfo.Create(first, VersionStamp.Create(), "First", "First", LanguageNames.CSharp,
                filePath: Path.Combine(Path.GetDirectoryName(path)!, "First.csproj"), parseOptions: parse, compilationOptions: compilation))
            .AddProject(ProjectInfo.Create(second, VersionStamp.Create(), "Second", "Second", LanguageNames.CSharp,
                filePath: Path.Combine(Path.GetDirectoryName(path)!, "Second.csproj"), parseOptions: parse, compilationOptions: compilation,
                projectReferences: [new ProjectReference(first)]))
            .AddMetadataReference(first, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddMetadataReference(second, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
            .AddDocument(DocumentId.CreateNewId(first), "Shared.cs", SourceText.From("internal sealed class Shared { }"), filePath: shared)
            .AddDocument(DocumentId.CreateNewId(second), "Shared.cs", SourceText.From("internal sealed class Shared { }"), filePath: shared);
        Assert.True(workspace.TryApplyChanges(solution));
        return new LoadedSolutionWorkspace(workspace, workspace.CurrentSolution);
    }
}

internal sealed class GatedReloadLoader : AdhocSolutionLoader
{
    private int _loads;
    public TaskCompletionSource ReloadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseReload { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _loads) > 1)
        {
            ReloadStarted.TrySetResult();
            await ReleaseReload.Task.WaitAsync(cancellationToken);
        }
        return await base.LoadAsync(path, progress, cancellationToken);
    }
}

internal sealed class SupersedingLoader : AdhocSolutionLoader
{
    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        if (path.EndsWith("First.slnx", StringComparison.Ordinal))
        {
            FirstStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                progress.Report(new ProjectLoadStatusSnapshot(path, "stale", null, TimeSpan.Zero));
                throw;
            }
        }
        return await base.LoadAsync(path, progress, cancellationToken);
    }
}

internal sealed class DeferredProgressLoader : AdhocSolutionLoader
{
    private IProgress<ProjectLoadStatusSnapshot>? _progress;
    private string? _path;

    public override Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        _path = path;
        _progress = progress;
        return base.LoadAsync(path, progress, cancellationToken);
    }

    public void ReportLate() =>
        _progress!.Report(new ProjectLoadStatusSnapshot(_path!, "late", null, TimeSpan.Zero));
}

internal sealed class CancellationCleanupLoader : ISolutionLoader
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            CleanupCompleted.TrySetResult();
            throw;
        }

        throw new InvalidOperationException("The load completed without cancellation.");
    }
}

internal sealed class SupersededCleanupLoader : AdhocSolutionLoader
{
    private int _loads;
    public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FirstCleanupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseFirstCleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _loads) == 1)
        {
            FirstStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                FirstCleanupStarted.TrySetResult();
                await ReleaseFirstCleanup.Task;
                throw;
            }
        }
        return await base.LoadAsync(path, progress, cancellationToken);
    }
}

internal sealed class GatedInitialLoader : AdhocSolutionLoader
{
    public int LoadCount => Volatile.Read(ref _loadCount);
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _loadCount;

    public override async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        Started.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return await base.LoadAsync(path, progress, cancellationToken);
    }
}

internal sealed class CountingSolutionLoader : AdhocSolutionLoader
{
    public int LoadCount => Volatile.Read(ref _loadCount);
    private int _loadCount;

    public override Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loadCount);
        return base.LoadAsync(path, progress, cancellationToken);
    }
}

internal sealed class RemovingContextLoader : AdhocSolutionLoader
{
    private int _loads;

    public override async Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        var loaded = await base.LoadAsync(path, progress, cancellationToken);
        if (Interlocked.Increment(ref _loads) == 1)
        {
            return loaded;
        }

        var second = loaded.Solution.Projects.Single(project => project.Name == "Second");
        var reduced = loaded.Solution.RemoveProject(second.Id);
        Assert.True(loaded.Workspace.TryApplyChanges(reduced));
        loaded.Solution = loaded.Workspace.CurrentSolution;
        return loaded;
    }
}

internal sealed class FailingSolutionLoader : ISolutionLoader
{
    public Task<LoadedSolutionWorkspace> LoadAsync(
        string path,
        IProgress<ProjectLoadStatusSnapshot> progress,
        CancellationToken cancellationToken)
    {
        return Task.FromException<LoadedSolutionWorkspace>(new InvalidOperationException("evaluation failed"));
    }
}

internal sealed class RecordingWarmCache(SolutionWarmCacheEntry? entry) : ISolutionWarmCache
{
    public int SaveCount => Volatile.Read(ref _saveCount);
    public int ClearCount => Volatile.Read(ref _clearCount);
    private int _saveCount;
    private int _clearCount;

    public Task<SolutionWarmCacheEntry?> LoadAsync(
        string workspaceRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(entry);
    }

    public Task SaveAsync(
        string workspaceRoot,
        SolutionWorkspaceSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _saveCount);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _clearCount);
        return Task.CompletedTask;
    }
}
