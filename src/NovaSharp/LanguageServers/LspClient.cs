using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class LspClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly LspClientTarget _target;
    private readonly Dictionary<string, LspRegistration> _registrations = [];
    private readonly RazorHtmlBridge? _razorHtml;
    private readonly Func<JsonElement, CancellationToken, Task<bool>>? _applyWorkspaceEdit;
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

    internal LspClient(Stream sendingStream, Stream receivingStream, RazorHtmlBridge? razorHtml = null,
        Func<JsonElement, CancellationToken, Task<bool>>? applyWorkspaceEdit = null)
    {
        _razorHtml = razorHtml;
        _applyWorkspaceEdit = applyWorkspaceEdit;
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

        [JsonRpcMethod("razor/updateHtml", UseSingleObjectParameterDeserialization = true)]
        public Task<object> UpdateHtml(JsonElement parameters, CancellationToken cancellationToken) =>
            owner._razorHtml?.UpdateAsync(parameters, cancellationToken) ?? Task.FromResult<object>(new { });

        [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlHover(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/hover", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlCompletion(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/completion", parameters, cancellationToken);

        [JsonRpcMethod("completionItem/resolve", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlCompletionResolve(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("completionItem/resolve", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/signatureHelp", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlSignatureHelp(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/signatureHelp", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/diagnostic", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlDiagnostics(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/diagnostic", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/formatting", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlFormatting(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/formatting", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/rangeFormatting", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlRangeFormatting(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/rangeFormatting", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/onTypeFormatting", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlOnTypeFormatting(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/onTypeFormatting", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/foldingRange", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlFoldingRange(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/foldingRange", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/documentHighlight", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlDocumentHighlight(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/documentHighlight", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/documentColor", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlDocumentColor(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/documentColor", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/colorPresentation", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlColorPresentation(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/colorPresentation", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlDefinition(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/definition", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/implementation", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlImplementation(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/implementation", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/prepareRename", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlPrepareRename(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/prepareRename", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/rename", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlRename(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/rename", parameters, cancellationToken);

        [JsonRpcMethod("textDocument/codeAction", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlCodeAction(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("textDocument/codeAction", parameters, cancellationToken);

        [JsonRpcMethod("codeAction/resolve", UseSingleObjectParameterDeserialization = true)]
        public Task<JsonElement?> ForwardHtmlCodeActionResolve(JsonElement parameters, CancellationToken cancellationToken) =>
            Forward("codeAction/resolve", parameters, cancellationToken);

        private Task<JsonElement?> Forward(string method, JsonElement parameters, CancellationToken cancellationToken) =>
            owner._razorHtml?.ForwardAsync(method, parameters, cancellationToken) ?? Task.FromResult<JsonElement?>(null);

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

        [JsonRpcMethod("workspace/applyEdit", UseSingleObjectParameterDeserialization = true)]
        public async Task<object> ApplyWorkspaceEdit(JsonElement parameters, CancellationToken cancellationToken)
        {
            if (!parameters.TryGetProperty("edit", out var edit) || owner._applyWorkspaceEdit is null)
                return new { applied = false, failureReason = "Workspace edits are unavailable." };
            var applied = await owner._applyWorkspaceEdit(edit.Clone(), cancellationToken);
            return applied ? new { applied = true, failureReason = (string?)null }
                : new { applied = false, failureReason = (string?)"The edit requires user confirmation." };
        }

        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object CreateProgress(JsonElement parameters) => new { };
    }
}
