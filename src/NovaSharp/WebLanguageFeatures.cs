using System.Text.RegularExpressions;

namespace NovaSharp;

internal enum WebProjectionKind { CSharp, Html, Css }
internal readonly record struct WebProjectionSegment(WebProjectionKind Kind, int HostStart, int ProjectedStart, int Length);

internal sealed class WebDocumentProjection(long version, string source, IReadOnlyList<WebProjectionSegment> segments)
{
    internal long Version { get; } = version;
    internal string Source { get; } = source;
    internal IReadOnlyList<WebProjectionSegment> Segments { get; } = segments;

    internal bool TryMapToProjected(long sourceVersion, WebProjectionKind kind, int hostPosition, out int position)
    {
        position = 0;
        if (sourceVersion != Version) return false;
        var segment = Segments.FirstOrDefault(item => item.Kind == kind
            && hostPosition >= item.HostStart && hostPosition <= item.HostStart + item.Length);
        if (segment.Length == 0) return false;
        position = segment.ProjectedStart + hostPosition - segment.HostStart;
        return true;
    }

    internal bool TryMapToHost(long sourceVersion, WebProjectionKind kind, TextRange projected, out TextRange host)
    {
        host = default;
        if (sourceVersion != Version) return false;
        var segment = Segments.FirstOrDefault(item => item.Kind == kind
            && projected.Start >= item.ProjectedStart
            && projected.Start + projected.Length <= item.ProjectedStart + item.Length);
        if (segment.Length == 0) return false;
        host = new(segment.HostStart + projected.Start - segment.ProjectedStart, projected.Length);
        return true;
    }
}

internal static partial class WebProjectionParser
{
    [GeneratedRegex("<style(?:\\s[^>]*)?>(?<body>[\\s\\S]*?)</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StyleRegex();
    [GeneratedRegex("@(?:code|functions)\\s*\\{")]
    private static partial Regex CodeRegex();

    internal static WebDocumentProjection Parse(string path, string source, long version)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".css") return new(version, source, [new(WebProjectionKind.Css, 0, 0, source.Length)]);
        if (extension is ".html" or ".htm") return new(version, source, [new(WebProjectionKind.Html, 0, 0, source.Length)]);
        var segments = new List<WebProjectionSegment>();
        var projected = new Dictionary<WebProjectionKind, int>();
        foreach (Match match in StyleRegex().Matches(source))
        {
            var body = match.Groups["body"];
            Add(WebProjectionKind.Css, body.Index, body.Length);
        }
        foreach (Match match in CodeRegex().Matches(source))
        {
            var open = source.IndexOf('{', match.Index);
            var close = MatchingBrace(source, open);
            if (close > open) Add(WebProjectionKind.CSharp, open + 1, close - open - 1);
        }
        var occupied = segments.Where(item => item.Kind != WebProjectionKind.Html)
            .Select(item => new TextRange(item.HostStart, item.Length)).OrderBy(item => item.Start).ToArray();
        var cursor = 0;
        foreach (var range in occupied)
        {
            if (range.Start > cursor) Add(WebProjectionKind.Html, cursor, range.Start - cursor);
            cursor = Math.Max(cursor, range.Start + range.Length);
        }
        if (cursor < source.Length) Add(WebProjectionKind.Html, cursor, source.Length - cursor);
        return new(version, source, segments.OrderBy(item => item.HostStart).ToArray());

        void Add(WebProjectionKind kind, int start, int length)
        {
            if (length <= 0) return;
            var offset = projected.GetValueOrDefault(kind);
            segments.Add(new(kind, start, offset, length));
            projected[kind] = offset + length;
        }
    }

    private static int MatchingBrace(string text, int open)
    {
        var depth = 0;
        var quote = '\0';
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (character == '\\') index++;
                else if (character == quote) quote = '\0';
                continue;
            }
            if (character is '\'' or '"') { quote = character; continue; }
            if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return index;
        }
        return -1;
    }
}

