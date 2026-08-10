#if DEBUG
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;
#endif

namespace NovaSharp;

public sealed record LanguageRequest(string DocumentId, string? ProjectContext, long Version, int Position,
    TextRange? Range = null);
public sealed record LanguageResponse<T>(long SourceVersion, T? Value, bool IsDegraded = false);
public sealed record CompletionEntry(string Id, string DisplayText, string FilterText, string SortText,
    string Kind, TextRange Replacement, IReadOnlyList<char> CommitCharacters, bool IsSnippet, string? Detail = null);
public sealed record CompletionResult(IReadOnlyList<CompletionEntry> Items);
public sealed record LanguageCommandDescriptor(string Name, System.Text.Json.JsonElement? Arguments = null);
public sealed record CompletionEdit(TextRange Replacement, string NewText, int? NewPosition,
    LanguageCommandDescriptor? Command = null);
public sealed record SignatureResult(IReadOnlyList<string> Signatures, int ActiveSignature, int ActiveParameter);
public sealed record HoverResult(TextRange Range, IReadOnlyList<string> Sections);
public sealed record SemanticSpan(int Start, int Length, string Classification);
public sealed record FormatResult(string Text, int SelectionStart, int SelectionLength);
public enum LanguageDiagnosticSeverity { Hidden, Information, Warning, Error }
public enum LanguageDiagnosticSource { Compiler, Analyzer, Build, LanguageServer }
public sealed record LanguageDiagnostic(string Id, LanguageDiagnosticSource Source, LanguageDiagnosticSeverity Severity,
    string Message, string DocumentPath, TextRange Range, int StartLine, int StartColumn, string? ProjectName,
    int EndLine = -1, int EndColumn = -1, IReadOnlyList<int>? Tags = null,
    IReadOnlyList<string>? RelatedInformation = null, string? CodeDescription = null, string? Producer = null,
    bool IsStale = false);

public interface ILanguageProvider
{
    Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request, bool explicitInvocation,
        CancellationToken cancellationToken);
    Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string itemId,
        CancellationToken cancellationToken);
    Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string itemId,
        char? commitCharacter, CancellationToken cancellationToken);
    Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request,
        CancellationToken cancellationToken);
    Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request,
        CancellationToken cancellationToken);
    Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken);
}

[Flags]
public enum LanguageCapabilities
{
    None = 0, Completion = 1, Hover = 2, SignatureHelp = 4, SemanticTokens = 8,
    Diagnostics = 16, Formatting = 32, Symbols = 64, Navigation = 128, Rename = 256, CodeActions = 512
}

public sealed record LanguageProviderInfo(string LanguageId, string DisplayName,
    LanguageCapabilities Capabilities, bool IsAvailable = true, string? Status = null);

internal interface IExtendedLanguageProvider
{
    void ClearDiagnostics(string documentPath);
    LanguageProviderInfo GetInfo(string documentPath);
    Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request, bool typeDefinition,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query, CancellationToken cancellationToken);
    Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string newName, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request, CancellationToken cancellationToken);
    Task<bool> ExecuteCommandAsync(string documentPath, LanguageCommandDescriptor command,
        CancellationToken cancellationToken) => Task.FromResult(false);
}

