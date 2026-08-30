using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NovaSharp.Async;
using NovaSharp.Diagnostics;
using NovaSharp.Editing;
using NovaSharp.LanguageServices;
using NovaSharp.Platform;
using NovaSharp.Solutions;
using NovaSharp.Text;
using NovaSharp.Workspace;

const long idleMemoryLimitBytes = 400L * 1024 * 1024;
const int processDeadlineSeconds = 45;

var options = Options.Parse(args);
Directory.CreateDirectory(options.OutputDirectory);

var profileDirectory = Directory.CreateTempSubdirectory("novasharp-phase-profile-").FullName;
NativeResult provisioning;
NativeResult cold;
NativeResult warm;
NativeResult[] warmSamples;
NativeResult large;
try
{
    // Browser-profile creation is installation state, not repeatable process startup. Provision it once, retain that
    // measurement separately, then apply the cold/warm product budgets to launches sharing the resulting profile.
    provisioning = await RunAsync("provisioning", options, profileDirectory);
    cold = await RunAsync("cold", options, profileDirectory);
    warmSamples = new NativeResult[3];
    for (var index = 0; index < warmSamples.Length; index++)
    {
        warmSamples[index] = await RunAsync($"warm-{index + 1}", options, profileDirectory);
    }

    warm = warmSamples.OrderBy(result => result.InteractiveEditorMilliseconds).ElementAt(warmSamples.Length / 2);
    var largeSource = Path.Combine(Path.GetTempPath(), $"novasharp-large-{Guid.NewGuid():N}.cs");
    try
    {
        await File.WriteAllTextAsync(largeSource, CreateLargeDocument());
        large = await RunAsync("large", options, profileDirectory, largeSource);
    }
    finally
    {
        File.Delete(largeSource);
    }
}
finally
{
    await DeleteDirectoryAsync(profileDirectory);
}

var managed = await RunManagedPerformanceAsync(options);
var workspace = await RunWorkspacePerformanceAsync();
var solution = await RunSolutionPerformanceAsync(options.SolutionPath, options.SourcePath);
var failures = new List<string>();

Check(provisioning.Success, "browser profile provisioning", provisioning.Error);
Check(cold.Success, "cold native smoke", cold.Error);
Check(warmSamples.All(result => result.Success), "warm native smoke",
    string.Join("; ", warmSamples.Where(result => !result.Success).Select(result => result.Error)));
Check(cold.InteractiveEditorMilliseconds <= options.ColdStartLimitMilliseconds,
    $"cold startup <= {options.ColdStartLimitMilliseconds} ms", $"{cold.InteractiveEditorMilliseconds} ms");
Check(warm.InteractiveEditorMilliseconds <= options.WarmStartLimitMilliseconds,
    $"warm startup median <= {options.WarmStartLimitMilliseconds} ms", $"{warm.InteractiveEditorMilliseconds} ms");
Check(warm.WorkingSetBytes <= idleMemoryLimitBytes,
    $"idle working set <= {idleMemoryLimitBytes / 1024 / 1024} MB", $"{warm.WorkingSetBytes / 1024 / 1024} MB");
Check(warm.Editor?.LanguageProviderCount == 8,
    "eight Monaco C# provider registrations are active", warm.Editor?.LanguageProviderCount.ToString());
Check(large.Success, "10 MB native smoke", large.Error);
Check(large.WorkingSetBytes - large.BaselineWorkingSetBytes <= 60L * 1024 * 1024,
    "10 MB document adds <= 60 MB working set",
    $"{Math.Max(0, large.WorkingSetBytes - large.BaselineWorkingSetBytes) / 1024 / 1024} MB");
Check(managed.ReplicationP95Milliseconds <= 50,
    "managed replication p95 <= 50 ms", $"{managed.ReplicationP95Milliseconds:F2} ms");
Check(managed.ReplicationP99Milliseconds <= 150,
    "managed replication p99 <= 150 ms", $"{managed.ReplicationP99Milliseconds:F2} ms");
Check(managed.MaximumReplicationQueueDepth <= managed.ReplicationCapacity * 0.25,
    "managed replication queue <= 25% capacity", $"{managed.MaximumReplicationQueueDepth}/{managed.ReplicationCapacity}");
Check(managed.SaveBarrierP95Milliseconds <= 120,
    "1 MB save barrier p95 <= 120 ms", $"{managed.SaveBarrierP95Milliseconds:F2} ms");
