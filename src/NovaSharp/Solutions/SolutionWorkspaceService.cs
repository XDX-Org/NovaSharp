using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.Platform;
using NovaSharp.Workspace;

namespace NovaSharp.Solutions;

/// <summary>Owns the current Roslyn workspace and serializes every mutation to it.</summary>
public sealed class SolutionWorkspaceService : IAsyncDisposable
{
    private abstract record MutationSignal;
    private sealed record ReplicaSignal(string DocumentUri) : MutationSignal;
    private sealed record SourceFileSignal(string Path) : MutationSignal;
    private sealed record ReplicaSource(Uri Uri, string Path, DocumentReplica Replica, long Sequence);
    private sealed class PendingEvaluation
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;

        public Task Completion => _completion.Task;

        public async Task<LoadedSolutionWorkspace> RunAsync(
            ISolutionLoader loader,
            string path,
            IProgress<ProjectLoadStatusSnapshot> progress,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            try
            {
                var loaded = await loader.LoadAsync(path, progress, cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                {
                    await loaded.DisposeAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return loaded;
            }
            finally
            {
                Volatile.Write(ref _state, 2);
                _completion.TrySetResult();
            }
        }

        public void CompleteIfNotStarted()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _completion.TrySetResult();
            }
        }
    }

    private const string DiagnosticProducer = "novasharp.solution-loader";
    private const int MaxProgressEntries = 500;
    private const int MaxDisplayedDocumentsPerProject = 5_000;
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);

    private readonly IWorkspacePaths _paths;
    private readonly ISolutionLoader _loader;
    private readonly BoundedWorkQueue _background;
    private readonly DiagnosticStore _diagnostics;
    private readonly INotificationService _notifications;
    private readonly IWorkbenchLog _log;
    private readonly Channel<MutationSignal> _mutations;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ReplicaSource> _replicas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoslynDocumentContext[]> _mappings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeProjectByDocument = new(StringComparer.Ordinal);
    private readonly List<ProjectLoadStatusSnapshot> _progress = [];
    private readonly HashSet<Task> _loads = [];
    private readonly Task _mutationConsumer;

    private SolutionWorkspaceSnapshot _snapshot = new();
    private SolutionWorkspaceSnapshot? _lastReadySnapshot;
    private LoadedSolutionWorkspace? _loaded;
    private CancellationTokenSource? _activeLoad;
    private CancellationTokenSource? _reloadDelay;
    private Task _activeEvaluation = Task.CompletedTask;
    private Task _reloadTask = Task.CompletedTask;
    private string[] _rawBuildLog = [];
    private long _sourceVersion;
    private long _lastSuccessfulLoadTimestamp;
    private int _pendingMutations;
    private int _droppedReplicaSignals;
    private int _droppedReplicaSources;
    private int _canceledLoads;
    private int _rescanReplicas;
    private int _disposed;
    private bool _closing;

    public SolutionWorkspaceService(
        IWorkspacePaths paths,
        ISolutionLoader loader,
        BoundedWorkQueue background,
        DiagnosticStore diagnostics,
        INotificationService notifications,
        IWorkbenchLog log,
        int mutationCapacity = 128,
        int replicaCapacity = 1_024)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(background);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfLessThan(mutationCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(replicaCapacity, 1);

        _paths = paths;
        _loader = loader;
        _background = background;
        _diagnostics = diagnostics;
        _notifications = notifications;
        _log = log;
        _mutations = Channel.CreateBounded<MutationSignal>(new BoundedChannelOptions(mutationCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        MutationCapacity = mutationCapacity;
        ReplicaCapacity = replicaCapacity;
        _mutationConsumer = Task.Run(ConsumeMutationsAsync);
    }

    public int MutationCapacity { get; }

    public int ReplicaCapacity { get; }

    public event Action<SolutionWorkspaceSnapshot>? Changed;

    public SolutionWorkspaceSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public Solution? CurrentSolution
    {
        get
        {
            lock (_gate)
            {
                return _loaded?.Solution;
            }
        }
    }

    public IReadOnlyList<string> RawBuildLog
    {
        get
        {
            lock (_gate)
            {
                return [.. _rawBuildLog];
            }
        }
    }

    public SolutionWorkspaceMetrics CurrentMetrics
    {
        get
        {
            TimeSpan elapsed;
            int retainedSnapshots;
            lock (_gate)
            {
                elapsed = _snapshot.Metrics.LastLoadDuration;
                retainedSnapshots = _loaded is null ? 0 : 1;
            }
            return CreateMetrics(elapsed, retainedSnapshots);
        }
    }

    public Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var load = OpenCoreAsync(path, cancellationToken);
        lock (_gate)
        {
            _loads.Add(load);
        }
        _ = load.ContinueWith(
            static (completed, state) => ((SolutionWorkspaceService)state!).RemoveLoad(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return load;
    }

    private async Task OpenCoreAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closing)
            {
                return;
            }
        }

        var canonical = _paths.Canonicalize(path);
        var watch = Stopwatch.StartNew();
        CancellationTokenSource load;
        var evaluation = new PendingEvaluation();
        long sourceVersion;
        lock (_gate)
        {
            _activeLoad?.Cancel();
            _activeLoad = load = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
            _activeEvaluation = evaluation.Completion;
            sourceVersion = ++_sourceVersion;
            _progress.Clear();
        }

        Publish(current => current with
        {
            State = SolutionLoadState.Loading,
            Path = canonical,
            Name = Path.GetFileName(canonical),
            Progress = [],
            Error = null,
            SourceVersion = sourceVersion,
        });

        LoadedSolutionWorkspace? candidate = null;
        try
        {
            var progress = new CallbackProgress<ProjectLoadStatusSnapshot>(item => ReportProgress(sourceVersion, item));
            candidate = await _background.EnqueueAsync(
                token => evaluation.RunAsync(_loader, canonical, progress, token),
                load.Token).ConfigureAwait(false);

            await _writer.WaitAsync(load.Token).ConfigureAwait(false);
            LoadedSolutionWorkspace? previous = null;
            try
            {
                lock (_gate)
                {
                    if (sourceVersion != _sourceVersion || load.IsCancellationRequested)
                    {
                        return;
                    }
                }

                candidate.Solution = OverlayReplicas(candidate.Workspace, candidate.Solution);
                previous = _loaded;
                _loaded = candidate;
                candidate = null;
                RebuildMappings(_loaded.Solution);
                var projects = BuildProjects(_loaded.Solution);
                var oldProjects = Snapshot.Projects;
                var changes = CompareContexts(oldProjects, projects);
                Interlocked.Exchange(ref _lastSuccessfulLoadTimestamp, Stopwatch.GetTimestamp());
                lock (_gate)
                {
                    _rawBuildLog = [.. _loaded.RawBuildLog];
                }

                Publish(current => current with
                {
                    State = SolutionLoadState.Ready,
                    Path = canonical,
                    Name = Path.GetFileName(canonical),
                    Projects = projects,
                    Progress = ProgressSnapshot(),
                    ContextChanges = changes,
                    LoadDiagnostics = _loaded.Diagnostics,
                    Error = null,
                    SourceVersion = sourceVersion,
                    Metrics = CreateMetrics(watch.Elapsed, retainedSnapshots: 1),
                });
                lock (_gate)
                {
                    _lastReadySnapshot = _snapshot;
                }
                PublishDiagnostics(canonical, sourceVersion, _loaded.Diagnostics);
                _notifications.Dismiss(NotificationIds.SolutionLoadFailed);
                _log.Write(LogLevel.Information, "solution", $"Loaded {Redaction.Path(canonical)} with {projects.Count} project contexts.");
            }
            finally
            {
                _writer.Release();
                if (previous is not null)
                {
                    await previous.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
            Interlocked.Increment(ref _canceledLoads);
            bool isCurrent;
            lock (_gate)
            {
                isCurrent = sourceVersion == _sourceVersion;
            }
            if (isCurrent)
            {
                Publish(_ => _lastReadySnapshot is { } ready
                    ? ready with { Error = null, Metrics = CreateMetrics(watch.Elapsed, retainedSnapshots: 1) }
                    : new SolutionWorkspaceSnapshot());
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            lock (_gate)
            {
                if (sourceVersion != _sourceVersion)
                {
                    return;
                }
            }

            var message = $"Could not load {Path.GetFileName(canonical)}. See project diagnostics and MSBuild output.";
            Publish(current => current with
            {
                State = SolutionLoadState.Failed,
                Error = message,
                LoadDiagnostics = [exception.Message],
                Metrics = CreateMetrics(watch.Elapsed, _loaded is null ? 0 : 1),
            });
            PublishDiagnostics(canonical, sourceVersion, [exception.Message]);
            _notifications.Raise(NotificationIds.SolutionLoadFailed, NotificationSeverity.Error, message);
            _log.Write(LogLevel.Error, "solution", $"Could not load {Redaction.Path(canonical)} ({exception.GetType().Name}).");
        }
        finally
        {
            evaluation.CompleteIfNotStarted();
            await evaluation.Completion.ConfigureAwait(false);
            if (candidate is not null)
            {
                await candidate.DisposeAsync().ConfigureAwait(false);
            }
            lock (_gate)
            {
                if (ReferenceEquals(_activeLoad, load))
                {
                    _activeLoad = null;
                    _activeEvaluation = Task.CompletedTask;
                }
            }
            load.Dispose();
        }
    }

    private void RemoveLoad(Task load)
    {
        lock (_gate)
        {
            _loads.Remove(load);
        }
    }

    public async Task CancelLoadAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? load;
        Task evaluation;
        SolutionWorkspaceSnapshot? ready;
        long cancellationVersion;
        lock (_gate)
        {
            load = _activeLoad;
            evaluation = _activeEvaluation;
            if (load is null || _snapshot.State != SolutionLoadState.Loading)
            {
                return;
            }

            ready = _lastReadySnapshot;
            _activeLoad = null;
            cancellationVersion = ++_sourceVersion;
        }

        await load.CancelAsync().ConfigureAwait(false);
        await WaitForCleanupAsync(evaluation, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (cancellationVersion != _sourceVersion)
            {
                return;
            }
        }
        Publish(_ => ready is null ? new SolutionWorkspaceSnapshot() : ready with { Error = null });
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        return Snapshot.Path is { } path
        ? OpenAsync(path, cancellationToken)
        : Task.CompletedTask;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancellationTokenSource? load;
        Task evaluation;
        lock (_gate)
        {
            _closing = true;
            _reloadDelay?.Cancel();
            load = _activeLoad;
            _activeLoad = null;
            _sourceVersion++;
            evaluation = Task.WhenAll(_loads.Append(_reloadTask));
        }
        try
        {
            if (load is not null)
            {
                await load.CancelAsync().ConfigureAwait(false);
            }
            await WaitForCleanupAsync(evaluation, cancellationToken).ConfigureAwait(false);

            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            LoadedSolutionWorkspace? previous;
            try
            {
                previous = _loaded;
                _loaded = null;
                lock (_gate)
                {
                    _lastReadySnapshot = null;
                    _mappings.Clear();
                    _activeProjectByDocument.Clear();
                    _rawBuildLog = [];
                }
                Publish(_ => new SolutionWorkspaceSnapshot());
            }
            finally
            {
                _writer.Release();
            }
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                _closing = false;
            }
        }
    }

    public void QueueReplica(DocumentReplicaChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var stale in _replicas
                         .Where(pair => pair.Key != change.DocumentUri.AbsoluteUri
                             && ReferenceEquals(pair.Value.Replica, change.Replica))
                         .Select(static pair => pair.Key)
                         .ToArray())
            {
                _replicas.Remove(stale);
            }
            if (!_replicas.ContainsKey(change.DocumentUri.AbsoluteUri) && _replicas.Count == ReplicaCapacity)
            {
                _replicas.Remove(_replicas.Keys.First());
                Interlocked.Increment(ref _droppedReplicaSources);
            }
            _replicas[change.DocumentUri.AbsoluteUri] = new ReplicaSource(
                change.DocumentUri, change.Path, change.Replica, change.Sequence);
        }

        if (_mutations.Writer.TryWrite(new ReplicaSignal(change.DocumentUri.AbsoluteUri)))
        {
            Interlocked.Increment(ref _pendingMutations);
        }
        else
        {
            Interlocked.Increment(ref _droppedReplicaSignals);
            Interlocked.Exchange(ref _rescanReplicas, 1);
        }
    }

    public void RemoveReplica(Uri documentUri, bool reloadFromDisk = true)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        bool removed;
        lock (_gate)
        {
            removed = _replicas.Remove(documentUri.AbsoluteUri);
        }
        if (removed && reloadFromDisk && Snapshot.Path is not null)
        {
            ScheduleReload();
        }
    }

    public async Task WaitForReplicaAsync(Uri documentUri, long sequence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        ReplicaSource? source;
        lock (_gate)
        {
            _replicas.TryGetValue(documentUri.AbsoluteUri, out source);
        }

        if (source is null)
        {
            return;
        }

        await source.Replica.WaitForSequenceAsync(sequence, cancellationToken).ConfigureAwait(false);
        await ApplyReplicaAsync(documentUri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<RoslynDocumentContext> GetDocumentContexts(Uri documentUri)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        lock (_gate)
        {
            return _mappings.TryGetValue(documentUri.AbsoluteUri, out var contexts) ? [.. contexts] : [];
        }
    }

    public async Task SetActiveContextAsync(Uri documentUri, ProjectId projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentNullException.ThrowIfNull(projectId);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (!_mappings.TryGetValue(documentUri.AbsoluteUri, out var contexts)
                    || !contexts.Any(context => context.ProjectId == projectId))
                {
                    throw new ArgumentException("The project is not a context for this document.", nameof(projectId));
                }
                _activeProjectByDocument[documentUri.AbsoluteUri] = projectId.Id.ToString();
                RefreshContextActivity(documentUri.AbsoluteUri);
            }
            if (_loaded is not null)
            {
                var projects = BuildProjects(_loaded.Solution);
                Publish(current => current with { Projects = projects });
            }
        }
        finally
        {
            _writer.Release();
        }
    }

    public void ObserveWorkspaceChanges(WorkspaceChangeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        var snapshot = Snapshot;
        if (snapshot.Path is null || snapshot.State == SolutionLoadState.Loading)
        {
            return;
        }

        var cutoff = Interlocked.Read(ref _lastSuccessfulLoadTimestamp);
        var current = batch.Overflowed
            ? batch
            : batch with
            {
                Changes = batch.Changes
                    .Where(change => change.ObservedTimestamp == 0 || change.ObservedTimestamp > cutoff)
                    .ToArray(),
            };
        if (!current.Overflowed && current.Changes.Count == 0) return;

        if (RequiresFullReload(current))
        {
            ScheduleReload();
            return;
        }

        foreach (var path in current.Changes
                     .Where(IsSourceContentChange)
                     .Select(change => _paths.Canonicalize(change.Path))
                     .Distinct(StringComparer.Ordinal))
        {
            if (_mutations.Writer.TryWrite(new SourceFileSignal(path)))
            {
                Interlocked.Increment(ref _pendingMutations);
            }
            else
            {
                Interlocked.Increment(ref _droppedReplicaSignals);
                ScheduleReload();
                return;
            }
        }
    }

    private void ScheduleReload()
    {
        CancellationTokenSource delay;
        lock (_gate)
        {
            _reloadDelay?.Cancel();
            _reloadDelay = delay = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _reloadTask = ReloadAfterDelayAsync(delay);
        }
    }

    private async Task ReloadAfterDelayAsync(CancellationTokenSource delay)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), delay.Token).ConfigureAwait(false);
            await ReloadAsync(delay.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_reloadDelay, delay))
                {
                    _reloadDelay = null;
                    _reloadTask = Task.CompletedTask;
                }
            }
            delay.Dispose();
        }
    }

    private async Task ConsumeMutationsAsync()
    {
        try
        {
            await foreach (var signal in _mutations.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendingMutations);
                switch (signal)
                {
                    case ReplicaSignal replica:
                        await ApplyReplicaAsync(replica.DocumentUri, _shutdown.Token).ConfigureAwait(false);
                        break;
                    case SourceFileSignal source:
                        await ApplySourceFileAsync(source.Path, _shutdown.Token).ConfigureAwait(false);
                        break;
                }
                if (Interlocked.Exchange(ref _rescanReplicas, 0) != 0)
                {
                    string[] uris;
                    lock (_gate)
                    {
                        uris = [.. _replicas.Keys];
                    }

                    foreach (var uri in uris)
                    {
                        await ApplyReplicaAsync(uri, _shutdown.Token).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplySourceFileAsync(string path, CancellationToken cancellationToken)
    {
        LoadedSolutionWorkspace? loaded;
        Document[] documents;
        long sourceVersion;
        var uri = _paths.ToDocumentUri(path).AbsoluteUri;

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_replicas.ContainsKey(uri)) return;
                loaded = _loaded;
                sourceVersion = _sourceVersion;
            }
            if (loaded is null) return;
            documents = DocumentsForPath(loaded.Solution, uri);
        }
        finally
        {
            _writer.Release();
        }

        // An untracked generated output is not a project input. Adds, removes, and renames take the full reload path.
        if (documents.Length == 0) return;

        try
        {
            var current = await documents[0].GetTextAsync(cancellationToken).ConfigureAwait(false);
            var text = await File.ReadAllTextAsync(path, current.Encoding ?? Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            var replacement = SourceText.From(text, current.Encoding ?? Encoding.UTF8);
            if (current.ContentEquals(replacement)) return;

            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_loaded, loaded) || sourceVersion != _sourceVersion || _replicas.ContainsKey(uri)) return;
                }

                var solution = loaded.Solution;
                foreach (var document in DocumentsForPath(solution, uri))
                {
                    solution = solution.WithDocumentText(document.Id, replacement, PreservationMode.PreserveIdentity);
                }
                if (ReferenceEquals(solution, loaded.Solution)) return;
                if (!loaded.Workspace.TryApplyChanges(solution))
                {
                    throw new InvalidOperationException("Roslyn rejected an external source-file update.");
                }
                loaded.Solution = loaded.Workspace.CurrentSolution;
            }
            finally
            {
                _writer.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or DecoderFallbackException
            or InvalidOperationException)
        {
            _log.Write(LogLevel.Warning, "solution", $"Could not refresh {Redaction.Path(path)} in Roslyn.", exception);
            ScheduleReload();
        }
    }

    private Document[] DocumentsForPath(Solution solution, string documentUri) => solution.Projects
        .SelectMany(static project => project.Documents)
        .Where(document => document.FilePath is not null
            && string.Equals(_paths.ToDocumentUri(document.FilePath).AbsoluteUri, documentUri, StringComparison.Ordinal))
        .ToArray();

    private async Task ApplyReplicaAsync(string documentUri, CancellationToken cancellationToken)
    {
        ReplicaSource? source;
        lock (_gate)
        {
            _replicas.TryGetValue(documentUri, out source);
        }

        if (source is null)
        {
            return;
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded is null)
            {
                return;
            }

            var updated = ApplyReplica(_loaded.Solution, documentUri, source.Replica.Snapshot());
            if (ReferenceEquals(updated, _loaded.Solution))
            {
                return;
            }

            if (!_loaded.Workspace.TryApplyChanges(updated))
            {
                throw new InvalidOperationException("Roslyn rejected an open-document update.");
            }
            _loaded.Solution = _loaded.Workspace.CurrentSolution;
            RebuildMappings(_loaded.Solution);
        }
        catch (InvalidOperationException exception)
        {
            _log.Write(LogLevel.Error, "solution", $"Could not synchronize {Redaction.Path(source.Path)} into Roslyn.", exception);
            _notifications.Raise(NotificationIds.SolutionSyncFailed, NotificationSeverity.Error,
                $"Could not synchronize {Path.GetFileName(source.Path)} with C# project state.");
        }
        finally
        {
            _writer.Release();
        }
    }

    private Solution OverlayReplicas(Microsoft.CodeAnalysis.Workspace workspace, Solution solution)
    {
        ReplicaSource[] replicas;
        lock (_gate)
        {
            replicas = [.. _replicas.Values];
        }

        foreach (var source in replicas)
        {
            solution = ApplyReplica(solution, source.Uri.AbsoluteUri, source.Replica.Snapshot());
        }
        if (!ReferenceEquals(solution, workspace.CurrentSolution) && !workspace.TryApplyChanges(solution))
        {
            throw new InvalidOperationException("Roslyn rejected dirty document overlays during reload.");
        }
        return workspace.CurrentSolution;
    }

    private Solution ApplyReplica(Solution solution, string documentUri, DocumentSnapshot snapshot)
    {
        var ids = solution.Projects
            .SelectMany(static project => project.Documents)
            .Where(document => document.FilePath is not null
                && string.Equals(_paths.ToDocumentUri(document.FilePath).AbsoluteUri, documentUri, StringComparison.Ordinal))
            .Select(static document => document.Id)
            .ToArray();
        if (ids.Length == 0)
        {
            return solution;
        }

        var text = SourceText.From(snapshot.Text, Encoding.UTF8);
        foreach (var id in ids)
        {
            solution = solution.WithDocumentText(id, text, PreservationMode.PreserveIdentity);
        }
        return solution;
    }

    private void RebuildMappings(Solution solution)
    {
        var mappings = solution.Projects
            .SelectMany(project => project.Documents
                .Where(static document => document.FilePath is not null)
                .Select(document => (Uri: _paths.ToDocumentUri(document.FilePath!).AbsoluteUri, Project: project, Document: document)))
            .GroupBy(static item => item.Uri, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                group => group.Select(item => new RoslynDocumentContext(
                    item.Project.Id,
                    item.Document.Id,
                    item.Project.Name,
                    TargetFramework(item.Project),
                    false)).ToArray(),
                StringComparer.Ordinal);

        lock (_gate)
        {
            _mappings.Clear();
            foreach (var pair in mappings)
            {
                _mappings[pair.Key] = pair.Value;
                if (!_activeProjectByDocument.TryGetValue(pair.Key, out var active)
                    || !pair.Value.Any(context => context.ProjectId.Id.ToString() == active))
                {
                    _activeProjectByDocument[pair.Key] = pair.Value[0].ProjectId.Id.ToString();
                }
                RefreshContextActivity(pair.Key);
            }

            foreach (var uri in _activeProjectByDocument.Keys.Where(uri => !_mappings.ContainsKey(uri)).ToArray())
            {
                _activeProjectByDocument.Remove(uri);
            }
        }
    }

    private void RefreshContextActivity(string documentUri)
    {
        if (!_mappings.TryGetValue(documentUri, out var contexts)
            || !_activeProjectByDocument.TryGetValue(documentUri, out var active))
        {
            return;
        }

        _mappings[documentUri] = contexts
            .Select(context => context with { IsActive = context.ProjectId.Id.ToString() == active })
            .ToArray();
    }

    private IReadOnlyList<ProjectContextSnapshot> BuildProjects(Solution solution)
    {
        return solution.Projects
            .OrderBy(static project => project.FilePath, StringComparer.Ordinal)
            .ThenBy(static project => project.Name, StringComparer.Ordinal)
            .Select(project =>
            {
                var allDocuments = project.Documents.Where(static document => document.FilePath is not null).ToArray();
                var documents = allDocuments.Take(MaxDisplayedDocumentsPerProject).Select(document =>
                {
                    var uri = _paths.ToDocumentUri(document.FilePath!).AbsoluteUri;
                    return new SolutionDocumentSnapshot(document.Id.Id.ToString(), document.Name, document.FilePath!, uri);
                }).ToArray();
                var references = project.ProjectReferences
                    .Select(reference => new SolutionReferenceSnapshot(
                        SolutionReferenceKind.Project,
                        solution.GetProject(reference.ProjectId)?.Name ?? reference.ProjectId.Id.ToString()))
                    .Concat(project.MetadataReferences.Select(reference => new SolutionReferenceSnapshot(
                        SolutionReferenceKind.Assembly,
                        Path.GetFileNameWithoutExtension(reference.Display ?? "assembly"),
                        reference.Display)))
                    .Concat(project.AnalyzerReferences.Select(reference => new SolutionReferenceSnapshot(
                        SolutionReferenceKind.Analyzer,
                        Path.GetFileNameWithoutExtension(reference.FullPath) ?? "analyzer",
                        reference.FullPath)))
                    .OrderBy(static reference => reference.Kind)
                    .ThenBy(static reference => reference.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var parse = project.ParseOptions as CSharpParseOptions;
                var compilation = project.CompilationOptions as CSharpCompilationOptions;
                var active = documents.Any(document =>
                {
                    lock (_gate)
                    {
                        return _activeProjectByDocument.TryGetValue(document.DocumentUri, out var id)
                            && id == project.Id.Id.ToString();
                    }
                });
                return new ProjectContextSnapshot(
                    project.Id.Id.ToString(),
                    project.Name,
                    project.FilePath ?? project.Name,
                    TargetFramework(project),
                    active,
                    documents,
                    references,
                    parse?.PreprocessorSymbolNames.Order(StringComparer.Ordinal).ToArray() ?? [],
                    parse?.LanguageVersion.ToDisplayString() ?? "default",
                    compilation?.NullableContextOptions.ToString() ?? "default",
                    allDocuments.Length > documents.Length);
            }).ToArray();
    }

    private static string? TargetFramework(Project project)
    {
        var outputPath = project.CompilationOutputInfo.AssemblyPath;
        var directory = outputPath is null ? null : Path.GetDirectoryName(outputPath);
        var candidate = directory is null ? null : Path.GetFileName(directory);
        return candidate is { Length: > 0 } && candidate.Contains('.') ? candidate : null;
    }

    private IReadOnlyList<ProjectContextChange> CompareContexts(
        IReadOnlyList<ProjectContextSnapshot> before,
        IReadOnlyList<ProjectContextSnapshot> after)
    {
        string Key(ProjectContextSnapshot project)
        {
            return $"{_paths.ToDocumentUri(project.Path).AbsoluteUri}\n{project.TargetFramework}";
        }

        var old = before.ToDictionary(Key, StringComparer.Ordinal);
        var current = after.ToDictionary(Key, StringComparer.Ordinal);
        return old.Values.Where(project => !current.ContainsKey(Key(project)))
            .Select(project => new ProjectContextChange(project.Path, project.TargetFramework, "removed"))
            .Concat(current.Values.Where(project => !old.ContainsKey(Key(project)))
                .Select(project => new ProjectContextChange(project.Path, project.TargetFramework, "added")))
            .ToArray();
    }

    private void ReportProgress(long sourceVersion, ProjectLoadStatusSnapshot item)
    {
        lock (_gate)
        {
            if (sourceVersion != _sourceVersion || _snapshot.State != SolutionLoadState.Loading)
            {
                return;
            }

            if (_progress.Count == MaxProgressEntries)
            {
                _progress.RemoveAt(0);
            }

            _progress.Add(item);
        }
        Publish(current => current.SourceVersion == sourceVersion
            ? current with { Progress = ProgressSnapshot() }
            : current);
    }

    private IReadOnlyList<ProjectLoadStatusSnapshot> ProgressSnapshot()
    {
        lock (_gate)
        {
            return [.. _progress];
        }
    }

    private void PublishDiagnostics(string path, long sourceVersion, IReadOnlyList<string> messages)
    {
        var context = _paths.ToDocumentUri(path).AbsoluteUri;
        _diagnostics.Replace(DiagnosticProducer, context, sourceVersion, messages.Select((message, index) =>
            new WorkbenchDiagnostic(
                DiagnosticProducer,
                context,
                sourceVersion,
                StableIdentity(message, index),
                NovaSharp.Diagnostics.DiagnosticSeverity.Error,
                message)));
    }

    private static string StableIdentity(string message, int index)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{index}:{message}"));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 8));
    }

    private static bool RequiresFullReload(WorkspaceChangeBatch batch)
    {
        if (batch.Overflowed)
        {
            return true;
        }

        return batch.Changes.Any(change =>
        {
            var name = Path.GetFileName(change.Path);
            var extension = Path.GetExtension(change.Path);
            return (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                    && change.Kind != NovaSharp.Workspace.WorkspaceChangeKind.Changed)
                || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
                || name.Equals("project.assets.json", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsSourceContentChange(WorkspaceChange change) =>
        change.Kind == NovaSharp.Workspace.WorkspaceChangeKind.Changed
        && Path.GetExtension(change.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    private async Task WaitForCleanupAsync(Task cleanup, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ShutdownDeadline);
        try
        {
            await cleanup.WaitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _log.Write(LogLevel.Warning, "solution", "Solution evaluation did not stop before its cleanup deadline.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _log.Write(LogLevel.Warning, "solution", "Solution evaluation cleanup completed with an error.", exception);
        }
    }

    private SolutionWorkspaceMetrics CreateMetrics(TimeSpan elapsed, int retainedSnapshots)
    {
        int retainedReplicas;
        lock (_gate)
        {
            retainedReplicas = _replicas.Count;
        }
        return new(
            MutationCapacity,
            ReplicaCapacity,
            Math.Max(0, Volatile.Read(ref _pendingMutations)),
            retainedReplicas,
            Volatile.Read(ref _droppedReplicaSignals),
            Volatile.Read(ref _droppedReplicaSources),
            Volatile.Read(ref _canceledLoads),
            retainedSnapshots,
            elapsed);
    }

    private void Publish(Func<SolutionWorkspaceSnapshot, SolutionWorkspaceSnapshot> update)
    {
        SolutionWorkspaceSnapshot snapshot;
        lock (_gate)
        {
            snapshot = update(_snapshot) with { Version = _snapshot.Version + 1 };
            _snapshot = snapshot;
        }
        Changed?.Invoke(snapshot);
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value)
        {
            report(value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Task pending;
        lock (_gate)
        {
            _closing = true;
            _activeLoad?.Cancel();
            _reloadDelay?.Cancel();
            pending = Task.WhenAll(_loads.Append(_reloadTask).Append(_mutationConsumer));
        }
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _mutations.Writer.TryComplete();
        using var deadline = new CancellationTokenSource(ShutdownDeadline);
        var writerHeld = false;
        try
        {
            await pending.WaitAsync(deadline.Token).ConfigureAwait(false);
            await _writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            writerHeld = true;
            if (_loaded is not null)
            {
                await _loaded.DisposeAsync().ConfigureAwait(false);
                _loaded = null;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log.Write(LogLevel.Warning, "solution", "Solution shutdown cleanup completed with an error.", exception);
        }
        finally
        {
            if (writerHeld)
            {
                _writer.Release();
            }
        }
        lock (_gate)
        {
            _replicas.Clear();
            _mappings.Clear();
            _activeProjectByDocument.Clear();
            _rawBuildLog = [];
        }
        if (pending.IsCompleted)
        {
            _activeLoad?.Dispose();
            _shutdown.Dispose();
            _writer.Dispose();
        }
    }
}
