using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class RazorHtmlBridge(
    Func<bool> isReady,
    Func<string, object, CancellationToken, Task> notify,
    Func<string, object, CancellationToken, Task<JsonElement>> request)
{
    private sealed record Document(string Checksum, string Text, long Version);
    private readonly Dictionary<string, Document> _documents = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async Task<object> UpdateAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (!TryString(parameters, "checksum", out var checksum)
            || !TryString(parameters, "text", out var text)
            || !TryUri(parameters, out var uri) || !isReady()) return new { };
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_documents.TryGetValue(uri, out var document))
            {
                var version = document.Version + 1;
                await notify("textDocument/didChange", new LspDidChangeTextDocumentParams(
                    new(uri, version), [new(null, null, text)]), cancellationToken);
                _documents[uri] = new(checksum, text, version);
            }
            else
            {
                await notify("textDocument/didOpen", new LspDidOpenTextDocumentParams(
                    new(uri, "html", 1, text)), cancellationToken);
                _documents[uri] = new(checksum, text, 1);
            }
        }
        finally { _gate.Release(); }
        return new { };
    }

    internal async Task CloseAsync(string uri, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_documents.Remove(uri) && isReady())
                await notify("textDocument/didClose", new LspDidCloseTextDocumentParams(new(uri)), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    internal async Task ReplayAsync(CancellationToken cancellationToken = default)
    {
        if (!isReady()) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            foreach (var (uri, document) in _documents.ToArray())
            {
                await notify("textDocument/didOpen", new LspDidOpenTextDocumentParams(
                    new(uri, "html", 1, document.Text)), cancellationToken);
                _documents[uri] = document with { Version = 1 };
            }
        }
        finally { _gate.Release(); }
    }

    internal async Task<JsonElement?> ForwardAsync(string method, JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!TryString(parameters, "checksum", out var checksum) || !TryUri(parameters, out var uri)
            || !parameters.TryGetProperty("request", out var forwarded) || !isReady()) return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_documents.TryGetValue(uri, out var document) || document.Checksum != checksum) return null;
            var result = await request(method, forwarded.Clone(), cancellationToken);
            return result.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : result;
        }
        finally { _gate.Release(); }
    }

    private static bool TryUri(JsonElement parameters, out string uri)
    {
        uri = string.Empty;
        return parameters.TryGetProperty("textDocument", out var document)
            && TryString(document, "uri", out uri);
    }

    private static bool TryString(JsonElement value, string property, out string result)
    {
        result = string.Empty;
        return value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String
            && (result = item.GetString() ?? string.Empty).Length > 0;
    }
}