Check(managed.SaveP95Milliseconds <= options.SaveLimitMilliseconds,
    $"1 MB save p95 <= {options.SaveLimitMilliseconds} ms", $"{managed.SaveP95Milliseconds:F2} ms");
Check(workspace.ExpansionMilliseconds <= 2_000,
    "20,000-entry Explorer expansion <= 2,000 ms", $"{workspace.ExpansionMilliseconds:F2} ms");
Check(workspace.ManagedMemoryIncreaseBytes <= 48L * 1024 * 1024,
    "20,000-entry Explorer managed memory <= 48 MB", $"{workspace.ManagedMemoryIncreaseBytes / 1024 / 1024} MB");
Check(workspace.WatcherRecoveryMilliseconds <= 2_000,
    "Explorer watcher recovery <= 2,000 ms", $"{workspace.WatcherRecoveryMilliseconds:F2} ms");
Check(workspace.WatcherCapacity == 1_024,
    "Explorer watcher queue capacity is 1,024", workspace.WatcherCapacity.ToString());
Check(solution.LoadMilliseconds <= 20_000,
    "representative solution load <= 20,000 ms", $"{solution.LoadMilliseconds:F2} ms");
Check(solution.ReloadMilliseconds <= 15_000,
    "representative solution reload <= 15,000 ms", $"{solution.ReloadMilliseconds:F2} ms");
Check(solution.WarmCacheDisplayMilliseconds <= 500,
    "warm solution tree display <= 500 ms", $"{solution.WarmCacheDisplayMilliseconds:F2} ms");
Check(solution.WarmValidatedLoadMilliseconds <= 15_000,
    "warm validated solution load <= 15,000 ms", $"{solution.WarmValidatedLoadMilliseconds:F2} ms");
Check(solution.WarmCacheExcludedRoslyn,
    "warm display cache is excluded from Roslyn authority", solution.WarmCacheExcludedRoslyn.ToString());
Check(solution.ForegroundReplicaMilliseconds <= 500,
    "foreground Roslyn replica barrier <= 500 ms", $"{solution.ForegroundReplicaMilliseconds:F2} ms");
Check(solution.FirstSemanticModelMilliseconds <= 5_000,
    "first semantic model <= 5,000 ms", $"{solution.FirstSemanticModelMilliseconds:F2} ms");
Check(solution.ManagedMemoryIncreaseBytes <= 384L * 1024 * 1024,
    "solution workspace managed memory <= 384 MB", $"{solution.ManagedMemoryIncreaseBytes / 1024 / 1024} MB");
Check(solution.RetainedRoslynSnapshots == 1,
    "one current Roslyn snapshot retained", solution.RetainedRoslynSnapshots.ToString());
Check(solution.MutationCapacity == 128 && solution.PendingMutations <= solution.MutationCapacity,
    "Roslyn mutation queue stays within its 128-item bound", $"{solution.PendingMutations}/{solution.MutationCapacity}");
Check(solution.ReplicaCapacity == 1_024 && solution.RetainedReplicas <= solution.ReplicaCapacity,
    "Roslyn replica cache stays within its 1,024-source bound", $"{solution.RetainedReplicas}/{solution.ReplicaCapacity}");
Check(solution.ProjectContexts >= 6,
    "representative SDK project contexts loaded", solution.ProjectContexts.ToString());
Check(solution.LinkedDocumentContexts >= 2,
    "linked document maps to multiple contexts", solution.LinkedDocumentContexts.ToString());
Check(solution.Language.FirstCompletionMilliseconds <= 750,
    "first project-aware completion <= 750 ms", $"{solution.Language.FirstCompletionMilliseconds:F2} ms");
Check(solution.Language.CompletionWarmupMilliseconds <= 1_500,
    "active-document completion warm-up <= 1,500 ms", $"{solution.Language.CompletionWarmupMilliseconds:F2} ms");
Check(solution.Language.WarmCompletionMilliseconds <= 200,
    "warm completion <= 200 ms", $"{solution.Language.WarmCompletionMilliseconds:F2} ms");
Check(solution.Language.CompletionListCacheHits >= 1 && solution.Language.CompletionListCacheEntries is > 0 and <= 16,
    "exact-version completion cache is used and bounded",
    $"{solution.Language.CompletionListCacheHits} hits, {solution.Language.CompletionListCacheEntries}/16 entries");
