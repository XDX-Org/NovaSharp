using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class LspClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly LspClientTarget _target;
    private readonly Dictionary<string, LspRegistration> _registrations = [];
    private bool _initialized;

    internal event Action<LspPublishDiagnosticsParams>? DiagnosticsPublished;
    internal event Action? DiagnosticRefreshRequested;
    internal event Action<LspLogMessageParams>? MessageLogged;
    internal event Action? CapabilitiesChanged;
    internal bool IsRegistered(string method) { lock (_registrations) return _registrations.Values.Any(item => item.Method == method); }
    internal IReadOnlyList<LspRegistration> Registrations(string method)
    {
        lock (_registrations) return _registrations.Values.Where(item => item.Method == method).ToArray();
    }

    internal LspClient(Stream sendingStream, Stream receivingStream)
    {
        _target = new(this);
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        var handler = new HeaderDelimitedMessageHandler(sendingStream, receivingStream, formatter);
        _rpc = new JsonRpc(handler, _target);
        _rpc.StartListening();
    }

    internal async Task<LspInitializeResult> InitializeAsync(LspInitializeParams parameters,
        CancellationToken cancellationToken)
    {
        var result = await RequestAsync<LspInitializeResult>("initialize", parameters, cancellationToken);
        await NotifyAsync("initialized", new { }, cancellationToken);
        _initialized = true;
        return result;
    }

    internal Task<T> RequestAsync<T>(string method, object? parameters, CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<T>(method, parameters, cancellationToken);

    internal Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _rpc.NotifyWithParameterObjectAsync(method, parameters);
    }

    internal async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) return;
        _initialized = false;
        await RequestAsync<object?>("shutdown", null, cancellationToken);
        await NotifyAsync("exit", null, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class LspClientTarget(LspClient owner)
    {
        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(LspPublishDiagnosticsParams parameters) => owner.DiagnosticsPublished?.Invoke(parameters);

        [JsonRpcMethod("workspace/diagnostic/refresh")]
        public object RefreshDiagnostics()
        {
            owner.DiagnosticRefreshRequested?.Invoke();
            return new { };
        }

        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(LspLogMessageParams parameters) => owner.MessageLogged?.Invoke(parameters);

        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void ShowMessage(LspLogMessageParams parameters) => owner.MessageLogged?.Invoke(parameters);

        [JsonRpcMethod("client/registerCapability", UseSingleObjectParameterDeserialization = true)]
        public object RegisterCapability(LspRegistrationParams parameters)
        {
            lock (owner._registrations)
                foreach (var registration in parameters.Registrations) owner._registrations[registration.Id] = registration;
            owner.CapabilitiesChanged?.Invoke();
            return new { };
        }

        [JsonRpcMethod("client/unregisterCapability", UseSingleObjectParameterDeserialization = true)]
        public object UnregisterCapability(LspUnregistrationParams parameters)
        {
            lock (owner._registrations)
                foreach (var registration in parameters.Unregistrations) owner._registrations.Remove(registration.Id);
            owner.CapabilitiesChanged?.Invoke();
            return new { };
        }

        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public IReadOnlyList<object?> Configuration(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            return items.EnumerateArray().Select(item => item.TryGetProperty("section", out var section)
                && section.GetString() is { } name && (name.EndsWith(".dotnet_compiler_diagnostics_scope", StringComparison.Ordinal)
                    || name.EndsWith(".dotnet_analyzer_diagnostics_scope", StringComparison.Ordinal))
                    ? (object?)"OpenFiles" : null).ToArray();
        }

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object CreateProgress(JsonElement parameters) => new { };
    }
}