internal sealed partial class WebLanguageProvider(RoslynProjectSystem projectSystem, LanguageDiagnosticStore diagnostics)
    : ILanguageProvider, IExtendedLanguageProvider
{
    private static readonly string[] HtmlTags = ["a", "article", "aside", "button", "div", "form", "h1", "header",
        "img", "input", "label", "li", "link", "main", "meta", "nav", "ol", "p", "script", "section", "span", "style", "table", "ul"];
    private static readonly string[] HtmlAttributes = ["class", "id", "href", "src", "title", "role", "aria-label", "disabled", "type", "value"];
    private static readonly string[] CssProperties = ["align-items", "background", "border", "color", "display", "flex", "font-size",
        "gap", "grid-template-columns", "height", "margin", "max-width", "min-width", "overflow", "padding", "position", "width"];
    private static readonly string[] RazorDirectives = ["@attribute", "@code", "@functions", "@implements", "@inherits", "@inject",
        "@layout", "@namespace", "@page", "@rendermode", "@typeparam", "@using"];
    private static readonly string[] CSharpKeywords = ["bool", "class", "decimal", "double", "else", "false", "foreach", "if", "int",
        "new", "null", "private", "protected", "public", "return", "string", "true", "var", "void", "while"];
    private readonly Dictionary<string, CompletionEntry> _completionItems = [];
    private long _completionId;
    internal int RetainedCompletionCount { get { lock (_completionItems) return _completionItems.Count; } }
    internal int RetainedCompletionBytes
    {
        get
        {
            lock (_completionItems)
                return _completionItems.Values.Sum(item => 64 + 2 * (item.Id.Length + item.DisplayText.Length
                    + item.FilterText.Length + item.SortText.Length + item.Kind.Length + (item.Detail?.Length ?? 0)));
        }
    }

    public LanguageProviderInfo GetInfo(string documentPath)
    {
        var extension = Path.GetExtension(documentPath).ToLowerInvariant();
        var id = extension == ".css" ? "css" : extension is ".html" or ".htm" ? "html" : "razor";
        var name = id == "css" ? "CSS" : id == "html" ? "HTML" : "Razor";
        var capabilities = LanguageCapabilities.Completion | LanguageCapabilities.Hover
            | LanguageCapabilities.SemanticTokens | LanguageCapabilities.Diagnostics | LanguageCapabilities.Formatting;
        if (id != "css") capabilities |= LanguageCapabilities.Symbols | LanguageCapabilities.Navigation | LanguageCapabilities.Rename;
        return new(id, name, capabilities);
    }

    public void ClearDiagnostics(string documentPath) => diagnostics.Remove(documentPath);
    internal void Restart()
    {
        lock (_completionItems) _completionItems.Clear();
    }

    public Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request, bool explicitInvocation,
        CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult(Degraded<CompletionResult>(request));
        var position = Math.Clamp(request.Position, 0, snapshot.Text.Length);
        var prefix = WordPrefix(snapshot.Text, position);
        IEnumerable<(string Text, string Kind, string? Detail)> candidates;
        var extension = Path.GetExtension(request.DocumentId).ToLowerInvariant();
        var projection = WebProjectionParser.Parse(request.DocumentId, snapshot.Text, request.Version);
        if (projection.TryMapToProjected(request.Version, WebProjectionKind.CSharp, position, out _))
            candidates = CSharpKeywords.Concat(ProjectTypeNames(request.DocumentId)).Distinct(StringComparer.Ordinal)
                .Select(item => (item, char.IsUpper(item[0]) ? "Type" : "Keyword", (string?)"Projected C#"));
        else if (extension == ".css" || InsideStyle(snapshot.Text, position))
            candidates = CssProperties.Select(item => (item, "Property", (string?)"CSS property"));
        else if (position > 0 && snapshot.Text[position - 1] == '@')
            candidates = RazorDirectives.Select(item => (item[1..], "Keyword", (string?)"Razor directive"));
        else if (InsideTag(snapshot.Text, position) && !TagNamePosition(snapshot.Text, position))
            candidates = HtmlAttributes.Select(item => (item, "Property", (string?)"HTML attribute"));
        else
            candidates = HtmlTags.Concat(ComponentNames(request.DocumentId)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(item => (item, char.IsUpper(item[0]) ? "Component" : "Tag", (string?)null));
        lock (_completionItems) _completionItems.Clear();
        var items = candidates.Where(item => explicitInvocation || item.Text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Take(200).Select(item => Entry(request, item.Text, item.Kind, item.Detail, prefix.Length)).ToArray();
        return Task.FromResult(new LanguageResponse<CompletionResult>(request.Version, new(items)));
    }

    public Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string itemId,
        CancellationToken cancellationToken)
    {
        lock (_completionItems)
            return Task.FromResult(_completionItems.TryGetValue(itemId, out var item)
                ? new LanguageResponse<CompletionEntry>(request.Version, item)
                : Degraded<CompletionEntry>(request));
    }

    public Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string itemId,
        char? commitCharacter, CancellationToken cancellationToken)
    {
        lock (_completionItems)
            return Task.FromResult(_completionItems.TryGetValue(itemId, out var item)
                ? new LanguageResponse<CompletionEdit>(request.Version, new(item.Replacement, item.DisplayText, null))
                : Degraded<CompletionEdit>(request));
    }

    public Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request,
        CancellationToken cancellationToken) => Task.FromResult(new LanguageResponse<SignatureResult>(request.Version, null));

    public Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult(Degraded<HoverResult>(request));
        var range = WordRange(snapshot.Text, request.Position);
        if (range.Length == 0) return Task.FromResult(new LanguageResponse<HoverResult>(request.Version, null));
        var word = snapshot.Text.Substring(range.Start, range.Length);
        var description = HtmlTags.Contains(word, StringComparer.OrdinalIgnoreCase) ? $"HTML <{word}> element"
            : CssProperties.Contains(word, StringComparer.OrdinalIgnoreCase) ? $"CSS {word} property"
            : ComponentNames(request.DocumentId).Contains(word, StringComparer.OrdinalIgnoreCase) ? $"Blazor component {word}" : null;
        return Task.FromResult(new LanguageResponse<HoverResult>(request.Version,
            description is null ? null : new(range, [description])));
    }

    public Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult(Degraded<IReadOnlyList<SemanticSpan>>(request));
        var spans = new List<SemanticSpan>();
        AddMatches(spans, snapshot.Text, CommentRegex(), "comment");
        AddMatches(spans, snapshot.Text, RazorDirectiveRegex(), "keyword");
        AddMatches(spans, snapshot.Text, TagRegex(), "class name", 1);
        AddMatches(spans, snapshot.Text, AttributeRegex(), "property name", 1);
        AddMatches(spans, snapshot.Text, StringRegex(), "string");
        AddMatches(spans, snapshot.Text, CssPropertyRegex(), "property name", 1);
        var projection = WebProjectionParser.Parse(request.DocumentId, snapshot.Text, request.Version);
        foreach (var segment in projection.Segments.Where(item => item.Kind == WebProjectionKind.CSharp))
            foreach (Match match in CSharpKeywordRegex().Matches(snapshot.Text.Substring(segment.HostStart, segment.Length)))
                spans.Add(new(segment.HostStart + match.Index, match.Length, "keyword"));
        return Task.FromResult(new LanguageResponse<IReadOnlyList<SemanticSpan>>(request.Version,
            spans.OrderBy(item => item.Start).ToArray()));
    }

    public Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult(Degraded<IReadOnlyList<LanguageDiagnostic>>(request));
        var results = new List<LanguageDiagnostic>();
        var projection = WebProjectionParser.Parse(request.DocumentId, snapshot.Text, request.Version);
        var htmlSegments = projection.Segments.Where(item => item.Kind == WebProjectionKind.Html).ToArray();
        var stack = new Stack<Match>();
        foreach (Match match in ElementRegex().Matches(snapshot.Text).Where(match =>
            htmlSegments.Any(segment => match.Index >= segment.HostStart
                && match.Index + match.Length <= segment.HostStart + segment.Length)))
        {
            var name = match.Groups["name"].Value;
            if (match.Value.StartsWith("</", StringComparison.Ordinal))
            {
                if (stack.Count == 0 || !stack.Peek().Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                    results.Add(Diagnostic("WEB001", $"Unexpected closing tag </{name}>.", request, snapshot.Text, match.Index, match.Length));
                else stack.Pop();
            }
            else if (!match.Value.EndsWith("/>", StringComparison.Ordinal) && !VoidTags.Contains(name)) stack.Push(match);
        }
        foreach (var match in stack)
            results.Add(Diagnostic("WEB002", $"Element <{match.Groups["name"].Value}> is not closed.", request,
                snapshot.Text, match.Index, match.Length));
        if (Path.GetExtension(request.DocumentId).Equals(".css", StringComparison.OrdinalIgnoreCase)
            || snapshot.Text.Contains("<style", StringComparison.OrdinalIgnoreCase))
        {
            var balance = 0;
            foreach (var (character, index) in snapshot.Text.Select((character, index) => (character, index)))
            {
                if (character == '{') balance++;
                else if (character == '}' && --balance < 0)
                    results.Add(Diagnostic("CSS001", "Unexpected closing brace.", request, snapshot.Text, index, 1));
            }
            if (balance > 0)
            {
                var start = snapshot.Text.LastIndexOf('{');
                results.Add(Diagnostic("CSS002", "CSS block is not closed.", request, snapshot.Text, start, 1));
            }
        }
        diagnostics.Replace(request.DocumentId, request.Version, LanguageDiagnosticSource.Compiler, results);
        return Task.FromResult(new LanguageResponse<IReadOnlyList<LanguageDiagnostic>>(request.Version, results));
    }

    public Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult(Degraded<FormatResult>(request));
        var lines = snapshot.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var depth = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("</", StringComparison.Ordinal) || trimmed.StartsWith('}')) depth = Math.Max(0, depth - 1);
            lines[index] = new string(' ', depth * 4) + trimmed;
            if ((trimmed.StartsWith('<') && !trimmed.StartsWith("</", StringComparison.Ordinal)
                    && !trimmed.EndsWith("/>", StringComparison.Ordinal) && !trimmed.Contains("</", StringComparison.Ordinal))
                || trimmed.EndsWith('{')) depth++;
        }
        var text = string.Join(Environment.NewLine, lines);
        var selection = request.Range ?? new(0, 0);
        return Task.FromResult(new LanguageResponse<FormatResult>(request.Version,
            new(text, Math.Min(selection.Start, text.Length), Math.Min(selection.Length, Math.Max(0, text.Length - selection.Start)))));
    }

    public Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request, bool typeDefinition,
        CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult<IReadOnlyList<NavigationTarget>>([]);
        var range = WordRange(snapshot.Text, request.Position);
        var name = range.Length == 0 ? "" : snapshot.Text.Substring(range.Start, range.Length);
        var target = ComponentFiles(request.DocumentId).FirstOrDefault(path =>
            Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<NavigationTarget>>(target is null ? []
            : [new(target, new(0, 0), 0, 0, name, "Razor", NavigationKind.Definition)]);
    }

    public Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request,
        CancellationToken cancellationToken) => GetDefinitionsAsync(request, false, cancellationToken);
    public Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NavigationTarget>>([]);

    public Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot) return Task.FromResult<IReadOnlyList<SymbolEntry>>([]);
        var symbols = ElementRegex().Matches(snapshot.Text).Cast<Match>().Where(item => !item.Value.StartsWith("</", StringComparison.Ordinal))
            .Select(item => Symbol(request.DocumentId, snapshot.Text, item.Groups["name"].Value, "Element", item.Groups["name"].Index,
                item.Groups["name"].Length)).ToArray();
        return Task.FromResult<IReadOnlyList<SymbolEntry>>(symbols);
    }

    public async Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query, CancellationToken cancellationToken)
    {
        var results = new List<SymbolEntry>();
        foreach (var path in ComponentFiles(projectSystem.State.Path ?? ""))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(Symbol(path, await File.ReadAllTextAsync(path, cancellationToken), name, "Component", 0, 0));
        }
        return results;
    }

    public Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string newName, CancellationToken cancellationToken)
    {
        if (Snapshot(request) is not { } snapshot || string.IsNullOrWhiteSpace(newName)) return Task.FromResult<WorkspaceEdit?>(null);
        var range = WordRange(snapshot.Text, request.Position);
        if (range.Length == 0) return Task.FromResult<WorkspaceEdit?>(null);
        var oldName = snapshot.Text.Substring(range.Start, range.Length);
        var changed = ElementRegex().Replace(snapshot.Text, match =>
            match.Groups["name"].Value.Equals(oldName, StringComparison.OrdinalIgnoreCase)
                ? string.Concat(match.Value.AsSpan(0, match.Groups["name"].Index - match.Index), newName,
                    match.Value.AsSpan(match.Groups["name"].Index - match.Index + match.Groups["name"].Length))
                : match.Value);
        return Task.FromResult<WorkspaceEdit?>(changed == snapshot.Text ? null : new($"Rename {oldName} to {newName}",
            [new(request.DocumentId, request.Version, snapshot.Text, changed)]));
    }

    public Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CodeActionEntry>>([]);

    private EditorSnapshot? Snapshot(LanguageRequest request)
    {
        var snapshot = projectSystem.GetTrackedSnapshot(request.DocumentId);
        return snapshot is { Version: var version } && version == request.Version ? snapshot : null;
    }

    private CompletionEntry Entry(LanguageRequest request, string text, string kind, string? detail, int prefixLength)
    {
        var id = Interlocked.Increment(ref _completionId).ToString();
        var entry = new CompletionEntry(id, text, text, text, kind,
            new(Math.Max(0, request.Position - prefixLength), prefixLength), [' ', '>', '='], false, detail);
        lock (_completionItems) _completionItems[id] = entry;
        return entry;
    }

    private IEnumerable<string> ComponentNames(string path) => ComponentFiles(path).Select(item => Path.GetFileNameWithoutExtension(item)!);
    private IEnumerable<string> ProjectTypeNames(string path)
    {
        var root = DiscoveryRoot(path);
        if (root is null || !Directory.Exists(root)) return [];
        return SafeFiles(root, "*.cs")
            .Where(item => !item.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !item.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Take(200)
            .SelectMany(ReadTypes);
    }
    private IEnumerable<string> ComponentFiles(string path)
    {
        var root = DiscoveryRoot(path);
        if (root is null || !Directory.Exists(root)) return [];
        return SafeFiles(root, "*.razor")
            .Where(item => !item.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !item.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }
    private string? DiscoveryRoot(string path)
    {
        if (projectSystem.State.Path is { } project) return Path.GetDirectoryName(project);
        for (var directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory))
            try
            {
                if (Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly).Any()) return directory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return Path.GetDirectoryName(path);
    }
    private static IReadOnlyList<string> SafeFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }
    private static IEnumerable<string> ReadTypes(string path)
    {
        try { return TypeRegex().Matches(File.ReadAllText(path)).Select(match => match.Groups[1].Value).ToArray(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string WordPrefix(string text, int position)
    {
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '-' or '_' or '@')) start--;
        return text[start..position].TrimStart('@');
    }
    private static TextRange WordRange(string text, int position)
    {
        position = Math.Clamp(position, 0, text.Length);
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '-' or '_')) start--;
        var end = position;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] is '-' or '_')) end++;
        return new(start, end - start);
    }
    private static bool InsideTag(string text, int position) => text.LastIndexOf('<', Math.Max(0, position - 1))
        > text.LastIndexOf('>', Math.Max(0, position - 1));
    private static bool TagNamePosition(string text, int position)
    {
        var open = text.LastIndexOf('<', Math.Max(0, position - 1));
        if (open < 0) return false;
        return text.AsSpan(open + 1, Math.Max(0, position - open - 1)).IndexOfAny(" \t\r\n") < 0;
    }
    private static bool InsideStyle(string text, int position) => text.LastIndexOf("<style", Math.Max(0, position - 1),
        StringComparison.OrdinalIgnoreCase) > text.LastIndexOf("</style", Math.Max(0, position - 1), StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> VoidTags = new(["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"], StringComparer.OrdinalIgnoreCase);
    private static void AddMatches(List<SemanticSpan> spans, string text, Regex regex, string classification, int group = 0)
    {
        foreach (Match match in regex.Matches(text))
        {
            var value = match.Groups[group];
            if (value.Length > 0) spans.Add(new(value.Index, value.Length, classification));
        }
    }
    private static LanguageDiagnostic Diagnostic(string id, string message, LanguageRequest request, string text, int start, int length)
    {
        var before = text.AsSpan(0, start);
        var line = before.Count('\n');
        var last = text.LastIndexOf('\n', Math.Max(0, start - 1));
        return new(id, LanguageDiagnosticSource.Compiler, LanguageDiagnosticSeverity.Error, message,
            request.DocumentId, new(start, length), line, start - last - 1, "Web");
    }
    private static SymbolEntry Symbol(string path, string text, string name, string kind, int start, int length)
    {
        var line = text.AsSpan(0, Math.Min(start, text.Length)).Count('\n');
        var last = text.LastIndexOf('\n', Math.Max(0, start - 1));
        return new(name, kind, path, new(start, length), line, start - last - 1, "Web", null);
    }
    private static LanguageResponse<T> Degraded<T>(LanguageRequest request) => new(request.Version, default, true);

    [GeneratedRegex("<!--[\\s\\S]*?-->|@\\*[\\s\\S]*?\\*@")]
    private static partial Regex CommentRegex();
    [GeneratedRegex("@(page|using|inject|code|functions|implements|inherits|layout|namespace|typeparam|attribute|rendermode)\\b")]
    private static partial Regex RazorDirectiveRegex();
    [GeneratedRegex("</?([A-Za-z][\\w.:-]*)")]
    private static partial Regex TagRegex();
    [GeneratedRegex("\\s([:@A-Za-z][\\w:.-]*)(?=\\s*=)")]
    private static partial Regex AttributeRegex();
    [GeneratedRegex("\"[^\"]*\"|'[^']*'")]
    private static partial Regex StringRegex();
    [GeneratedRegex("(?:^|[;{]\\s*)([-\\w]+)\\s*:", RegexOptions.Multiline)]
    private static partial Regex CssPropertyRegex();
    [GeneratedRegex("</?(?<name>[A-Za-z][\\w.:-]*)[^>]*>")]
    private static partial Regex ElementRegex();
    [GeneratedRegex("\\b(bool|class|decimal|double|else|false|foreach|if|int|new|null|private|protected|public|return|string|true|var|void|while)\\b")]
    private static partial Regex CSharpKeywordRegex();
    [GeneratedRegex("\\b(?:class|record|struct|interface|enum)\\s+([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex TypeRegex();
}

internal sealed class LanguageProviderRegistry : ILanguageProvider, IExtendedLanguageProvider
{
    private readonly CSharpLanguageProvider _csharp;
    private readonly WebLanguageProvider _web;
    private readonly Dictionary<string, (ILanguageProvider Provider, IExtendedLanguageProvider Extended)> _providers
        = new(StringComparer.OrdinalIgnoreCase);
    internal LanguageProviderRegistry(RoslynProjectSystem projectSystem)
    {
        Diagnostics = new();
        _csharp = new(projectSystem, Diagnostics);
        _web = new(projectSystem, Diagnostics);
        Register([".cs"], _csharp);
        Register([".razor", ".cshtml", ".html", ".htm", ".css"], _web);
    }
    internal LanguageDiagnosticStore Diagnostics { get; }
    internal int RetainedCompletionCount => _csharp.RetainedCompletionCount + _web.RetainedCompletionCount;
    internal int RetainedWebCompletionBytes => _web.RetainedCompletionBytes;
    internal int RetainedProjectionCount => 0;
    internal void Register(IEnumerable<string> extensions, ILanguageProvider provider)
    {
        if (provider is not IExtendedLanguageProvider extended)
            throw new ArgumentException("Language providers must declare capabilities.", nameof(provider));
        foreach (var extension in extensions)
            _providers[extension.StartsWith('.') ? extension : $".{extension}"] = (provider, extended);
    }
    internal void Restart()
    {
        _csharp.Restart();
        _web.Restart();
        Diagnostics.Clear();
    }
    private (ILanguageProvider Provider, IExtendedLanguageProvider Extended) Registration(string path) =>
        _providers.GetValueOrDefault(Path.GetExtension(path), (UnavailableLanguageProvider.Instance, UnavailableLanguageProvider.Instance));
    private ILanguageProvider Provider(string path) => Registration(path).Provider;
    private IExtendedLanguageProvider Extended(string path) => Registration(path).Extended;
    public LanguageProviderInfo GetInfo(string path) => Extended(path).GetInfo(path);
    public void ClearDiagnostics(string path) => Extended(path).ClearDiagnostics(path);
    public Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request, bool explicitInvocation, CancellationToken token) => Provider(request.DocumentId).GetCompletionsAsync(request, explicitInvocation, token);
    public Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string id, CancellationToken token) => Provider(request.DocumentId).GetCompletionDetailsAsync(request, id, token);
    public Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string id, char? character, CancellationToken token) => Provider(request.DocumentId).GetCompletionEditAsync(request, id, character, token);
    public Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request, CancellationToken token) => Provider(request.DocumentId).GetSignatureHelpAsync(request, token);
    public Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken token) => Provider(request.DocumentId).GetHoverAsync(request, token);
    public Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request, CancellationToken token) => Provider(request.DocumentId).GetSemanticSpansAsync(request, token);
    public Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request, CancellationToken token) => Provider(request.DocumentId).GetDiagnosticsAsync(request, token);
    public Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken token) => Provider(request.DocumentId).FormatAsync(request, token);
    public Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request, bool type, CancellationToken token) => Extended(request.DocumentId).GetDefinitionsAsync(request, type, token);
    public Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request, CancellationToken token) => Extended(request.DocumentId).GetImplementationsAsync(request, token);
    public Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request, CancellationToken token) => Extended(request.DocumentId).FindReferencesAsync(request, token);
    public Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request, CancellationToken token) => Extended(request.DocumentId).GetDocumentSymbolsAsync(request, token);
    public async Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query, CancellationToken token) =>
        (await _csharp.FindWorkspaceSymbolsAsync(query, token)).Concat(await _web.FindWorkspaceSymbolsAsync(query, token)).ToArray();
    public Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string name, CancellationToken token) => Extended(request.DocumentId).RenameAsync(request, name, token);
    public Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request, CancellationToken token) => Extended(request.DocumentId).GetCodeActionsAsync(request, token);
}