Check(solution.Language.CompletionWarmupFailures == 0,
    "background completion warm-up is recoverable", $"{solution.Language.CompletionWarmupFailures} failures");
Check(solution.Language.SignatureMilliseconds <= 250,
    "warm signature help <= 250 ms", $"{solution.Language.SignatureMilliseconds:F2} ms");
Check(solution.Language.HoverMilliseconds <= 250,
    "warm hover <= 250 ms", $"{solution.Language.HoverMilliseconds:F2} ms");
Check(solution.Language.FormattingMilliseconds <= 1_000,
    "format selection <= 1,000 ms", $"{solution.Language.FormattingMilliseconds:F2} ms");
Check(solution.Language.SemanticTokensMilliseconds <= 1_000,
    "semantic token refresh <= 1,000 ms", $"{solution.Language.SemanticTokensMilliseconds:F2} ms");
Check(solution.Language.ExpectedItemFound,
    "language features use the unsaved Roslyn replica", "expected completion/signature/hover/format/semantic results");
Check(solution.Language.Capacity == 128
        && solution.Language.Pending == 0
        && solution.Language.MaximumPending <= solution.Language.Capacity,
    "language-service work stays within its explicit bound",
    $"pending {solution.Language.Pending}, maximum {solution.Language.MaximumPending}, capacity {solution.Language.Capacity}");

var record = new VerificationRecord(
    options.FixtureName,
    Environment.ProcessorCount,
    Environment.Version.ToString(),
    options.ColdStartLimitMilliseconds,
    options.WarmStartLimitMilliseconds,
    options.SaveLimitMilliseconds,
    provisioning,
    cold,
    warm,
    warmSamples,
    large,
    managed,
    workspace,
    solution,
    failures);
var recordPath = Path.Combine(options.OutputDirectory, "phase-01-07-native.json");
await File.WriteAllTextAsync(
    recordPath,
    JsonSerializer.Serialize(record, Serialization.JsonOptions),
    CancellationToken.None);

Console.WriteLine($"Verification record: {recordPath}");
return failures.Count == 0 ? 0 : 1;

void Check(bool condition, string name, string? detail)
{
    Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}")}");
    if (!condition)
    {
        failures.Add(string.IsNullOrWhiteSpace(detail) ? name : $"{name}: {detail}");
    }
}

static async Task<NativeResult> RunAsync(
    string label,
    Options options,
    string profilePath,
    string? sourcePath = null)
{
    var resultPath = Path.Combine(options.OutputDirectory, $"{label}-native.json");
    var start = new ProcessStartInfo(options.ApplicationPath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    start.ArgumentList.Add("--phase-smoke-source");
    start.ArgumentList.Add(sourcePath ?? options.SourcePath);
    start.ArgumentList.Add("--phase-smoke-solution");
    start.ArgumentList.Add(options.SolutionPath);
    start.ArgumentList.Add("--phase-smoke-result");
    start.ArgumentList.Add(resultPath);
    start.ArgumentList.Add("--phase-smoke-profile");
    start.ArgumentList.Add(profilePath);

    using var process = Process.Start(start) ?? throw new InvalidOperationException("The NovaSharp process did not start.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(processDeadlineSeconds));

    try
    {
        await process.WaitForExitAsync(deadline.Token);
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException($"NovaSharp did not finish its {label} native smoke test in {processDeadlineSeconds} seconds.");
    }

    var output = await standardOutput;
    var error = await standardError;
    if (!string.IsNullOrWhiteSpace(output))
    {
        Console.WriteLine(output.TrimEnd());
    }

    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine(error.TrimEnd());
    }

    if (!File.Exists(resultPath))
    {
        throw new InvalidOperationException($"NovaSharp exited with {process.ExitCode} without writing {resultPath}.");
    }

    await using var stream = File.OpenRead(resultPath);
    return await JsonSerializer.DeserializeAsync<NativeResult>(stream, Serialization.JsonOptions)
        ?? throw new InvalidDataException($"{resultPath} did not contain a smoke result.");
}

static async Task DeleteDirectoryAsync(string path)
{
    var deadline = Stopwatch.StartNew();
    while (true)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException &&
            deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(100);
        }
    }
}

