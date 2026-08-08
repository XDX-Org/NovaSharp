using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace NovaSharp;

public enum NavigationKind { Definition, TypeDefinition, Implementation, Reference }
public sealed record NavigationTarget(string DocumentPath, TextRange Range, int Line, int Column,
    string DisplayText, string ProjectName, NavigationKind Kind, string? Preview = null);
public sealed record SymbolEntry(string Name, string Kind, string DocumentPath, TextRange Range,
    int Line, int Column, string ProjectName, string? Container);
public sealed record WorkspaceDocumentEdit(string DocumentPath, long? ExpectedVersion, string ExpectedText,
    string NewText, DiskStamp? ExpectedDiskStamp = null, IReadOnlyList<TextRange>? ExpectedRanges = null);
public sealed record WorkspaceEdit(string Title, IReadOnlyList<WorkspaceDocumentEdit> Documents);
public sealed record CodeActionEntry(string Title, string Kind, WorkspaceEdit Edit, bool IsPreferred = false);

internal sealed partial class CSharpLanguageProvider
{
    public async Task<IReadOnlyList<NavigationTarget>> GetDefinitionsAsync(LanguageRequest request,
        bool typeDefinition, CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        var symbol = document is null ? null : await SymbolFinder.FindSymbolAtPositionAsync(document,
            request.Position, cancellationToken);
        if (symbol is null || document is null) return [];
        if (typeDefinition) symbol = SymbolType(symbol) ?? symbol;
        return await TargetsAsync(symbol, document.Project.Solution,
            typeDefinition ? NavigationKind.TypeDefinition : NavigationKind.Definition, cancellationToken);
    }

