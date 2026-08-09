using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class LanguageServerManager : ILspDocumentSink, IAsyncDisposable
{
    private readonly LanguageServerDefinition _definition;
    private readonly string _workspace;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Queue<DateTime> _crashes = new();
    private LanguageServerProcess? _process;
    private LspClient? _client;

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
    internal event Action? Ready;
    internal event Action? CapabilitiesChanged;
    internal LanguageServerStatus Status { get; private set; }
    internal JsonElement Capabilities { get; private set; }
    public bool IsReady => Status.State == LanguageServerState.Ready && _client is not null;
    internal bool IsMethodRegistered(string method) => _client?.IsRegistered(method) == true;

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
                _client = new(_process.Output, _process.Input);
                _client.DiagnosticsPublished += OnDiagnostics;
                _client.CapabilitiesChanged += () => CapabilitiesChanged?.Invoke();
                using var initializeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                initializeTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var capabilities = new
                {
                    workspace = new { workspaceFolders = true, configuration = true, applyEdit = true },
                    textDocument = new { synchronization = new { dynamicRegistration = true, willSave = false, didSave = true },
                        publishDiagnostics = new { relatedInformation = true, tagSupport = new { valueSet = new[] { 1, 2 } } } },
                    window = new { workDoneProgress = true }
                };
                SetStatus(LanguageServerState.LoadingWorkspace);
                var root = LspConverters.FileUri(_workspace);
                var result = await _client.InitializeAsync(new(Environment.ProcessId, root.AbsoluteUri, capabilities,
                    new("NovaSharp"), [new(root.AbsoluteUri, Path.GetFileName(_workspace))]), initializeTimeout.Token);
                Capabilities = result.Capabilities.Clone();
                SetStatus(LanguageServerState.Ready, result.ServerInfo?.Name, result.ServerInfo?.Version);
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

    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken = default) =>
        _client is null ? Task.CompletedTask : _client.NotifyAsync(method, parameters, cancellationToken);

    internal async Task<T?> RequestAsync<T>(string method, object parameters, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (!IsReady || client is null) return default;
        return await client.RequestAsync<T>(method, parameters, cancellationToken);
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
        var now = DateTime.UtcNow;
        _crashes.Enqueue(now);
        while (_crashes.TryPeek(out var crash) && now - crash > TimeSpan.FromSeconds(180)) _crashes.Dequeue();
        await _lifecycle.WaitAsync();
        try { await CleanupAsync(); }
        finally { _lifecycle.Release(); }
        if (_crashes.Count >= 5) { SetStatus(LanguageServerState.Unavailable, detail: "The server repeatedly crashed."); return; }
        SetStatus(LanguageServerState.Restarting);
        await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, _crashes.Count - 1)));
        await StartAsync();
    }

    private void OnDiagnostics(LspPublishDiagnosticsParams parameters) => DiagnosticsPublished?.Invoke(parameters);

    private async Task CleanupAsync()
    {
        var client = _client;
        var process = _process;
        _client = null;
        _process = null;
        if (process is not null)
            await process.StopAsync(client is null ? null : client.ShutdownAsync, TimeSpan.FromSeconds(2));
        if (client is not null) await client.DisposeAsync();
        if (process is not null) await process.DisposeAsync();
    }

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

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try { await CleanupAsync(); SetStatus(LanguageServerState.Stopped); }
        finally { _lifecycle.Release(); _lifecycle.Dispose(); }
    }
}
