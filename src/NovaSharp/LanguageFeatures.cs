using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Text;

namespace NovaSharp;

public sealed record LanguageRequest(string DocumentId, string? ProjectContext, long Version, int Position,
    TextRange? Range = null);
public sealed record LanguageResponse<T>(long SourceVersion, T? Value, bool IsDegraded = false);
public sealed record CompletionEntry(string Id, string DisplayText, string FilterText, string SortText,
    string Kind, TextRange Replacement, IReadOnlyList<char> CommitCharacters, bool IsSnippet, string? Detail = null);
public sealed record CompletionResult(IReadOnlyList<CompletionEntry> Items);
public sealed record CompletionEdit(TextRange Replacement, string NewText, int? NewPosition);
public sealed record SignatureResult(IReadOnlyList<string> Signatures, int ActiveSignature, int ActiveParameter);
public sealed record HoverResult(TextRange Range, IReadOnlyList<string> Sections);
public sealed record SemanticSpan(int Start, int Length, string Classification);
public sealed record FormatResult(string Text, int SelectionStart, int SelectionLength);

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
    Task<LanguageResponse<FormatResult>> FormatAsync(LanguageRequest request, CancellationToken cancellationToken);
}

internal sealed class CSharpLanguageProvider(RoslynProjectSystem projectSystem) : ILanguageProvider
{
    private readonly Dictionary<string, (long Version, Document Document, CompletionItem Item)> _completionItems = [];
    private long _nextCompletionId;

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
    private static LanguageResponse<T> Degraded<T>(LanguageRequest request) => new(request.Version, default, true);
}

internal sealed class LatestLanguageRequest : IDisposable
{
    private CancellationTokenSource? _pending;
    private long _sequence;

    internal async Task<T?> RunAsync<T>(Func<CancellationToken, Task<LanguageResponse<T>>> operation,
        long currentVersion, CancellationToken cancellationToken = default)
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = _pending;
        var sequence = Interlocked.Increment(ref _sequence);
        try
        {
            var response = await operation(pending.Token);
            return sequence == Interlocked.Read(ref _sequence) && response.SourceVersion == currentVersion
                ? response.Value : default;
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { return default; }
        catch (Exception) { return default; }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
    }
}
