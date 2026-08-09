using System.Text.Json;

namespace NovaSharp.LanguageServers;

internal sealed class LspLanguageProvider : ILanguageProvider, IExtendedLanguageProvider
{
    private sealed record CachedCompletion(long Version, JsonElement Item, TextRange Replacement);
    private readonly Func<string, LanguageServerManager?> _server;
    private readonly Func<string, EditorSnapshot?> _snapshot;
    private readonly Dictionary<string, CachedCompletion> _completions = [];
    private long _nextCompletion;

    internal LspLanguageProvider(Func<string, LanguageServerManager?> server,
        Func<string, EditorSnapshot?> snapshot, LanguageDiagnosticStore? diagnostics = null)
    {
        _server = server;
        _snapshot = snapshot;
        Diagnostics = diagnostics ?? new();
    }

    internal LanguageDiagnosticStore Diagnostics { get; }

    public LanguageProviderInfo GetInfo(string path)
    {
        var server = _server(path);
        var language = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => ("csharp", "C#"), ".razor" or ".cshtml" => ("razor", "Razor"),
            ".html" or ".htm" => ("html", "HTML"), ".css" => ("css", "CSS"), _ => ("text", "Text")
        };
        if (language.Item1 == "text") return new(language.Item1, language.Item2, LanguageCapabilities.None, false,
            "No language server is registered for this document.");
        if (server is null || !server.IsReady) return new(language.Item1, language.Item2, LanguageCapabilities.None,
            false, server?.Status.Detail ?? $"{language.Item2} language server is {server?.Status.State.ToString().ToLowerInvariant() ?? "unavailable"}.");
        return new(language.Item1, language.Item2, Capabilities(server), true);
    }

    public void ClearDiagnostics(string path) => Diagnostics.Remove(path);

    public async Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request,
        bool explicitInvocation, CancellationToken cancellationToken)
    {
        var (server, snapshot, position) = Context(request);
        if (server is null || snapshot is null) return Missing<CompletionResult>(request);
        var response = await server.RequestAsync<JsonElement>("textDocument/completion", new
        {
            textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri }, position,
            context = new { triggerKind = explicitInvocation ? 1 : 2 }
        }, cancellationToken);
        var items = response.ValueKind == JsonValueKind.Array ? response : response.TryGetProperty("items", out var list) ? list : default;
        var results = new List<CompletionEntry>();
        lock (_completions) _completions.Clear();
        if (items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray().Take(200))
            {
                var id = Interlocked.Increment(ref _nextCompletion).ToString();
                var label = String(item, "label") ?? string.Empty;
                var replacement = CompletionRange(item, snapshot.Value.Text, request.Position);
                var cached = new CachedCompletion(request.Version, item.Clone(), replacement);
                lock (_completions) _completions[id] = cached;
                results.Add(CompletionEntry(id, item, label, replacement));
            }
        return new(request.Version, new(results));
    }

    public async Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string itemId,
        CancellationToken cancellationToken)
    {
        CachedCompletion cached;
        lock (_completions) if (!_completions.TryGetValue(itemId, out cached!) || cached.Version != request.Version)
            return Missing<CompletionEntry>(request);
        var server = Ready(request.DocumentId);
        if (server is null) return Missing<CompletionEntry>(request);
        var item = Supports(server, "completionProvider", "resolveProvider")
            ? await server.RequestAsync<JsonElement>("completionItem/resolve", cached.Item, cancellationToken) : cached.Item;
        return new(request.Version, CompletionEntry(itemId, item, String(item, "label") ?? string.Empty, cached.Replacement));
    }

    public Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string itemId,
        char? commitCharacter, CancellationToken cancellationToken)
    {
        CachedCompletion cached;
        lock (_completions) if (!_completions.TryGetValue(itemId, out cached!) || cached.Version != request.Version)
            return Task.FromResult(Missing<CompletionEdit>(request));
        var edit = cached.Item.TryGetProperty("textEdit", out var textEdit) ? textEdit : default;
        var text = String(edit, "newText") ?? String(cached.Item, "insertText") ?? String(cached.Item, "label") ?? string.Empty;
        return Task.FromResult(new LanguageResponse<CompletionEdit>(request.Version, new(cached.Replacement, text, null)));
    }

    public async Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var result = await PositionRequest(request, "textDocument/signatureHelp", cancellationToken);
        if (result is not { } value || !value.TryGetProperty("signatures", out var signatures)) return new(request.Version, null);
        return new(request.Version, new(signatures.EnumerateArray().Select(item => String(item, "label") ?? string.Empty).ToArray(),
            Int(value, "activeSignature"), Int(value, "activeParameter")));
    }

    public async Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var value = await PositionRequest(request, "textDocument/hover", cancellationToken);
        if (value is null) return new(request.Version, null);
        var sections = value.Value.TryGetProperty("contents", out var contents) ? Markup(contents) : [];
        var range = value.Value.TryGetProperty("range", out var r) && _snapshot(request.DocumentId) is { } snapshot
            ? LspConverters.ToRange(snapshot.Text, r.Deserialize<LspRange>()!) : new TextRange(request.Position, 0);
        return new(request.Version, new(range, sections));
    }

    public async Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return Missing<IReadOnlyList<SemanticSpan>>(request);
        var value = await server.RequestAsync<JsonElement>("textDocument/semanticTokens/full",
            new { textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri } }, cancellationToken);
        if (!value.TryGetProperty("data", out var data)) return new(request.Version, []);
        var legend = server.Capabilities.TryGetProperty("semanticTokensProvider", out var semanticProvider)
            && semanticProvider.TryGetProperty("legend", out var semanticLegend)
            && semanticLegend.TryGetProperty("tokenTypes", out var tokenTypes)
            ? tokenTypes.EnumerateArray().Select(item => item.GetString() ?? "text").ToArray() : [];
        var numbers = data.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var spans = new List<SemanticSpan>(); var line = 0; var character = 0;
        for (var i = 0; i + 4 < numbers.Length; i += 5)
        {
            line += numbers[i]; character = numbers[i] == 0 ? character + numbers[i + 1] : numbers[i + 1];
            var start = LspConverters.ToOffset(snapshot.Value.Text, new(line, character));
            spans.Add(new(start, numbers[i + 2], numbers[i + 3] < legend.Length ? legend[numbers[i + 3]] : "text"));
        }
        return new(request.Version, spans);
    }

    public Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new LanguageResponse<IReadOnlyList<LanguageDiagnostic>>(
            request.Version, Diagnostics.Entries.Where(item => PathEquals(item.DocumentPath, request.DocumentId)).ToArray()));

    public async Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return Missing<FormatResult>(request);
        var method = request.Range is null ? "textDocument/formatting" : "textDocument/rangeFormatting";
        object parameters = request.Range is { } range
            ? new { textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri },
                range = ToRange(snapshot.Value.Text, range), options = new { tabSize = 4, insertSpaces = true } }
            : new { textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri },
                options = new { tabSize = 4, insertSpaces = true } };
        var edits = await server.RequestAsync<JsonElement>(method, parameters, cancellationToken);
        var text = ApplyEdits(snapshot.Value.Text, edits);
        var selection = request.Range ?? new(0, 0);
        return new(request.Version, new(text, selection.Start, selection.Length));
    }

    public Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request, bool typeDefinition,
        CancellationToken cancellationToken) => Locations(request, typeDefinition ? "textDocument/typeDefinition" : "textDocument/definition",
            typeDefinition ? NavigationKind.TypeDefinition : NavigationKind.Definition, cancellationToken);
    public Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request, CancellationToken cancellationToken) =>
        Locations(request, "textDocument/implementation", NavigationKind.Implementation, cancellationToken);
    public Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request, CancellationToken cancellationToken) =>
        Locations(request, "textDocument/references", NavigationKind.Reference, cancellationToken, new { includeDeclaration = true });

    public async Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return [];
        var result = await server.RequestAsync<JsonElement>("textDocument/documentSymbol",
            new { textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri } }, cancellationToken);
        return Symbols(result, request.DocumentId, snapshot.Value.Text).ToArray();
    }

    public async Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query, CancellationToken cancellationToken)
    {
        var results = new List<SymbolEntry>();
        foreach (var server in Servers().Where(item => item.IsReady))
        {
            var value = await server.RequestAsync<JsonElement>("workspace/symbol", new { query }, cancellationToken);
            if (value.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in value.EnumerateArray())
            {
                var location = item.GetProperty("location"); if (!TryLocation(location, out var path, out var range)) continue;
                var text = _snapshot(path)?.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "");
                var converted = LspConverters.ToRange(text, range);
                results.Add(new(String(item, "name") ?? "symbol", Kind(Int(item, "kind")), path, converted,
                    range.Start.Line, range.Start.Character, "", String(item, "containerName")));
            }
        }
        return results;
    }

    public async Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string newName, CancellationToken cancellationToken)
    {
        var value = await PositionRequest(request, "textDocument/rename", cancellationToken, new { newName });
        return value is null ? null : await WorkspaceEditAsync($"Rename to {newName}", value.Value, cancellationToken);
    }

    public async Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return [];
        var range = request.Range ?? new TextRange(request.Position, 0);
        var value = await server.RequestAsync<JsonElement>("textDocument/codeAction", new
        {
            textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri }, range = ToRange(snapshot.Value.Text, range),
            context = new { diagnostics = Array.Empty<object>() }
        }, cancellationToken);
        var actions = new List<CodeActionEntry>();
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                if (item.TryGetProperty("edit", out var edit) && await WorkspaceEditAsync(String(item, "title") ?? "Code action", edit, cancellationToken) is { } converted)
                    actions.Add(new(String(item, "title") ?? "Code action", String(item, "kind") ?? "", converted,
                        item.TryGetProperty("isPreferred", out var preferred) && preferred.ValueKind == JsonValueKind.True));
        return actions;
    }

    private async Task<JsonElement?> PositionRequest(LanguageRequest request, string method, CancellationToken cancellationToken,
        object? extra = null)
    {
        var (server, _, position) = Context(request); if (server is null) return null;
        var parameters = extra is null
            ? new Dictionary<string, object?> { ["textDocument"] = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri }, ["position"] = position }
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(extra))!;
        parameters["textDocument"] = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri }; parameters["position"] = position;
        var value = await server.RequestAsync<JsonElement>(method, parameters, cancellationToken);
        return value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : value;
    }

    private async Task<IReadOnlyList<NavigationTarget>> Locations(LanguageRequest request, string method, NavigationKind kind,
        CancellationToken cancellationToken, object? context = null)
    {
        var value = await PositionRequest(request, method, cancellationToken, context is null ? null : new { context });
        if (value is null) return [];
        var items = value.Value.ValueKind == JsonValueKind.Array ? value.Value.EnumerateArray().ToArray() : new[] { value.Value };
        var results = new List<NavigationTarget>();
        foreach (var item in items)
        {
            var location = item.TryGetProperty("targetUri", out _) ? item : item;
            if (!TryLocation(location, out var path, out var range)) continue;
            var text = _snapshot(path)?.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : "");
            var converted = LspConverters.ToRange(text, range);
            results.Add(new(path, converted, range.Start.Line, range.Start.Character, Path.GetFileName(path), "", kind));
        }
        return results;
    }

    private async Task<WorkspaceEdit?> WorkspaceEditAsync(string title, JsonElement edit, CancellationToken token)
    {
        var documents = new List<WorkspaceDocumentEdit>();
        if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
            foreach (var property in changes.EnumerateObject())
                await AddDocumentAsync(property.Name, null, property.Value);
        if (edit.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in documentChanges.EnumerateArray())
                if (change.TryGetProperty("textDocument", out var document) && change.TryGetProperty("edits", out var edits))
                    await AddDocumentAsync(String(document, "uri") ?? "", document.TryGetProperty("version", out var version)
                        && version.TryGetInt64(out var expectedVersion) ? expectedVersion : null, edits);
        return documents.Count == 0 ? null : new(title, documents);

        async Task AddDocumentAsync(string uriText, long? expectedVersion, JsonElement edits)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) return;
            var path = Path.GetFullPath(uri.LocalPath); var snapshot = _snapshot(path);
            var oldText = snapshot?.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, token) : "");
            documents.Add(new(path, expectedVersion ?? snapshot?.Version, oldText, ApplyEdits(oldText, edits)));
        }
    }

    private (LanguageServerManager? Server, EditorSnapshot? Snapshot, LspPosition Position) Context(LanguageRequest request)
    {
        var snapshot = _snapshot(request.DocumentId);
        return (Ready(request.DocumentId), snapshot, LspConverters.ToPosition(snapshot?.Text ?? "", request.Position));
    }
    private LanguageServerManager? Ready(string path) => _server(path) is { IsReady: true } server ? server : null;
    private IEnumerable<LanguageServerManager> Servers() => new[] { ".cs", ".html", ".css" }.Select(_server).OfType<LanguageServerManager>().Distinct();
    private static LanguageResponse<T> Missing<T>(LanguageRequest request) => new(request.Version, default, true);
    private static bool PathEquals(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static string? String(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
        && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static int Int(JsonElement value, string name) => value.TryGetProperty(name, out var item) && item.TryGetInt32(out var result) ? result : 0;
    private static bool Supports(LanguageServerManager server, string provider, string property) => server.Capabilities.TryGetProperty(provider, out var value)
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var enabled) && enabled.ValueKind == JsonValueKind.True;
    private static LanguageCapabilities Capabilities(LanguageServerManager server)
    {
        var value = server.Capabilities;
        LanguageCapabilities result = LanguageCapabilities.None;
        Add("completionProvider", LanguageCapabilities.Completion); Add("hoverProvider", LanguageCapabilities.Hover);
        Add("signatureHelpProvider", LanguageCapabilities.SignatureHelp); Add("semanticTokensProvider", LanguageCapabilities.SemanticTokens);
        Add("diagnosticProvider", LanguageCapabilities.Diagnostics); Add("documentFormattingProvider", LanguageCapabilities.Formatting);
        Add("documentSymbolProvider", LanguageCapabilities.Symbols); Add("definitionProvider", LanguageCapabilities.Navigation);
        Add("renameProvider", LanguageCapabilities.Rename); Add("codeActionProvider", LanguageCapabilities.CodeActions);
        if (value.TryGetProperty("textDocumentSync", out _)) result |= LanguageCapabilities.Diagnostics;
        Dynamic("textDocument/completion", LanguageCapabilities.Completion); Dynamic("textDocument/hover", LanguageCapabilities.Hover);
        Dynamic("textDocument/signatureHelp", LanguageCapabilities.SignatureHelp); Dynamic("textDocument/semanticTokens", LanguageCapabilities.SemanticTokens);
        Dynamic("textDocument/formatting", LanguageCapabilities.Formatting); Dynamic("textDocument/documentSymbol", LanguageCapabilities.Symbols);
        Dynamic("textDocument/definition", LanguageCapabilities.Navigation); Dynamic("textDocument/rename", LanguageCapabilities.Rename);
        Dynamic("textDocument/codeAction", LanguageCapabilities.CodeActions);
        return result;
        void Add(string name, LanguageCapabilities capability) { if (value.TryGetProperty(name, out var item) && item.ValueKind is not JsonValueKind.False and not JsonValueKind.Null) result |= capability; }
        void Dynamic(string method, LanguageCapabilities capability) { if (server.IsMethodRegistered(method)) result |= capability; }
    }
    private static CompletionEntry CompletionEntry(string id, JsonElement item, string label, TextRange range) => new(id, label,
        String(item, "filterText") ?? label, String(item, "sortText") ?? label, Kind(Int(item, "kind")), range,
        item.TryGetProperty("commitCharacters", out var chars) ? chars.EnumerateArray().SelectMany(value => value.GetString() ?? "").ToArray() : [],
        Int(item, "insertTextFormat") == 2, Markup(item.TryGetProperty("documentation", out var docs) ? docs : default).FirstOrDefault() ?? String(item, "detail"));
    private static TextRange CompletionRange(JsonElement item, string text, int position) => item.TryGetProperty("textEdit", out var edit)
        && edit.TryGetProperty("range", out var range) ? LspConverters.ToRange(text, range.Deserialize<LspRange>()!) : new(position, 0);
    private static IReadOnlyList<string> Markup(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [value.GetString() ?? ""], JsonValueKind.Array => value.EnumerateArray().SelectMany(Markup).ToArray(),
        JsonValueKind.Object when value.TryGetProperty("value", out var item) => [item.GetString() ?? ""], _ => []
    };
    private static object ToRange(string text, TextRange range) => new { start = LspConverters.ToPosition(text, range.Start), end = LspConverters.ToPosition(text, range.Start + range.Length) };
    private static string ApplyEdits(string text, JsonElement edits)
    {
        if (edits.ValueKind != JsonValueKind.Array) return text;
        var converted = edits.EnumerateArray().Select(item => (Range: LspConverters.ToRange(text, item.GetProperty("range").Deserialize<LspRange>()!), Text: String(item, "newText") ?? ""))
            .OrderByDescending(item => item.Range.Start).ToArray();
        for (var index = 1; index < converted.Length; index++)
            if (converted[index].Range.Start + converted[index].Range.Length > converted[index - 1].Range.Start)
                throw new InvalidDataException("The language server returned overlapping text edits.");
        foreach (var edit in converted) text = text.Remove(edit.Range.Start, edit.Range.Length).Insert(edit.Range.Start, edit.Text);
        return text;
    }
    private static bool TryLocation(JsonElement item, out string path, out LspRange range)
    {
        var uriName = item.TryGetProperty("targetUri", out var target) ? target : item.TryGetProperty("uri", out var uri) ? uri : default;
        var rangeName = item.TryGetProperty("targetSelectionRange", out var selection) ? selection : item.TryGetProperty("range", out var direct) ? direct : default;
        if (uriName.ValueKind != JsonValueKind.String || !Uri.TryCreate(uriName.GetString(), UriKind.Absolute, out var parsed) || !parsed.IsFile || rangeName.ValueKind != JsonValueKind.Object)
        { path = ""; range = null!; return false; }
        path = Path.GetFullPath(parsed.LocalPath); range = rangeName.Deserialize<LspRange>()!; return true;
    }
    private static IEnumerable<SymbolEntry> Symbols(JsonElement values, string path, string text)
    {
        if (values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in values.EnumerateArray())
        {
            var range = item.TryGetProperty("selectionRange", out var selection) ? selection : item.GetProperty("range");
            var lsp = range.Deserialize<LspRange>()!; var converted = LspConverters.ToRange(text, lsp);
            yield return new(String(item, "name") ?? "symbol", Kind(Int(item, "kind")), path, converted, lsp.Start.Line, lsp.Start.Character, "", String(item, "detail"));
            if (item.TryGetProperty("children", out var children)) foreach (var child in Symbols(children, path, text)) yield return child;
        }
    }
    private static string Kind(int kind) => kind switch { 2 => "Module", 5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field", 10 => "Enum", 12 => "Function", 13 => "Variable", 14 => "Constant", 23 => "Struct", 11 => "Interface", _ => "Text" };
}