static async Task<ManagedPerformanceRecord> RunManagedPerformanceAsync(Options options)
{
    const int replicationCapacity = 256;
    var replicationSamples = new double[1_000];
    var replica = new DocumentReplica(string.Empty, sequence: 1, alternativeSequence: 1);
    await using (var pump = new DocumentReplicationPump(
        replica,
        _ => Task.FromResult(replica.Snapshot()),
        replicationCapacity))
    {
        var maximumQueueDepth = 0;
        for (var index = 0; index < replicationSamples.Length; index++)
        {
            var sequence = index + 2L;
            var started = Stopwatch.GetTimestamp();
            if (!pump.TryEnqueue(new TextEditBatch(
                options.SourcePath,
                sequence - 1,
                sequence,
                sequence,
                EditOrigins.User,
                [new TextEdit(index, index, "x")])))
            {
                throw new InvalidOperationException("The managed replication benchmark overflowed.");
            }

            maximumQueueDepth = Math.Max(maximumQueueDepth, pump.QueueDepth);
            await pump.WaitForSequenceAsync(sequence, CancellationToken.None);
            replicationSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var barrierSamples = new double[20];
        var barrierReplica = new DocumentReplica(new string('x', 1024 * 1024), sequence: 1, alternativeSequence: 1);
        await using (var barrierPump = new DocumentReplicationPump(
            barrierReplica,
            _ => Task.FromResult(barrierReplica.Snapshot()),
            replicationCapacity))
        {
            for (var index = 0; index < barrierSamples.Length; index++)
            {
                var sequence = index + 2L;
                var started = Stopwatch.GetTimestamp();
                if (!barrierPump.TryEnqueue(new TextEditBatch(
                    options.SourcePath,
                    sequence - 1,
                    sequence,
                    sequence,
                    EditOrigins.User,
                    [new TextEdit(barrierReplica.Length, barrierReplica.Length, "x")])))
                {
                    throw new InvalidOperationException("The save-barrier benchmark overflowed.");
                }

                await barrierPump.WaitForSequenceAsync(sequence, CancellationToken.None);
                barrierSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }

        var benchmarkDirectory = Path.Combine(Path.GetTempPath(), $"novasharp-phase-verification-{Guid.NewGuid():N}");
        Directory.CreateDirectory(benchmarkDirectory);
        try
        {
            var target = Path.Combine(benchmarkDirectory, "one-megabyte.cs");
            var text = new string('x', 1024 * 1024 - 1) + "\n";
            await File.WriteAllTextAsync(target, text);
            var store = new DocumentFileStore();
            var paths = new WorkspacePaths();
            await using var queue = new BoundedWorkQueue(capacity: 4, workerCount: 1);
            var saver = new DocumentSaver(paths, store, new DocumentTextCodec(), queue);
            var record = new DocumentRecord(
                paths.ToDocumentUri(target),
                target,
                Path.GetFileName(target),
                TextEncodings.Utf8,
                LineEndingStyle.Lf,
                LineEndingsWereMixed: false,
                DecodedWithFallback: false,
                store.GetState(target),
                SavedSequence: 1);
            var snapshot = new DocumentSnapshot(text, Sequence: 2, AlternativeSequence: 2);
            var saveSamples = new double[20];
            for (var index = 0; index < saveSamples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var result = await saver.SaveAsync(record, snapshot, cancellationToken: CancellationToken.None);
                saveSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (result.Status != DocumentSaveStatus.Saved)
                {
                    throw new InvalidOperationException($"The managed save benchmark failed: {result.Message}");
                }

                record = result.Record;
            }

            return new ManagedPerformanceRecord(
                Percentile(replicationSamples, 0.95),
                Percentile(replicationSamples, 0.99),
                replicationCapacity,
                maximumQueueDepth,
                Percentile(barrierSamples, 0.95),
                Percentile(saveSamples, 0.95));
        }
        finally
        {
            Directory.Delete(benchmarkDirectory, recursive: true);
        }
    }
}

static async Task<WorkspacePerformanceRecord> RunWorkspacePerformanceAsync()
{
    const int entryCount = 20_000;
    var fixture = Directory.CreateTempSubdirectory("novasharp-explorer-fixture-").FullName;
    var state = Directory.CreateTempSubdirectory("novasharp-explorer-state-").FullName;
    try
    {
        for (var index = 0; index < entryCount; index++)
        {
            await using var stream = new FileStream(
                Path.Combine(fixture, $"file-{index:D5}.cs"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                1,
                useAsync: true);
        }

        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var paths = new WorkspacePaths();
        var store = new DocumentFileStore();
        await using var queue = new BoundedWorkQueue(32, 2);
        await using var watcher = new FileSystemWorkspaceWatcher(paths, 1_024);
        var persistence = new WorkspacePersistenceService(new VerificationApplicationPaths(state), store, queue);
        var notifications = new NotificationService(new BoundedWorkbenchLog());
        await using var explorer = new WorkspaceExplorerService(
            paths,
            new WorkspaceFileSystem(paths, queue),
            watcher,
            persistence,
            notifications);

        var expansionStarted = Stopwatch.GetTimestamp();
        await explorer.OpenAsync(fixture);
        var expansionMilliseconds = Stopwatch.GetElapsedTime(expansionStarted).TotalMilliseconds;
        if (explorer.Snapshot.Root?.Children?.Count != entryCount)
        {
            throw new InvalidOperationException("The 20,000-entry Explorer fixture was not fully enumerated.");
        }

        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        // Watcher subscription is provisioning, like browser-profile creation above. Let the native watcher reach its
        // steady state before measuring an event that happens after it is ready.
        await Task.Delay(500);
        var external = Path.Combine(fixture, "external.cs");
        var watcherStarted = Stopwatch.GetTimestamp();
        await File.WriteAllTextAsync(external, "class External;\n");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline
            && explorer.Snapshot.Root?.Children?.Any(node => node.Name == "external.cs") != true)
        {
            await Task.Delay(10);
        }
        if (explorer.Snapshot.Root?.Children?.Any(node => node.Name == "external.cs") != true)
        {
            throw new TimeoutException("The Explorer did not recover the external create event.");
        }

        return new WorkspacePerformanceRecord(
            entryCount,
            expansionMilliseconds,
            Math.Max(0, memoryAfter - memoryBefore),
            Stopwatch.GetElapsedTime(watcherStarted).TotalMilliseconds,
            watcher.Capacity);
    }
    finally
    {
        await DeleteDirectoryAsync(fixture);
        await DeleteDirectoryAsync(state);
    }
}

static async Task<SolutionPerformanceRecord> RunSolutionPerformanceAsync(string solutionPath, string sourcePath)
{
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    var paths = new WorkspacePaths();
    var log = new BoundedWorkbenchLog();
    var notifications = new NotificationService(log);
    await using var queue = new BoundedWorkQueue(32, 2);
    await using var service = new SolutionWorkspaceService(
        paths,
        new MSBuildSolutionLoader(),
        queue,
        new DiagnosticStore(),
        notifications,
        log);
    await using var language = new CSharpLanguageService(service);

    var loadStarted = Stopwatch.GetTimestamp();
    await service.OpenAsync(solutionPath);
    var loadMilliseconds = Stopwatch.GetElapsedTime(loadStarted).TotalMilliseconds;
    if (service.Snapshot.State != SolutionLoadState.Ready)
    {
        throw new InvalidOperationException(service.Snapshot.Error ?? "The representative solution did not load.");
    }

    var sourceUri = paths.ToDocumentUri(sourcePath);
    var contexts = service.GetDocumentContexts(sourceUri);
    if (contexts.Count == 0)
    {
        throw new InvalidOperationException("The phase source file has no Roslyn context.");
    }
    var text = await File.ReadAllTextAsync(sourcePath);
    const string completionProbe = "\ninternal sealed class PhaseSevenProbe{void Probe(){_ = string.Concat(\"a\",\"b\");_ = string.Empt;}}\n";
    var replicaText = text + completionProbe;
    var replica = new DocumentReplica(replicaText, 1, 1);
    var replicaStarted = Stopwatch.GetTimestamp();
    service.QueueReplica(new DocumentReplicaChange(sourceUri, sourcePath, replica, 1));
    await service.WaitForReplicaAsync(sourceUri, 1);
    var replicaMilliseconds = Stopwatch.GetElapsedTime(replicaStarted).TotalMilliseconds;

    var semanticStarted = Stopwatch.GetTimestamp();
    var semanticModel = await service.CurrentSolution!.GetDocument(contexts[0].DocumentId)!.GetSemanticModelAsync();
    if (semanticModel is null)
    {
        throw new InvalidOperationException("Roslyn did not produce a semantic model.");
    }
    var semanticMilliseconds = Stopwatch.GetElapsedTime(semanticStarted).TotalMilliseconds;

    var activeContext = service.GetDocumentContexts(sourceUri).Single(context => context.IsActive);
    var completionWarmupStarted = Stopwatch.GetTimestamp();
    await language.WarmCompletionAsync(
        sourceUri,
        activeContext.ProjectId.Id.ToString(),
        service.Snapshot.SourceVersion,
        1);
    var completionWarmupMilliseconds = Stopwatch.GetElapsedTime(completionWarmupStarted).TotalMilliseconds;
    var completionPosition = replicaText.LastIndexOf("Empt", StringComparison.Ordinal) + "Empt".Length;
    LanguageRequest CompletionRequest(string id) => new(
        id,
        sourceUri.AbsoluteUri,
        activeContext.ProjectId.Id.ToString(),
        service.Snapshot.SourceVersion,
        1,
        completionPosition,
        IsExplicit: true);
    var firstCompletionStarted = Stopwatch.GetTimestamp();
    var firstCompletion = await language.GetCompletionsAsync(CompletionRequest("first-completion"));
    var firstCompletionMilliseconds = Stopwatch.GetElapsedTime(firstCompletionStarted).TotalMilliseconds;
    var warmCompletionStarted = Stopwatch.GetTimestamp();
    var warmCompletion = await language.GetCompletionsAsync(CompletionRequest("warm-completion"));
    var warmCompletionMilliseconds = Stopwatch.GetElapsedTime(warmCompletionStarted).TotalMilliseconds;
    var signaturePosition = replicaText.LastIndexOf("\",\"", StringComparison.Ordinal) + 2;
    _ = await language.GetSignatureHelpAsync(CompletionRequest("signature-warmup") with
    {
        Position = signaturePosition,
        TriggerCharacter = ",",
    });
    var signatureStarted = Stopwatch.GetTimestamp();
    var signature = await language.GetSignatureHelpAsync(CompletionRequest("signature-measured") with
    {
        Position = signaturePosition,
        TriggerCharacter = ",",
    });
    var signatureMilliseconds = Stopwatch.GetElapsedTime(signatureStarted).TotalMilliseconds;
    var hoverStarted = Stopwatch.GetTimestamp();
    var hover = await language.GetHoverAsync(CompletionRequest("hover") with
    {
        Position = replicaText.LastIndexOf("Concat", StringComparison.Ordinal) + 1,
    });
    var hoverMilliseconds = Stopwatch.GetElapsedTime(hoverStarted).TotalMilliseconds;
    var probeStart = replicaText.Length - completionProbe.Length;
    var formattingStarted = Stopwatch.GetTimestamp();
    var formatting = await language.FormatAsync(CompletionRequest("format") with
    {
        Position = probeStart,
        RangeStart = probeStart,
        RangeEnd = replicaText.Length,
    });
    var formattingMilliseconds = Stopwatch.GetElapsedTime(formattingStarted).TotalMilliseconds;
    var semanticStartedAt = Stopwatch.GetTimestamp();
    var semanticTokens = await language.GetSemanticTokensAsync(CompletionRequest("semantic") with
    {
        Position = probeStart,
        RangeStart = probeStart,
        RangeEnd = replicaText.Length,
    });
    var semanticTokensMilliseconds = Stopwatch.GetElapsedTime(semanticStartedAt).TotalMilliseconds;
    var languageMetrics = language.Metrics;

    var reloadStarted = Stopwatch.GetTimestamp();
    await service.ReloadAsync();
    var reloadMilliseconds = Stopwatch.GetElapsedTime(reloadStarted).TotalMilliseconds;
    var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
    var linkedPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "tests", "fixtures", "phase-06", "Shared.cs");
    var metrics = service.CurrentMetrics;

    double warmCacheDisplayMilliseconds;
    double warmValidatedLoadMilliseconds;
    bool warmCacheExcludedRoslyn;
    var warmState = Directory.CreateTempSubdirectory("novasharp-solution-warm-").FullName;
    try
    {
        var workspaceRoot = Path.GetDirectoryName(solutionPath)!;
        var warmCache = new SolutionWarmCache(
            new VerificationApplicationPaths(warmState),
            paths,
            new DocumentFileStore(),
            queue);
        await warmCache.SaveAsync(workspaceRoot, service.Snapshot);
        await using var warmService = new SolutionWorkspaceService(
            paths,
            new MSBuildSolutionLoader(),
            queue,
            new DiagnosticStore(),
            notifications,
            log,
            warmCache: warmCache,
            workspaceRoot: () => workspaceRoot);
        var cacheDisplayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warmStarted = Stopwatch.GetTimestamp();
        warmService.Changed += snapshot =>
        {
            if (snapshot.RestoredFromWarmCache)
            {
                cacheDisplayed.TrySetResult();
            }
        };
        var restore = warmService.RestoreAsync(workspaceRoot);
        await cacheDisplayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        warmCacheDisplayMilliseconds = Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
        warmCacheExcludedRoslyn = warmService.CurrentSolution is null;
        if (!await restore || warmService.Snapshot.State != SolutionLoadState.Ready)
        {
            throw new InvalidOperationException("The warm solution cache did not complete live validation.");
        }
        warmValidatedLoadMilliseconds = Stopwatch.GetElapsedTime(warmStarted).TotalMilliseconds;
    }
    finally
    {
        await DeleteDirectoryAsync(warmState);
    }

    return new SolutionPerformanceRecord(
        service.Snapshot.Projects.Count,
        service.GetDocumentContexts(paths.ToDocumentUri(linkedPath)).Count,
        loadMilliseconds,
        reloadMilliseconds,
        warmCacheDisplayMilliseconds,
        warmValidatedLoadMilliseconds,
        warmCacheExcludedRoslyn,
        replicaMilliseconds,
        semanticMilliseconds,
        Math.Max(0, memoryAfter - memoryBefore),
        metrics.RetainedRoslynSnapshots,
        metrics.MutationQueueCapacity,
        metrics.PendingMutations,
        metrics.ReplicaCapacity,
        metrics.RetainedReplicas,
        new LanguagePerformanceRecord(
            completionWarmupMilliseconds,
            firstCompletionMilliseconds,
            warmCompletionMilliseconds,
            signatureMilliseconds,
            hoverMilliseconds,
            formattingMilliseconds,
            semanticTokensMilliseconds,
            firstCompletion?.Items.Any(item => item.Label == "Empty") == true
                && warmCompletion?.Items.Any(item => item.Label == "Empty") == true
                && signature?.Signatures.Count > 0
                && hover is not null
                && formatting?.Edits.Count > 0
                && semanticTokens?.Tokens.Count > 0,
            languageMetrics.Capacity,
            languageMetrics.Pending,
            languageMetrics.MaximumPending,
            languageMetrics.LastQueueDelayMilliseconds,
            languageMetrics.LastReplicaBarrierMilliseconds,
            languageMetrics.LastRoslynMilliseconds,
            languageMetrics.LastTotalMilliseconds,
            languageMetrics.CompletionListCacheHits,
            languageMetrics.CompletionListCacheEntries,
            languageMetrics.CompletionWarmupFailures));
}

static string CreateLargeDocument()
{
    const int length = 10 * 1024 * 1024;
    const string line = "// 0123456789abcdef0123456789abcdef0123456789abcdef0123456789ab\n";
    var text = new StringBuilder(length);
    while (text.Length + line.Length <= length)
    {
        text.Append(line);
    }

    var remaining = length - text.Length;
    if (remaining > 0)
    {
        text.Append('/', remaining - 1).Append('\n');
    }

    return text.ToString();
}

static double Percentile(double[] values, double percentile)
{
    Array.Sort(values);
    return values[Math.Min(values.Length - 1, (int) Math.Ceiling(values.Length * percentile) - 1)];
}

internal sealed record Options(
    string ApplicationPath,
    string SourcePath,
    string SolutionPath,
    string OutputDirectory,
    string FixtureName,
    int ColdStartLimitMilliseconds,
    int WarmStartLimitMilliseconds,
    int SaveLimitMilliseconds)
{
    internal static Options Parse(string[] arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (index + 1 >= arguments.Length || !arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException("Options must be supplied as --name value pairs.");
            }

            values.Add(arguments[index], arguments[index + 1]);
        }

        string Required(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing {name}.");

        int PositiveInteger(string name, int defaultValue)
        {
            if (!values.TryGetValue(name, out var value))
            {
                return defaultValue;
            }

            return int.TryParse(value, out var parsed) && parsed > 0
                ? parsed
                : throw new ArgumentException($"{name} must be a positive integer.");
        }

        return new Options(
            Path.GetFullPath(Required("--application")),
            Path.GetFullPath(Required("--source")),
            Path.GetFullPath(Required("--solution")),
            Path.GetFullPath(Required("--output")),
            Required("--fixture-name"),
            PositiveInteger("--cold-start-limit", 2_500),
            PositiveInteger("--warm-start-limit", 1_600),
            PositiveInteger("--save-limit", 250));
    }
}

internal sealed record EditorInfo(
    string MonacoVersion,
    bool DedicatedWorker,
    int ModelCount,
    int ExternalRequestCount,
    int DocumentLength,
    int ReplicationCapacity,
    int ReplicationQueueDepth,
    int ReplicationMaximumQueueDepth,
    int ReplicationOverflowCount,
    int LanguageProviderCount,
    int LanguageRequestCount,
    double LanguageRequestP95Milliseconds);

internal sealed record NativeResult(
    bool Success,
    string RuntimeIdentifier,
    string OsDescription,
    string Architecture,
    long InteractiveEditorMilliseconds,
    long BaselineWorkingSetBytes,
    long WorkingSetBytes,
    EditorInfo? Editor,
    int DocumentLength,
    bool SolutionLoaded,
    int ProjectContexts,
    string? Error);

internal sealed record VerificationRecord(
    string FixtureName,
    int ProcessorCount,
    string DotNetRuntime,
    int ColdStartLimitMilliseconds,
    int WarmStartLimitMilliseconds,
    int SaveLimitMilliseconds,
    NativeResult Provisioning,
    NativeResult Cold,
    NativeResult Warm,
    IReadOnlyList<NativeResult> WarmSamples,
    NativeResult LargeDocument,
    ManagedPerformanceRecord ManagedPerformance,
    WorkspacePerformanceRecord WorkspacePerformance,
    SolutionPerformanceRecord SolutionPerformance,
    IReadOnlyList<string> Failures);

internal sealed record ManagedPerformanceRecord(
    double ReplicationP95Milliseconds,
    double ReplicationP99Milliseconds,
    int ReplicationCapacity,
    int MaximumReplicationQueueDepth,
    double SaveBarrierP95Milliseconds,
    double SaveP95Milliseconds);

internal sealed record WorkspacePerformanceRecord(
    int EntryCount,
    double ExpansionMilliseconds,
    long ManagedMemoryIncreaseBytes,
    double WatcherRecoveryMilliseconds,
    int WatcherCapacity);

internal sealed record SolutionPerformanceRecord(
    int ProjectContexts,
    int LinkedDocumentContexts,
    double LoadMilliseconds,
    double ReloadMilliseconds,
    double WarmCacheDisplayMilliseconds,
    double WarmValidatedLoadMilliseconds,
    bool WarmCacheExcludedRoslyn,
    double ForegroundReplicaMilliseconds,
    double FirstSemanticModelMilliseconds,
    long ManagedMemoryIncreaseBytes,
    int RetainedRoslynSnapshots,
    int MutationCapacity,
    int PendingMutations,
    int ReplicaCapacity,
    int RetainedReplicas,
    LanguagePerformanceRecord Language);

internal sealed record LanguagePerformanceRecord(
    double CompletionWarmupMilliseconds,
    double FirstCompletionMilliseconds,
    double WarmCompletionMilliseconds,
    double SignatureMilliseconds,
    double HoverMilliseconds,
    double FormattingMilliseconds,
    double SemanticTokensMilliseconds,
    bool ExpectedItemFound,
    int Capacity,
    int Pending,
    int MaximumPending,
    double LastQueueDelayMilliseconds,
    double LastReplicaBarrierMilliseconds,
    double LastRoslynMilliseconds,
    double LastTotalMilliseconds,
    long CompletionListCacheHits,
    int CompletionListCacheEntries,
    long CompletionWarmupFailures);

internal sealed class VerificationApplicationPaths(string directory) : IApplicationPaths
{
    public string ConfigurationDirectory { get; } = directory;
}

internal static class Serialization
{
    internal static JsonSerializerOptions JsonOptions { get; } =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
