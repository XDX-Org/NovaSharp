using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class LanguageServerManager : ILspDocumentSink, IAsyncDisposable
{
    private static readonly string[] SemanticTokenTypes =
    [
        "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable",
        "property", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment",
        "string", "number", "regexp", "operator", "decorator", "recordClass", "recordStruct", "delegate", "field"
    ];
    private static readonly string[] SemanticTokenModifiers = ["declaration", "definition", "readonly", "static",
        "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary", "ReassignedVariable"];
    private readonly LanguageServerDefinition _definition;
    private readonly string _workspace;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Queue<DateTime> _crashes = new();
    private RazorHtmlBridge? _razorHtml;
    private Func<JsonElement, CancellationToken, Task<bool>>? _applyWorkspaceEdit;
    private readonly object _watchedGate = new();
    private readonly Dictionary<string, int> _watchedChanges = [];
    private FileSystemWatcher? _watcher;
    private Timer? _watchedTimer;
    private LanguageServerProcess? _process;
    private LspClient? _client;
    private string? _lastCrash;

    internal LanguageServerManager(LanguageServerDefinition definition, string workspace)
    {
        _definition = definition;
        _workspace = Path.GetFullPath(workspace);
        Status = definition.Launch is null
            ? new(LanguageServerState.Unavailable, Detail: definition.UnavailableReason)
            : new(LanguageServerState.Stopped);
    }

    internal event Action<LanguageServerStatus>? StatusChanged;
    internal event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;
    internal event Action? DiagnosticRefreshRequested;
    internal event Action? Ready;
    internal event Action? CapabilitiesChanged;
    internal LanguageServerStatus Status { get; private set; }
    internal string? LastCrash => _lastCrash;
    internal long WorkingSet => _process?.WorkingSet ?? 0;
    internal LanguageServerKind Kind => _definition.Kind;
    internal string WorkspaceRoot => _workspace;
    internal JsonElement Capabilities { get; private set; }
    public bool IsReady => Status.State == LanguageServerState.Ready && _client is not null;
    internal bool IsMethodRegistered(string method) => _client?.IsRegistered(method) == true;
    internal IReadOnlyList<LspRegistration> Registrations(string method) => _client?.Registrations(method) ?? [];

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_definition.Launch is null) return;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null) return;
            SetStatus(LanguageServerState.Starting);
            try
            {
                _process = LanguageServerProcess.Start(_definition.Launch);
                _client = new(_process.Output, _process.Input, _razorHtml, _applyWorkspaceEdit);
                _client.DiagnosticsPublished += OnDiagnostics;
                _client.DiagnosticRefreshRequested += OnDiagnosticRefresh;
                _client.CapabilitiesChanged += () => CapabilitiesChanged?.Invoke();
                var projectInitialization = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _client.ProjectInitializationCompleted += () => projectInitialization.TrySetResult();
                using var initializeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                initializeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var capabilities = new
                {
                    general = new { positionEncodings = new[] { "utf-16" } },
                    workspace = new
                    {
                        workspaceFolders = true, configuration = true, applyEdit = true,
                        workspaceEdit = new
                        {
                            documentChanges = true,
                            resourceOperations = new[] { "create", "rename", "delete" },
                            failureHandling = "transactional",
                            normalizesLineEndings = false,
                            changeAnnotationSupport = new { groupsOnLabel = true }
                        },
                        didChangeWatchedFiles = new { dynamicRegistration = true, relativePatternSupport = true }
                    },
                    textDocument = new { synchronization = new { dynamicRegistration = true, willSave = false, didSave = true },
                        publishDiagnostics = new { relatedInformation = true, tagSupport = new { valueSet = new[] { 1, 2 } } },
                        diagnostic = new { dynamicRegistration = true, relatedDocumentSupport = true },
                        completion = new
                        {
                            dynamicRegistration = true,
                            completionItem = new
                            {
                                snippetSupport = true, commitCharactersSupport = true, deprecatedSupport = true,
                                preselectSupport = true, insertReplaceSupport = true,
                                documentationFormat = new[] { "markdown", "plaintext" },
                                resolveSupport = new { properties = new[] { "documentation", "detail", "additionalTextEdits", "command" } }
                            },
                            contextSupport = true
                        },
                        hover = new { dynamicRegistration = true, contentFormat = new[] { "markdown", "plaintext" } },
                        signatureHelp = new { dynamicRegistration = true },
                        semanticTokens = new
                        {
                            dynamicRegistration = true,
                            requests = new { range = true, full = new { delta = false } },
                            tokenTypes = SemanticTokenTypes, tokenModifiers = SemanticTokenModifiers,
                            formats = new[] { "relative" }, overlappingTokenSupport = false, multilineTokenSupport = false
                        },
                        formatting = new { dynamicRegistration = true }, rangeFormatting = new { dynamicRegistration = true },
                        definition = new { dynamicRegistration = true, linkSupport = true },
                        typeDefinition = new { dynamicRegistration = true, linkSupport = true },
                        implementation = new { dynamicRegistration = true, linkSupport = true },
                        references = new { dynamicRegistration = true }, documentSymbol = new { dynamicRegistration = true },
                        rename = new { dynamicRegistration = true, prepareSupport = true },
                        codeAction = new { dynamicRegistration = true, dataSupport = true,
                            resolveSupport = new { properties = new[] { "edit", "command" } } }
                    },
                    window = new { workDoneProgress = true, showDocument = new { support = true } }
                };
                SetStatus(LanguageServerState.LoadingWorkspace);
                var root = LspConverters.FileUri(_workspace);
                var result = await _client.InitializeAsync(new(Environment.ProcessId, root.AbsoluteUri, capabilities,
                    new("NovaSharp"), [new(root.AbsoluteUri, Path.GetFileName(_workspace))]), initializeTimeout.Token);
                Capabilities = result.Capabilities.Clone();
                if (_definition.Kind == LanguageServerKind.RoslynRazor
                    && await OpenRoslynWorkspaceAsync(initializeTimeout.Token))
                    await projectInitialization.Task.WaitAsync(cancellationToken);
                SetStatus(LanguageServerState.Ready, result.ServerInfo?.Name, result.ServerInfo?.Version);
                StartWatching();
                Ready?.Invoke();
                _ = ObserveExitAsync(_process);
            }
            catch (Exception exception)
            {
                await CleanupAsync();
                SetStatus(LanguageServerState.Unavailable, detail: SafeMessage(exception));
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default)
    {
        if (_client is not null) await _client.NotifyAsync(method, parameters, cancellationToken);
        if (_definition.Kind == LanguageServerKind.RoslynRazor && method == "textDocument/didClose"
            && parameters is LspDidCloseTextDocumentParams closed && _razorHtml is not null)
            await _razorHtml.CloseAsync(closed.TextDocument.Uri, cancellationToken);
    }

    internal void SetRazorHtmlBridge(RazorHtmlBridge bridge) => _razorHtml = bridge;
    internal void SetApplyWorkspaceEditHandler(Func<JsonElement, CancellationToken, Task<bool>> handler) =>
        _applyWorkspaceEdit = handler;

    internal async Task<T?> RequestAsync<T>(string method, object parameters, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (!IsReady || client is null) return default;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(method is "textDocument/completion" or "textDocument/hover" or "textDocument/signatureHelp"
            ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));
        return await client.RequestAsync<T>(method, parameters, timeout.Token);
    }

    internal async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try { SetStatus(LanguageServerState.Restarting); await CleanupAsync(); }
        finally { _lifecycle.Release(); }
        _crashes.Clear();
        await StartAsync(cancellationToken);
    }

    private async Task ObserveExitAsync(LanguageServerProcess process)
    {
        await process.Exited;
        if (!ReferenceEquals(process, _process) || Status.State == LanguageServerState.Stopped) return;
        _lastCrash = CrashDetail(process);
        var now = DateTime.UtcNow;
        _crashes.Enqueue(now);
        while (_crashes.TryPeek(out var crash) && now - crash > TimeSpan.FromSeconds(180)) _crashes.Dequeue();
        await _lifecycle.WaitAsync();
        try { await CleanupAsync(); }
        finally { _lifecycle.Release(); }
        if (_crashes.Count >= 5) { SetStatus(LanguageServerState.Unavailable, detail: $"The server repeatedly crashed. {_lastCrash}"); return; }
        SetStatus(LanguageServerState.Restarting, detail: _lastCrash);
        await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, _crashes.Count - 1)));
        await StartAsync();
    }

    private void OnDiagnostics(LspPublishDiagnosticsParams parameters) => DiagnosticsPublished?.Invoke(parameters);
    private void OnDiagnosticRefresh() => DiagnosticRefreshRequested?.Invoke();

    private async Task<bool> OpenRoslynWorkspaceAsync(CancellationToken cancellationToken)
    {
        var solutions = Directory.EnumerateFiles(_workspace, "*.sln", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(_workspace, "*.slnx", SearchOption.TopDirectoryOnly)).Take(2).ToArray();
        if (solutions.Length == 1)
        {
            await _client!.NotifyAsync("solution/open",
                new { solution = LspConverters.FileUri(solutions[0]).AbsoluteUri }, cancellationToken);
            return true;
        }
        var projects = Directory.EnumerateFiles(_workspace, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "bin" or "obj"))
            .Take(200).Select(path => LspConverters.FileUri(path).AbsoluteUri).ToArray();
        if (projects.Length > 0)
        {
            await _client!.NotifyAsync("project/open", new { projects }, cancellationToken);
            return true;
        }
        return false;
    }

    private async Task CleanupAsync()
    {
        _watcher?.Dispose(); _watcher = null;
        _watchedTimer?.Dispose(); _watchedTimer = null;
        lock (_watchedGate) _watchedChanges.Clear();
        var client = _client;
        var process = _process;
        _client = null;
        _process = null;
        if (process is not null)
            try { await process.StopAsync(client is null ? null : client.ShutdownAsync, TimeSpan.FromSeconds(2)); }
            catch (Exception exception) when (exception is StreamJsonRpc.ConnectionLostException or IOException
                or ObjectDisposedException or OperationCanceledException) { }
        if (client is not null) await client.DisposeAsync();
        if (process is not null) await process.DisposeAsync();
    }

    private void StartWatching()
    {
        if (!Directory.Exists(_workspace) || _watcher is not null) return;
        _watchedTimer = new(_ => _ = FlushWatchedChangesAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new(_workspace)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };
        _watcher.Created += (_, args) => QueueWatchedChange(args.FullPath, 1);
        _watcher.Changed += (_, args) => QueueWatchedChange(args.FullPath, 2);
        _watcher.Deleted += (_, args) => QueueWatchedChange(args.FullPath, 3);
        _watcher.Renamed += (_, args) => { QueueWatchedChange(args.OldFullPath, 3); QueueWatchedChange(args.FullPath, 1); };
        _watcher.EnableRaisingEvents = true;
    }

    private void QueueWatchedChange(string path, int type)
    {
        if (Ignored(path) || !WatchedExtension(path)) return;
        lock (_watchedGate)
        {
            _watchedChanges[Path.GetFullPath(path)] = type;
            _watchedTimer?.Change(100, Timeout.Infinite);
        }
    }

    private async Task FlushWatchedChangesAsync()
    {
        KeyValuePair<string, int>[] changes;
        lock (_watchedGate) { changes = _watchedChanges.ToArray(); _watchedChanges.Clear(); }
        var client = _client;
        if (changes.Length == 0 || client is null || !IsReady
            || _definition.Kind != LanguageServerKind.RoslynRazor && !client.IsRegistered("workspace/didChangeWatchedFiles")) return;
        try
        {
            await client.NotifyAsync("workspace/didChangeWatchedFiles", new
            {
                changes = changes.Select(item => new { uri = LspConverters.FileUri(item.Key).AbsoluteUri, type = item.Value }).ToArray()
            });
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException
            or StreamJsonRpc.ConnectionLostException) { }
    }

    private static bool WatchedExtension(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".cs" or ".razor" or ".cshtml" or ".html" or ".htm" or ".css" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx";
    private static bool Ignored(string path) => path.Split(Path.DirectorySeparatorChar)
        .Any(segment => segment is ".git" or "bin" or "obj" or ".vs");

    private void SetStatus(LanguageServerState state, string? name = null, string? version = null, string? detail = null)
    {
        Status = new(state, name, version, detail);
        StatusChanged?.Invoke(Status);
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        FileNotFoundException => "The configured language-server executable was not found.",
        UnauthorizedAccessException => "The configured language-server executable could not be started.",
        OperationCanceledException => "Language-server initialization timed out.",
        _ => exception.GetType().Name
    };

    private string CrashDetail(LanguageServerProcess process)
    {
        var stderr = process.Stderr.Replace('\r', ' ').Replace('\n', ' ').Trim();
        stderr = stderr.Replace(_workspace, "<workspace>", StringComparisonForPaths());
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) stderr = stderr.Replace(profile, "<user>", StringComparisonForPaths());
        stderr = System.Text.RegularExpressions.Regex.Replace(stderr,
            @"(?i)(password|passwd|token|secret|api[-_]?key)\s*[:=]\s*\S+", "$1=<redacted>");
        if (stderr.Length > 4000) stderr = stderr[^4000..];
        return string.IsNullOrWhiteSpace(stderr) ? $"Exit code {process.ExitCode}." : $"Exit code {process.ExitCode}: {stderr}";
    }

    private static StringComparison StringComparisonForPaths() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try { await CleanupAsync(); SetStatus(LanguageServerState.Stopped); }
        finally { _lifecycle.Release(); _lifecycle.Dispose(); }
    }
}
