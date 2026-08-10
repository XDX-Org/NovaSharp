using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovaSharp.LanguageServers;

internal sealed class LspLanguageProvider : ILanguageProvider, IExtendedLanguageProvider
{
    private static readonly JsonSerializerOptions ProtocolJson = new(JsonSerializerDefaults.Web);
    private static readonly Regex EmbeddedDataImage = new(@"!\[[^\]]*\]\(data:[^)]+\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private sealed record CachedCompletion(long Version, JsonElement Item, TextRange Replacement);
    private readonly Func<string, LanguageServerManager?> _server;
    private readonly Func<string, EditorSnapshot?> _snapshot;
    private readonly Func<string, CancellationToken, Task> _synchronize;
    private readonly Dictionary<string, CachedCompletion> _completions = [];
    private readonly Dictionary<(object Server, string Uri, string Identifier),
        (string? ResultId, IReadOnlyList<LspDiagnostic> Items)> _diagnosticReports = [];
    private long _nextCompletion;

    internal LspLanguageProvider(Func<string, LanguageServerManager?> server,
        Func<string, EditorSnapshot?> snapshot, LanguageDiagnosticStore? diagnostics = null,
        Func<string, CancellationToken, Task>? synchronize = null)
    {
        _server = server;
        _snapshot = snapshot;
        _synchronize = synchronize ?? ((_, _) => Task.CompletedTask);
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
        await _synchronize(request.DocumentId, cancellationToken);
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
            || server.Registrations("textDocument/completion").Any(registration => registration.RegisterOptions is { } options
                && options.TryGetProperty("resolveProvider", out var resolve) && resolve.ValueKind == JsonValueKind.True)
            ? await server.RequestAsync<JsonElement>("completionItem/resolve", cached.Item, cancellationToken) : cached.Item;
        lock (_completions) _completions[itemId] = cached with { Item = item.Clone() };
        return new(request.Version, CompletionEntry(itemId, item, String(item, "label") ?? string.Empty, cached.Replacement));
    }

    public Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string itemId,
        char? commitCharacter, CancellationToken cancellationToken)
    {
        CachedCompletion cached;
        lock (_completions) if (!_completions.TryGetValue(itemId, out cached!) || cached.Version != request.Version)
            return Task.FromResult(Missing<CompletionEdit>(request));
        if (_snapshot(request.DocumentId) is not { } snapshot || snapshot.Version != request.Version)
            return Task.FromResult(Missing<CompletionEdit>(request));
        var edit = cached.Item.TryGetProperty("textEdit", out var textEdit) ? textEdit : default;
        var text = String(edit, "newText") ?? String(cached.Item, "insertText") ?? String(cached.Item, "label") ?? string.Empty;
        var (expanded, relativeCaret) = Int(cached.Item, "insertTextFormat") == 2 ? ExpandSnippet(text) : (text, (int?)null);
        var command = CompletionCommand(cached.Item);
        var replacement = cached.Replacement;
        var caret = relativeCaret is null ? null : replacement.Start + relativeCaret;
        if (cached.Item.TryGetProperty("additionalTextEdits", out var additional)
            && additional.ValueKind == JsonValueKind.Array && additional.GetArrayLength() > 0)
        {
            var edits = additional.EnumerateArray().Select(item => (Range: LspConverters.ToRange(snapshot.Text,
                    ParseRange(item.GetProperty("range"))), Text: String(item, "newText") ?? string.Empty))
                .Append((Range: replacement, Text: expanded)).OrderByDescending(item => item.Range.Start).ToArray();
            for (var index = 1; index < edits.Length; index++)
                if (edits[index].Range.Start + edits[index].Range.Length > edits[index - 1].Range.Start)
                    return Task.FromResult(Missing<CompletionEdit>(request));
            var finalText = snapshot.Text;
            foreach (var item in edits)
            {
                finalText = finalText.Remove(item.Range.Start, item.Range.Length).Insert(item.Range.Start, item.Text);
                if (caret is { } position && item.Range.Start < replacement.Start)
                    caret = position + item.Text.Length - item.Range.Length;
            }
            return Task.FromResult(new LanguageResponse<CompletionEdit>(request.Version,
                new(new(0, snapshot.Text.Length), finalText, caret, command)));
        }
        return Task.FromResult(new LanguageResponse<CompletionEdit>(request.Version,
            new(replacement, expanded, caret, command)));
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
        var sections = value.Value.TryGetProperty("contents", out var contents) ? HoverSections(contents) : [];
        if (sections.Count == 0) return new(request.Version, null);
        var range = value.Value.TryGetProperty("range", out var r) && _snapshot(request.DocumentId) is { } snapshot
            ? LspConverters.ToRange(snapshot.Text, ParseRange(r)) : new TextRange(request.Position, 0);
        return new(request.Version, new(range, sections));
    }