internal sealed class UnavailableLanguageProvider : ILanguageProvider, IExtendedLanguageProvider
{
    internal static UnavailableLanguageProvider Instance { get; } = new();
    private UnavailableLanguageProvider() { }
    public LanguageProviderInfo GetInfo(string path) => new("text", "Text", LanguageCapabilities.None, false,
        $"No language provider is registered for {Path.GetExtension(path)} files.");
    public void ClearDiagnostics(string path) { }
    private static LanguageResponse<T> Empty<T>(LanguageRequest request) => new(request.Version, default, true);
    public Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request, bool explicitInvocation, CancellationToken token) => Task.FromResult(Empty<CompletionResult>(request));
    public Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string id, CancellationToken token) => Task.FromResult(Empty<CompletionEntry>(request));
    public Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string id, char? character, CancellationToken token) => Task.FromResult(Empty<CompletionEdit>(request));
    public Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request, CancellationToken token) => Task.FromResult(Empty<SignatureResult>(request));
    public Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken token) => Task.FromResult(Empty<HoverResult>(request));
    public Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request, CancellationToken token) => Task.FromResult(Empty<IReadOnlyList<SemanticSpan>>(request));
    public Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request, CancellationToken token) => Task.FromResult(Empty<IReadOnlyList<LanguageDiagnostic>>(request));
    public Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken token) => Task.FromResult(Empty<FormatResult>(request));
    public Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request, bool type, CancellationToken token) => Task.FromResult<IReadOnlyList<NavigationTarget>>([]);
    public Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<NavigationTarget>>([]);
    public Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<NavigationTarget>>([]);
    public Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<SymbolEntry>>([]);
    public Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query, CancellationToken token) => Task.FromResult<IReadOnlyList<SymbolEntry>>([]);
    public Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string name, CancellationToken token) => Task.FromResult<WorkspaceEdit?>(null);
    public Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request, CancellationToken token) => Task.FromResult<IReadOnlyList<CodeActionEntry>>([]);
}