    public async Task<IReadOnlyList<NavigationTarget>> GetImplementationsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        var symbol = document is null ? null : await SymbolFinder.FindSymbolAtPositionAsync(document,
            request.Position, cancellationToken);
        if (symbol is null || document is null) return [];
        var implementations = await SymbolFinder.FindImplementationsAsync(symbol, document.Project.Solution,
            cancellationToken: cancellationToken);
        var results = new List<NavigationTarget>();
        foreach (var implementation in implementations)
            results.AddRange(await TargetsAsync(implementation, document.Project.Solution,
                NavigationKind.Implementation, cancellationToken));
        return DistinctTargets(results);
    }

    public async Task<IReadOnlyList<NavigationTarget>> FindReferencesAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        var symbol = document is null ? null : await SymbolFinder.FindSymbolAtPositionAsync(document,
            request.Position, cancellationToken);
        if (symbol is null || document is null) return [];
        var references = await SymbolFinder.FindReferencesAsync(symbol, document.Project.Solution, cancellationToken);
        var results = new List<NavigationTarget>();
        foreach (var reference in references)
            foreach (var location in reference.Locations)
                if (document.Project.Solution.GetDocument(location.Document.Id) is { FilePath: { } path } target)
                    results.Add(await TargetAsync(target, location.Location.SourceSpan, reference.Definition,
                        NavigationKind.Reference, cancellationToken));
        return DistinctTargets(results);
    }

    public async Task<IReadOnlyList<SymbolEntry>> GetDocumentSymbolsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        var root = document is null ? null : await document.GetSyntaxRootAsync(cancellationToken);
        var model = document is null ? null : await document.GetSemanticModelAsync(cancellationToken);
        if (document?.FilePath is null || root is null || model is null) return [];
        var symbols = root.DescendantNodes().Select(node => (Node: node, Symbol: model.GetDeclaredSymbol(node, cancellationToken)))
            .Where(item => item.Symbol is INamespaceSymbol or INamedTypeSymbol or IMethodSymbol or IPropertySymbol
                or IFieldSymbol or IEventSymbol).DistinctBy(item => item.Symbol, SymbolEqualityComparer.Default);
        var results = new List<SymbolEntry>();
        foreach (var (node, symbol) in symbols)
        {
            if (symbol is null) continue;
            results.Add(await SymbolAsync(document, symbol, node.Span, cancellationToken));
        }
        return results.OrderBy(item => item.Range.Start).ToArray();
    }

    public async Task<IReadOnlyList<SymbolEntry>> FindWorkspaceSymbolsAsync(string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || projectSystem.CurrentSolution is not { } solution) return [];
        var results = new List<SymbolEntry>();
        foreach (var project in solution.Projects)
            foreach (var symbol in await SymbolFinder.FindDeclarationsAsync(project, query, true,
                         SymbolFilter.TypeAndMember, cancellationToken))
                foreach (var location in symbol.Locations.Where(item => item.IsInSource))
                    if (solution.GetDocument(location.SourceTree) is { FilePath: not null } document)
                        results.Add(await SymbolAsync(document, symbol, location.SourceSpan, cancellationToken));
        return results.Take(500).ToArray();
    }

    public async Task<WorkspaceEdit?> RenameAsync(LanguageRequest request, string newName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(newName)) return null;
        var document = await ResolveAsync(request, cancellationToken);
        var symbol = document is null ? null : await SymbolFinder.FindSymbolAtPositionAsync(document,
            request.Position, cancellationToken);
        if (symbol is null || document is null) return null;
        var oldSolution = document.Project.Solution;
        var renamed = await Renamer.RenameSymbolAsync(oldSolution, symbol, new SymbolRenameOptions(),
            newName, cancellationToken);
        var edits = new List<WorkspaceDocumentEdit>();
        foreach (var projectChange in renamed.GetChanges(oldSolution).GetProjectChanges())
            foreach (var id in projectChange.GetChangedDocuments())
            {
                var before = oldSolution.GetDocument(id);
                var after = renamed.GetDocument(id);
                if (before?.FilePath is null || after is null) continue;
                var oldText = (await before.GetTextAsync(cancellationToken)).ToString();
                var newText = (await after.GetTextAsync(cancellationToken)).ToString();
                var version = projectSystem.GetTrackedSnapshot(before.FilePath)?.Version;
                edits.Add(new(before.FilePath, version, oldText, newText));
            }
        return new($"Rename {symbol.Name} to {newName}", edits.DistinctBy(item => item.DocumentPath,
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<CodeActionEntry>> GetCodeActionsAsync(LanguageRequest request,
        CancellationToken cancellationToken)
    {
        var document = await ResolveAsync(request, cancellationToken);
        if (document?.FilePath is null) return [];
        var oldText = (await document.GetTextAsync(cancellationToken)).ToString();
        var version = projectSystem.GetTrackedSnapshot(document.FilePath)?.Version;
        var actions = new List<CodeActionEntry>();
        var formatted = await Formatter.FormatAsync(document, cancellationToken: cancellationToken);
        var formattedText = (await formatted.GetTextAsync(cancellationToken)).ToString();
        if (formattedText != oldText)
            actions.Add(new("Format document", "source.format", new("Format document",
                [new(document.FilePath, version, oldText, formattedText)])));

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        var compilation = await document.Project.GetCompilationAsync(cancellationToken);
        if (root is not CompilationUnitSyntax unit || compilation is null) return actions;
        var diagnostics = compilation.GetDiagnostics(cancellationToken)
            .Where(item => item.Location.SourceTree == root.SyntaxTree).ToArray();
        var unnecessary = diagnostics.Where(item => item.Id == "CS8019").Select(item =>
            root.FindNode(item.Location.SourceSpan).FirstAncestorOrSelf<UsingDirectiveSyntax>()).Where(item => item is not null)
            .Distinct().ToArray();
        var currentUsing = unnecessary.FirstOrDefault(item => item!.Span.Contains(request.Position));
        if (currentUsing is not null)
            actions.Add(RemoveUsings(document.FilePath, version, oldText, unit, [currentUsing], "Remove unnecessary using"));
        if (unnecessary.Length > 1)
            actions.Add(RemoveUsings(document.FilePath, version, oldText, unit, unnecessary!,
                "Fix all unnecessary usings in document") with { Kind = "source.fixAll" });

        var missing = diagnostics.FirstOrDefault(item => item.Id == "CS0246" && item.Location.SourceSpan.Contains(request.Position));
        if (missing is not null)
        {
            var name = root.FindToken(missing.Location.SourceSpan.Start).ValueText;
            foreach (var project in document.Project.Solution.Projects)
                foreach (var symbol in (await SymbolFinder.FindDeclarationsAsync(project, name, false,
                             SymbolFilter.Type, cancellationToken)).OfType<INamedTypeSymbol>())
                {
                    var ns = symbol.ContainingNamespace?.ToDisplayString();
                    if (string.IsNullOrWhiteSpace(ns) || unit.Usings.Any(item => item.Name?.ToString() == ns)) continue;
                    var changed = unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(ns))
                        .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed));
                    actions.Add(new($"using {ns};", "quickfix", new($"Add using {ns}",
                        [new(document.FilePath, version, oldText, changed.ToFullString())]), true));
                }
        }
        return actions.DistinctBy(item => item.Title).ToArray();
    }

    private static CodeActionEntry RemoveUsings(string path, long? version, string oldText,
        CompilationUnitSyntax root, IEnumerable<UsingDirectiveSyntax> usings, string title)
    {
        var changed = root.RemoveNodes(usings, SyntaxRemoveOptions.KeepNoTrivia)!;
        return new(title, "quickfix", new(title, [new(path, version, oldText, changed.ToFullString())]), true);
    }

    private async Task<IReadOnlyList<NavigationTarget>> TargetsAsync(ISymbol symbol, Solution solution,
        NavigationKind kind, CancellationToken cancellationToken)
    {
        var results = new List<NavigationTarget>();
        foreach (var location in symbol.Locations.Where(item => item.IsInSource))
            if (solution.GetDocument(location.SourceTree) is { FilePath: not null } document)
                results.Add(await TargetAsync(document, location.SourceSpan, symbol, kind, cancellationToken));
        return results;
    }

    private static async Task<NavigationTarget> TargetAsync(Document document, Microsoft.CodeAnalysis.Text.TextSpan span,
        ISymbol symbol, NavigationKind kind, CancellationToken cancellationToken)
    {
        var text = await document.GetTextAsync(cancellationToken);
        var line = text.Lines.GetLinePosition(span.Start);
        var preview = text.Lines[line.Line].ToString().Trim();
        return new(document.FilePath!, new(span.Start, span.Length), line.Line, line.Character,
            symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), document.Project.Name, kind, preview);
    }

    private static async Task<SymbolEntry> SymbolAsync(Document document, ISymbol symbol,
        Microsoft.CodeAnalysis.Text.TextSpan span, CancellationToken cancellationToken)
    {
        var line = (await document.GetTextAsync(cancellationToken)).Lines.GetLinePosition(span.Start);
        return new(symbol.Name, symbol.Kind.ToString(), document.FilePath!, new(span.Start, span.Length),
            line.Line, line.Character, document.Project.Name,
            symbol.ContainingSymbol?.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
    }

    private static ISymbol? SymbolType(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => local.Type, IFieldSymbol field => field.Type, IPropertySymbol property => property.Type,
        IParameterSymbol parameter => parameter.Type, IMethodSymbol method => method.ReturnType,
        IAliasSymbol alias => alias.Target, _ => symbol as INamedTypeSymbol
    };

    private static IReadOnlyList<NavigationTarget> DistinctTargets(IEnumerable<NavigationTarget> targets) => targets
        .DistinctBy(item => (item.DocumentPath, item.Range.Start, item.Range.Length)).ToArray();
}