    public async Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return Missing<IReadOnlyList<SemanticSpan>>(request);
        await _synchronize(request.DocumentId, cancellationToken);
        var value = await server.RequestAsync<JsonElement>("textDocument/semanticTokens/full",
            new { textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri } }, cancellationToken);
        if (!value.TryGetProperty("data", out var data)) return new(request.Version, []);
        var semanticProvider = server.Capabilities.TryGetProperty("semanticTokensProvider", out var configuredProvider)
            ? configuredProvider
            : server.Registrations("textDocument/semanticTokens")
                .Select(registration => registration.RegisterOptions)
                .FirstOrDefault(options => options is { ValueKind: JsonValueKind.Object } value
                    && value.TryGetProperty("legend", out _)) ?? default;
        var legend = semanticProvider.ValueKind == JsonValueKind.Object
            && semanticProvider.TryGetProperty("legend", out var semanticLegend)
            && semanticLegend.TryGetProperty("tokenTypes", out var tokenTypes)
            ? tokenTypes.EnumerateArray().Select(item => item.GetString() ?? "text").ToArray() : [];
        var modifiers = semanticProvider.ValueKind == JsonValueKind.Object
            && semanticProvider.TryGetProperty("legend", out semanticLegend)
            && semanticLegend.TryGetProperty("tokenModifiers", out var tokenModifiers)
            ? tokenModifiers.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [];
        var numbers = data.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var spans = new List<SemanticSpan>(); var line = 0; var character = 0;
        for (var i = 0; i + 4 < numbers.Length; i += 5)
        {
            line += numbers[i]; character = numbers[i] == 0 ? character + numbers[i + 1] : numbers[i + 1];
            var start = LspConverters.ToOffset(snapshot.Value.Text, new(line, character));
            var classification = numbers[i + 3] < legend.Length ? legend[numbers[i + 3]] : "text";
            for (var modifier = 0; modifier < modifiers.Length; modifier++)
                if ((numbers[i + 4] & 1 << modifier) != 0) classification += $" {modifiers[modifier]}";
            spans.Add(new(start, numbers[i + 2], classification));
        }
        return new(request.Version, spans);
    }

    public async Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId);
        if (server is null) return Missing<IReadOnlyList<LanguageDiagnostic>>(request);
        await _synchronize(request.DocumentId, cancellationToken);
        if (_snapshot(request.DocumentId) is not { } requestedSnapshot
            || requestedSnapshot.Version != request.Version)
            return Missing<IReadOnlyList<LanguageDiagnostic>>(request);
        if (SupportsDiagnostics(server) || server.Kind == LanguageServerKind.RoslynRazor)
        {
            var uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri;
            var registrations = server.Registrations("textDocument/diagnostic");
            var identifiers = registrations.Select(registration => registration.RegisterOptions is { } options
                    && options.TryGetProperty("identifier", out var identifier) ? identifier.GetString() : null)
                .Distinct().ToArray();
            if (identifiers.Length == 0) identifiers = [null];
            var diagnostics = new List<LspDiagnostic>();
            foreach (var identifier in identifiers)
            {
                var parameters = new Dictionary<string, object?> { ["textDocument"] = new { uri } };
                if (identifier is not null) parameters["identifier"] = identifier;
                var key = (server, uri, identifier ?? string.Empty);
                lock (_diagnosticReports)
                    if (_diagnosticReports.TryGetValue(key, out var previous) && previous.ResultId is not null)
                        parameters["previousResultId"] = previous.ResultId;
                var result = await server.RequestAsync<JsonElement>("textDocument/diagnostic", parameters, cancellationToken);
                diagnostics.AddRange(ApplyDiagnosticReport(key, result));
            }
            if (_snapshot(request.DocumentId)?.Version != request.Version)
                return Missing<IReadOnlyList<LanguageDiagnostic>>(request);
            new LspDiagnosticPublisher(server.Kind.ToString(), Diagnostics, _snapshot)
                .Publish(new(uri, diagnostics.DistinctBy(item => (item.Code?.GetRawText(), item.Message,
                    item.Range.Start.Line, item.Range.Start.Character)).ToArray()), request.Version);
        }
        return new(request.Version, Diagnostics.Entries.Where(item => PathEquals(item.DocumentPath, request.DocumentId)).ToArray());
    }

    internal IReadOnlyList<LspDiagnostic> ApplyDiagnosticReport(
        (object Server, string Uri, string Identifier) key, JsonElement result)
    {
        lock (_diagnosticReports)
        {
            if (result.ValueKind != JsonValueKind.Object) return [];
            var kind = result.TryGetProperty("kind", out var reportKind) ? reportKind.GetString() : "full";
            if (kind == "unchanged")
                return _diagnosticReports.TryGetValue(key, out var retained) ? retained.Items : [];
            if (!result.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];
            var diagnostics = items.EnumerateArray().Select(item => item.Deserialize<LspDiagnostic>(ProtocolJson)!)
                .Where(item => item is not null).ToArray();
            var resultId = result.TryGetProperty("resultId", out var id) ? id.GetString() : null;
            _diagnosticReports[key] = (resultId, diagnostics);
            return diagnostics;
        }
    }

    public async Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var server = Ready(request.DocumentId); var snapshot = _snapshot(request.DocumentId);
        if (server is null || snapshot is null) return Missing<FormatResult>(request);
        await _synchronize(request.DocumentId, cancellationToken);
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
        await _synchronize(request.DocumentId, cancellationToken);
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
        await _synchronize(request.DocumentId, cancellationToken);
        var range = request.Range ?? new TextRange(request.Position, 0);
        var value = await server.RequestAsync<JsonElement>("textDocument/codeAction", new
        {
            textDocument = new { uri = LspConverters.FileUri(request.DocumentId).AbsoluteUri }, range = ToRange(snapshot.Value.Text, range),
            context = new { diagnostics = Array.Empty<object>() }
        }, cancellationToken);
        var actions = new List<CodeActionEntry>();
        if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
            {
                if (item.TryGetProperty("disabled", out _)) continue;
                var resolved = item;
                if (!resolved.TryGetProperty("edit", out _) && SupportsCodeActionResolve(server))
                    resolved = await server.RequestAsync<JsonElement>("codeAction/resolve", item, cancellationToken);
                if (resolved.TryGetProperty("edit", out var edit)
                    && await WorkspaceEditAsync(String(resolved, "title") ?? "Code action", edit, cancellationToken) is { } converted)
                    actions.Add(new(String(resolved, "title") ?? "Code action", String(resolved, "kind") ?? "", converted,
                        resolved.TryGetProperty("isPreferred", out var preferred) && preferred.ValueKind == JsonValueKind.True,
                        ActionCommand(resolved)));
                else if (ActionCommand(resolved) is { } command && AdvertisesCommand(server, command.Name))
                    actions.Add(new(String(resolved, "title") ?? "Code action", String(resolved, "kind") ?? "", null,
                        false, command));
            }
        return actions;
    }

    public async Task<bool> ExecuteCommandAsync(string documentPath, LanguageCommandDescriptor command,
        CancellationToken cancellationToken)
    {
        var server = Ready(documentPath);
        if (server is null || !AdvertisesCommand(server, command.Name)) return false;
        await server.RequestAsync<JsonElement>("workspace/executeCommand", new
        {
            command = command.Name,
            arguments = command.Arguments is { } arguments && arguments.ValueKind == JsonValueKind.Array
                ? arguments : JsonSerializer.SerializeToElement(Array.Empty<object>())
        }, cancellationToken);
        return true;
    }

    private async Task<JsonElement?> PositionRequest(LanguageRequest request, string method, CancellationToken cancellationToken,
        object? extra = null)
    {
        await _synchronize(request.DocumentId, cancellationToken);
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

    internal async Task<WorkspaceEdit?> WorkspaceEditAsync(string title, JsonElement edit, CancellationToken token)
    {
        var documents = new List<WorkspaceDocumentEdit>();
        var resources = new List<WorkspaceResourceEdit>();
        var renamedSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspaceRoot = Servers().Select(server => server.WorkspaceRoot).FirstOrDefault();
        if (edit.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Object)
            foreach (var property in changes.EnumerateObject())
                await AddDocumentAsync(property.Name, null, property.Value);
        if (edit.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
            foreach (var change in documentChanges.EnumerateArray())
                if (change.TryGetProperty("textDocument", out var document) && change.TryGetProperty("edits", out var edits))
                    await AddDocumentAsync(String(document, "uri") ?? "", document.TryGetProperty("version", out var version)
                        && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var expectedVersion)
                            ? expectedVersion : null, edits);
                else if (String(change, "kind") is { } kind) AddResource(kind, change);
        return documents.Count == 0 && resources.Count == 0 ? null : new(title, documents, resources);

        async Task AddDocumentAsync(string uriText, long? expectedVersion, JsonElement edits)
        {
            if (!Uri.TryCreate(uriText, UriKind.Absolute, out var uri) || !uri.IsFile) return;
            var path = Path.GetFullPath(uri.LocalPath); var snapshot = _snapshot(path);
            if (workspaceRoot is not null && !Inside(workspaceRoot, path))
                throw new InvalidDataException("The language server requested an edit outside the workspace.");
            var sourcePath = renamedSources.GetValueOrDefault(uriText);
            var oldText = snapshot?.Text ?? (File.Exists(path) ? await File.ReadAllTextAsync(path, token)
                : sourcePath is not null && Uri.TryCreate(sourcePath, UriKind.Absolute, out var source) && source.IsFile
                    && File.Exists(source.LocalPath) ? await File.ReadAllTextAsync(source.LocalPath, token) : "");
            documents.Add(new(path, expectedVersion ?? snapshot?.Version, oldText, ApplyEdits(oldText, edits)));
        }

        void AddResource(string kind, JsonElement operation)
        {
            var oldUri = String(operation, kind == "rename" ? "oldUri" : "uri");
            var newUri = kind == "rename" ? String(operation, "newUri") : null;
            if (!FilePath(oldUri, out var path) || newUri is not null && !FilePath(newUri, out _)) return;
            if (workspaceRoot is not null && (!Inside(workspaceRoot, path)
                || newUri is not null && FilePath(newUri, out var destination) && !Inside(workspaceRoot, destination)))
                throw new InvalidDataException("The language server requested a resource operation outside the workspace.");
            var options = operation.TryGetProperty("options", out var configured) ? configured : default;
            var overwrite = Bool(options, "overwrite");
            var ignore = Bool(options, kind == "delete" ? "ignoreIfNotExists" : "ignoreIfExists");
            var recursive = Bool(options, "recursive");
            var newPath = FilePath(newUri, out var convertedNewPath) ? convertedNewPath : null;
            resources.Add(new(kind, path, newPath, overwrite, ignore, recursive));
            if (kind == "rename" && newUri is not null && oldUri is not null) renamedSources[newUri] = oldUri;
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
    private static bool Bool(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static bool FilePath(string? value, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile) return false;
        path = Path.GetFullPath(uri.LocalPath); return true;
    }
    private static bool Inside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
    private static bool Supports(LanguageServerManager server, string provider, string property) => server.Capabilities.TryGetProperty(provider, out var value)
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var enabled) && enabled.ValueKind == JsonValueKind.True;
    private static bool SupportsCodeActionResolve(LanguageServerManager server) =>
        Supports(server, "codeActionProvider", "resolveProvider")
        || server.Registrations("textDocument/codeAction").Any(registration => registration.RegisterOptions is { } options
            && options.TryGetProperty("resolveProvider", out var resolve) && resolve.ValueKind == JsonValueKind.True);
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
        Dynamic("textDocument/diagnostic", LanguageCapabilities.Diagnostics);
        return result;
        void Add(string name, LanguageCapabilities capability) { if (value.TryGetProperty(name, out var item) && item.ValueKind is not JsonValueKind.False and not JsonValueKind.Null) result |= capability; }
        void Dynamic(string method, LanguageCapabilities capability) { if (server.IsMethodRegistered(method)) result |= capability; }
    }
    private static bool SupportsDiagnostics(LanguageServerManager server) =>
        server.Capabilities.TryGetProperty("diagnosticProvider", out var provider)
            && provider.ValueKind is not JsonValueKind.False and not JsonValueKind.Null
        || server.IsMethodRegistered("textDocument/diagnostic");
    private static CompletionEntry CompletionEntry(string id, JsonElement item, string label, TextRange range) => new(id, label,
        String(item, "filterText") ?? label, String(item, "sortText") ?? label, Kind(Int(item, "kind")), range,
        item.TryGetProperty("commitCharacters", out var chars) ? chars.EnumerateArray().SelectMany(value => value.GetString() ?? "").ToArray() : [],
        Int(item, "insertTextFormat") == 2, Markup(item.TryGetProperty("documentation", out var docs) ? docs : default).FirstOrDefault() ?? String(item, "detail"));
    private static TextRange CompletionRange(JsonElement item, string text, int position) => item.TryGetProperty("textEdit", out var edit)
        && (edit.TryGetProperty("range", out var range) || edit.TryGetProperty("insert", out range))
            ? LspConverters.ToRange(text, ParseRange(range)) : new(position, 0);
    internal static (string Text, int? Caret) ExpandSnippet(string snippet)
    {
        var result = new System.Text.StringBuilder(snippet.Length);
        int? caret = null;
        for (var index = 0; index < snippet.Length;)
        {
            if (snippet[index] == '\\' && index + 1 < snippet.Length && snippet[index + 1] is '$' or '}' or '\\')
            {
                result.Append(snippet[index + 1]); index += 2; continue;
            }
            if (snippet[index] != '$') { result.Append(snippet[index++]); continue; }
            var marker = index++;
            if (index < snippet.Length && char.IsDigit(snippet[index]))
            {
                var tabStop = 0;
                while (index < snippet.Length && char.IsDigit(snippet[index])) tabStop = tabStop * 10 + snippet[index++] - '0';
                if (tabStop == 0) caret = result.Length;
                continue;
            }
            if (index >= snippet.Length || snippet[index++] != '{') { result.Append('$'); index = marker + 1; continue; }
            var numberStart = index;
            while (index < snippet.Length && char.IsDigit(snippet[index])) index++;
            if (numberStart == index) { result.Append("${"); continue; }
            var number = int.Parse(snippet[numberStart..index], System.Globalization.CultureInfo.InvariantCulture);
            if (index < snippet.Length && snippet[index] == ':')
            {
                var end = snippet.IndexOf('}', ++index);
                if (end < 0) { result.Append(snippet[marker..]); break; }
                if (number == 0) caret = result.Length;
                result.Append(snippet[index..end]); index = end + 1; continue;
            }
            if (index < snippet.Length && snippet[index] == '|')
            {
                var end = snippet.IndexOf("|}", ++index, StringComparison.Ordinal);
                if (end < 0) { result.Append(snippet[marker..]); break; }
                var choice = snippet[index..end].Split(',')[0];
                if (number == 0) caret = result.Length;
                result.Append(choice); index = end + 2; continue;
            }
            if (index < snippet.Length && snippet[index] == '}') { if (number == 0) caret = result.Length; index++; continue; }
            result.Append(snippet[marker..index]);
        }
        return (result.ToString(), caret);
    }
    private static LanguageCommandDescriptor? CompletionCommand(JsonElement item)
    {
        if (!item.TryGetProperty("command", out var command) || String(command, "command") is not { } name) return null;
        return new(name, command.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null);
    }
    private static LanguageCommandDescriptor? ActionCommand(JsonElement item)
    {
        var value = item.TryGetProperty("command", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : item;
        if (String(value, "command") is not { } name) return null;
        return new(name, value.TryGetProperty("arguments", out var arguments) ? arguments.Clone() : null);
    }
    private static bool AdvertisesCommand(LanguageServerManager server, string command)
    {
        if (server.Capabilities.TryGetProperty("executeCommandProvider", out var provider)
            && provider.TryGetProperty("commands", out var commands) && commands.ValueKind == JsonValueKind.Array
            && commands.EnumerateArray().Any(item => item.GetString() == command)) return true;
        return server.Registrations("workspace/executeCommand").Any(registration => registration.RegisterOptions is { } options
            && options.TryGetProperty("commands", out var registered) && registered.ValueKind == JsonValueKind.Array
            && registered.EnumerateArray().Any(item => item.GetString() == command));
    }
    private static IReadOnlyList<string> Markup(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => [CleanMarkup(value.GetString())], JsonValueKind.Array => value.EnumerateArray().SelectMany(Markup).ToArray(),
        JsonValueKind.Object when value.TryGetProperty("value", out var item) => [CleanMarkup(item.GetString())], _ => []
    };
    private static string CleanMarkup(string? value) => EmbeddedDataImage.Replace(value ?? string.Empty, string.Empty).Trim();
    internal static IReadOnlyList<string> HoverSections(JsonElement contents) => Markup(contents)
        .Where(section => !string.IsNullOrWhiteSpace(section)).ToArray();
    private static object ToRange(string text, TextRange range) => new { start = LspConverters.ToPosition(text, range.Start), end = LspConverters.ToPosition(text, range.Start + range.Length) };
    private static string ApplyEdits(string text, JsonElement edits)
    {
        if (edits.ValueKind != JsonValueKind.Array) return text;
        var converted = edits.EnumerateArray().Select(item => (Range: LspConverters.ToRange(text, ParseRange(item.GetProperty("range"))), Text: String(item, "newText") ?? ""))
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
        path = Path.GetFullPath(parsed.LocalPath); range = ParseRange(rangeName); return true;
    }
    private static IEnumerable<SymbolEntry> Symbols(JsonElement values, string path, string text)
    {
        if (values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var item in values.EnumerateArray())
        {
            var range = item.TryGetProperty("selectionRange", out var selection) ? selection : item.GetProperty("range");
            var lsp = ParseRange(range); var converted = LspConverters.ToRange(text, lsp);
            yield return new(String(item, "name") ?? "symbol", Kind(Int(item, "kind")), path, converted, lsp.Start.Line, lsp.Start.Character, "", String(item, "detail"));
            if (item.TryGetProperty("children", out var children)) foreach (var child in Symbols(children, path, text)) yield return child;
        }
    }
    private static string Kind(int kind) => kind switch { 2 => "Module", 5 => "Class", 6 => "Method", 7 => "Property", 8 => "Field", 10 => "Enum", 12 => "Function", 13 => "Variable", 14 => "Constant", 23 => "Struct", 11 => "Interface", _ => "Text" };
    internal static LspRange ParseRange(JsonElement value) => value.Deserialize<LspRange>(ProtocolJson)
        is { Start: not null, End: not null } range ? range : throw new InvalidDataException("The language server returned an invalid range.");
}