#if DEBUG
internal sealed partial class CSharpLanguageProvider(RoslynProjectSystem projectSystem, LanguageDiagnosticStore? diagnosticStore = null)
    : ILanguageProvider, IExtendedLanguageProvider
{
    internal LanguageDiagnosticStore Diagnostics { get; } = diagnosticStore ?? new();
    private readonly Dictionary<string, (long Version, Document Document, CompletionItem Item)> _completionItems = [];
    private long _nextCompletionId;
    internal int RetainedCompletionCount { get { lock (_completionItems) return _completionItems.Count; } }

    public LanguageProviderInfo GetInfo(string documentPath) => new("csharp", "C#",
        LanguageCapabilities.Completion | LanguageCapabilities.Hover | LanguageCapabilities.SignatureHelp
        | LanguageCapabilities.SemanticTokens | LanguageCapabilities.Diagnostics | LanguageCapabilities.Formatting
        | LanguageCapabilities.Symbols | LanguageCapabilities.Navigation | LanguageCapabilities.Rename
        | LanguageCapabilities.CodeActions);
    public void ClearDiagnostics(string documentPath) => Diagnostics.Remove(documentPath);
    internal void Restart()
    {
        lock (_completionItems) _completionItems.Clear();
    }

    public async Task<LanguageResponse<CompletionResult>> GetCompletionsAsync(LanguageRequest request,
        bool explicitInvocation, CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<CompletionResult>(request);
        var service = CompletionService.GetService(document);
        if (service is null) return Degraded<CompletionResult>(request);
        var trigger = explicitInvocation ? CompletionTrigger.Invoke : CompletionTrigger.CreateInsertionTrigger(
            request.Position > 0 ? (await document.GetTextAsync(cancellationToken))[request.Position - 1] : '\0');
        var list = await service.GetCompletionsAsync(document, request.Position, trigger: trigger,
            cancellationToken: cancellationToken);
        var entries = new List<CompletionEntry>();
        lock (_completionItems) _completionItems.Clear();
        foreach (var item in list is null ? [] : list.ItemsList.Take(200))
        {
            var id = Interlocked.Increment(ref _nextCompletionId).ToString();
            lock (_completionItems) _completionItems[id] = (request.Version, document, item);
            entries.Add(ToEntry(id, item));
        }
        return new(request.Version, new(entries));
    }

    public async Task<LanguageResponse<CompletionEntry>> GetCompletionDetailsAsync(LanguageRequest request, string itemId,
        CancellationToken cancellationToken)
    {
        (long Version, Document Document, CompletionItem Item) cached;
        lock (_completionItems)
            if (!_completionItems.TryGetValue(itemId, out cached) || cached.Version != request.Version)
                return Degraded<CompletionEntry>(request);
        var service = CompletionService.GetService(cached.Document);
        if (service is null) return Degraded<CompletionEntry>(request);
        var description = await service.GetDescriptionAsync(cached.Document, cached.Item, cancellationToken);
        return new(request.Version, ToEntry(itemId, cached.Item) with { Detail = description?.Text });
    }

    public async Task<LanguageResponse<CompletionEdit>> GetCompletionEditAsync(LanguageRequest request, string itemId,
        char? commitCharacter, CancellationToken cancellationToken)
    {
        (long Version, Document Document, CompletionItem Item) cached;
        lock (_completionItems)
            if (!_completionItems.TryGetValue(itemId, out cached) || cached.Version != request.Version)
                return Degraded<CompletionEdit>(request);
        var service = CompletionService.GetService(cached.Document);
        if (service is null) return Degraded<CompletionEdit>(request);
        var change = await service.GetChangeAsync(cached.Document, cached.Item, commitCharacter, cancellationToken);
        return new(request.Version, new(new(change.TextChange.Span.Start, change.TextChange.Span.Length),
            change.TextChange.NewText ?? string.Empty, change.NewPosition));
    }

    public async Task<LanguageResponse<SignatureResult>> GetSignatureHelpAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<SignatureResult>(request);
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var invocation = root?.FindToken(Math.Max(0, request.Position - 1)).Parent?.AncestorsAndSelf()
            .OfType<InvocationExpressionSyntax>().FirstOrDefault();
        if (invocation is null || model is null) return new(request.Version, null);
        var symbols = model.GetMemberGroup(invocation.Expression, cancellationToken).OfType<IMethodSymbol>().ToArray();
        if (symbols.Length == 0 && model.GetSymbolInfo(invocation.Expression, cancellationToken).Symbol is IMethodSymbol symbol)
            symbols = [symbol];
        var activeParameter = invocation.ArgumentList.Arguments.GetSeparators()
            .Count(separator => separator.SpanStart < request.Position);
        var selected = model.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
        var activeSignature = selected is null ? -1 : Array.FindIndex(symbols,
            candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, selected.OriginalDefinition));
        if (activeSignature < 0) activeSignature = Math.Max(0, Array.FindIndex(symbols,
            candidate => activeParameter < candidate.Parameters.Length || candidate.Parameters.LastOrDefault()?.IsParams == true));
        return new(request.Version, new(symbols.Select(SymbolText).ToArray(), activeSignature, activeParameter));
    }

    public async Task<LanguageResponse<HoverResult>> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<HoverResult>(request);
        var service = QuickInfoService.GetService(document);
        var item = service is null ? null : await service.GetQuickInfoAsync(document, request.Position, cancellationToken);
        if (item is null) return new(request.Version, null);
        var sections = item.Sections.Select(section => section.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var model = await document.GetSemanticModelAsync(cancellationToken);
        var symbol = root is null || model is null ? null : model.GetSymbolInfo(root.FindToken(request.Position).Parent!, cancellationToken).Symbol;
        if (symbol is not null)
            sections.Add($"Project: {document.Project.Name} · Assembly: {symbol.ContainingAssembly?.Name ?? document.Project.AssemblyName}");
        return new(request.Version, new(new(item.Span.Start, item.Span.Length), sections));
    }

    public async Task<LanguageResponse<IReadOnlyList<SemanticSpan>>> GetSemanticSpansAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<IReadOnlyList<SemanticSpan>>(request);
        var text = await document.GetTextAsync(cancellationToken);
        var requested = request.Range is { } range ? new TextSpan(range.Start, range.Length) : new TextSpan(0, text.Length);
        var spans = await Classifier.GetClassifiedSpansAsync(document, requested, cancellationToken);
        var results = spans
            .Select(span => new SemanticSpan(span.TextSpan.Start, span.TextSpan.Length, span.ClassificationType)).ToArray();
        return new(request.Version, results);
    }

    public async Task<LanguageResponse<IReadOnlyList<LanguageDiagnostic>>> GetDiagnosticsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<IReadOnlyList<LanguageDiagnostic>>(request);
        var tree = await document.GetSyntaxTreeAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (tree is null || compilation is null) return new(request.Version, []);
        var compiler = compilation.GetDiagnostics(cancellationToken).Where(item => item.Location.SourceTree == tree)
            .Select(item => ToDiagnostic(item, LanguageDiagnosticSource.Compiler, document)).ToArray();
        Diagnostics.Replace(document.FilePath!, request.Version, LanguageDiagnosticSource.Compiler, compiler);

        var analyzers = document.Project.AnalyzerReferences.SelectMany(reference => reference.GetAnalyzers(document.Project.Language)).ToImmutableArray();
        var analyzer = analyzers.IsDefaultOrEmpty ? [] : (await compilation.WithAnalyzers(analyzers,
                options: null).GetAnalyzerDiagnosticsAsync(cancellationToken))
            .Where(item => item.Location.SourceTree == tree)
            .Select(item => ToDiagnostic(item, LanguageDiagnosticSource.Analyzer, document)).ToArray();
        Diagnostics.Replace(document.FilePath!, request.Version, LanguageDiagnosticSource.Analyzer, analyzer);
        return new(request.Version, compiler.Concat(analyzer).ToArray());
    }

    public async Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document is null) return Degraded<FormatResult>(request);
        var formatted = request.Range is { } range
            ? await Formatter.FormatAsync(document, new TextSpan(range.Start, range.Length), cancellationToken: cancellationToken)
            : await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
        var text = (await formatted.GetTextAsync(cancellationToken)).ToString();
        var selection = request.Range ?? new(0, 0);
        return new(request.Version, new(text, selection.Start, selection.Length));
    }

    private Task<Document?> ResolveAsync(LanguageRequest request, CancellationToken cancellationToken) =>
        projectSystem.GetLanguageDocumentAsync(request.DocumentId, request.ProjectContext, request.Version, cancellationToken);

    private static CompletionEntry ToEntry(string id, CompletionItem item) => new(id, item.DisplayText,
        item.FilterText, item.SortText, item.Tags.FirstOrDefault() ?? "Text", new(item.Span.Start, item.Span.Length),
        CompletionCharacters(item), item.Tags.Any(tag => tag.Contains("snippet", StringComparison.OrdinalIgnoreCase)));
    private static IReadOnlyList<char> CompletionCharacters(CompletionItem item)
    {
        var characters = new HashSet<char>([' ', ';', '.', ',', '(', ')', '[', ']', '{', '}']);
        foreach (var rule in item.Rules.CommitCharacterRules)
            switch (rule.Kind)
            {
                case CharacterSetModificationKind.Add: characters.UnionWith(rule.Characters); break;
                case CharacterSetModificationKind.Remove: characters.ExceptWith(rule.Characters); break;
                case CharacterSetModificationKind.Replace: characters = rule.Characters.ToHashSet(); break;
            }
        return characters.ToArray();
    }
    private static string SymbolText(IMethodSymbol symbol) => symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
    private static LanguageDiagnostic ToDiagnostic(Diagnostic diagnostic, LanguageDiagnosticSource source, Document document)
    {
        var span = diagnostic.Location.SourceSpan;
        var lines = diagnostic.Location.GetLineSpan();
        var line = lines.StartLinePosition;
        var severity = diagnostic.Severity switch
        {
            DiagnosticSeverity.Error => LanguageDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => LanguageDiagnosticSeverity.Warning,
            DiagnosticSeverity.Info => LanguageDiagnosticSeverity.Information,
            _ => LanguageDiagnosticSeverity.Hidden
        };
        return new(diagnostic.Id, source, severity, diagnostic.GetMessage(), document.FilePath!,
            new(span.Start, span.Length), line.Line, line.Character, document.Project.Name,
            lines.EndLinePosition.Line, lines.EndLinePosition.Character);
    }
    private static LanguageResponse<T> Degraded<T>(LanguageRequest request) => new(request.Version, default, true);
}

#endif

internal sealed class LatestLanguageRequest : IDisposable
{
    private CancellationTokenSource? _pending;
    private long _sequence;
    internal Exception? LastError { get; private set; }
    internal string Status { get; private set; } = "Not started";

    internal async Task<T?> RunAsync<T>(Func<CancellationToken, Task<LanguageResponse<T>>> operation,
        long currentVersion, CancellationToken cancellationToken = default)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = _pending;
        var sequence = Interlocked.Increment(ref _sequence);
        LastError = null;
        Status = "Running";
        try
        {
            var response = await Task.Run(() => operation(pending.Token), pending.Token);
            if (sequence != Interlocked.Read(ref _sequence)) { Status = "Superseded"; return default; }
            if (response.SourceVersion != currentVersion) { Status = $"Version {response.SourceVersion}, expected {currentVersion}"; return default; }
            Status = response.IsDegraded ? "Degraded" : response.Value is null ? "No value" : "Completed";
            return response.Value;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { Status = "Cancelled"; return default; }
        catch (Exception exception) { LastError = exception; Status = "Failed"; return default; }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
    }
}
