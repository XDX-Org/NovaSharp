using System.Collections.Immutable;
using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;
using NovaSharp.Async;
using NovaSharp.Solutions;

namespace NovaSharp.LanguageServices;

public sealed class CSharpLanguageService : ICSharpLanguageService, IAsyncDisposable
{
    private sealed record CompletionStamp(
        DocumentId DocumentId,
        ProjectId ProjectId,
        string DocumentUri,
        long SourceVersion,
        long Sequence,
        long ReplicaVersion);

    private sealed record CachedCompletion(CompletionStamp Stamp, CompletionItem Item);
    private sealed record CompletionListKey(
        string DocumentUri,
        string? ProjectContextId,
        long SourceVersion,
        long Sequence,
        int Position,
        string? TriggerCharacter,
        bool IsExplicit);
    private sealed record CachedCompletionList(
        CompletionStamp Stamp,
        ImmutableArray<CompletionItem> Items,
        bool IsIncomplete);
    private sealed record CompletionWarmupKey(
        string DocumentUri,
        string ProjectContextId,
        long SourceVersion,
        long Sequence);

    private const int CompletionCacheCapacity = 512;
    private const int CompletionListCacheCapacity = 16;
    private const int MaximumCompletionItems = 500;
    private const int MaximumSemanticTokens = 20_000;
    private readonly SolutionWorkspaceService _solutions;
    private readonly BoundedWorkQueue _work;
    private readonly SemaphoreSlim _admission;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CancellationTokenSource> _latest = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedCompletion> _completionCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _completionOrder = new();
    private readonly Dictionary<CompletionListKey, CachedCompletionList> _completionListCache = [];
    private readonly Queue<CompletionListKey> _completionListOrder = new();
    private readonly Dictionary<CompletionWarmupKey, Task> _completionWarmups = [];
    private readonly Queue<CompletionWarmupKey> _completionWarmupOrder = new();
    private int _pending;
    private int _maximumPending;
    private long _completed;
    private long _canceled;
    private long _rejectedStale;
    private long _failed;
    private long _completionListCacheHits;
    private long _completionWarmupFailures;
    private long _lastQueueDelayTicks;
    private long _lastReplicaBarrierTicks;
    private long _lastRoslynTicks;
    private long _lastTotalTicks;
    private int _disposed;

    public CSharpLanguageService(SolutionWorkspaceService solutions, int capacity = 64, int workerCount = 2)
    {
        ArgumentNullException.ThrowIfNull(solutions);
        _solutions = solutions;
        _work = new BoundedWorkQueue(capacity, workerCount);
        _admission = new SemaphoreSlim(_work.TotalCapacity, _work.TotalCapacity);
    }

    public LanguageServiceMetrics Metrics
    {
        get
        {
            int completionListCacheEntries;
            lock (_gate) completionListCacheEntries = _completionListCache.Count;
            return new(
                _work.TotalCapacity,
                Volatile.Read(ref _pending),
                Volatile.Read(ref _maximumPending),
                Interlocked.Read(ref _completed),
                Interlocked.Read(ref _canceled),
                Interlocked.Read(ref _rejectedStale),
                Interlocked.Read(ref _failed),
                TimeSpan.FromTicks(Interlocked.Read(ref _lastQueueDelayTicks)).TotalMilliseconds,
                TimeSpan.FromTicks(Interlocked.Read(ref _lastReplicaBarrierTicks)).TotalMilliseconds,
                TimeSpan.FromTicks(Interlocked.Read(ref _lastRoslynTicks)).TotalMilliseconds,
                TimeSpan.FromTicks(Interlocked.Read(ref _lastTotalTicks)).TotalMilliseconds,
                Interlocked.Read(ref _completionListCacheHits),
                completionListCacheEntries,
                Interlocked.Read(ref _completionWarmupFailures));
        }
    }

    public Task<LanguageCompletionList?> GetCompletionsAsync(LanguageRequest request, CancellationToken cancellationToken = default) =>
        RunLatestAsync(request, "completion", async (snapshot, document, token) =>
        {
            var key = CompletionKey(request);
            CachedCompletionList? cached;
            lock (_gate) _completionListCache.TryGetValue(key, out cached);
            if (cached is not null && Matches(cached.Stamp, snapshot))
            {
                Interlocked.Increment(ref _completionListCacheHits);
                return CreateCompletionList(request, cached);
            }

            var service = CompletionService.GetService(document);
            if (service is null) return null;
            var trigger = request.IsExplicit || string.IsNullOrEmpty(request.TriggerCharacter)
                ? CompletionTrigger.Invoke
                : CompletionTrigger.CreateInsertionTrigger(request.TriggerCharacter[0]);
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            var position = ClampPosition(request.Position, text);
            var list = await service.GetCompletionsAsync(
                document,
                position,
                trigger: trigger,
                cancellationToken: token).ConfigureAwait(false);
            if (list is null) return new LanguageCompletionList(request.RequestId, request.SourceVersion, request.Sequence, false, []);

            var (items, isIncomplete) = RankCompletionItems(service, document, list, text, position);
            cached = new(
                Stamp(snapshot),
                items,
                isIncomplete);
            CacheCompletionList(key, cached);
            return CreateCompletionList(request, cached);
        }, cancellationToken);

    public Task<LanguageCompletionDetails?> ResolveCompletionAsync(
        LanguageCompletionResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        CachedCompletion? cached;
        lock (_gate) _completionCache.TryGetValue(request.ItemId, out cached);
        if (cached is null) return Task.FromResult<LanguageCompletionDetails?>(null);

        return RunLatestAsync(request.Request, "completion-resolve", async (snapshot, document, token) =>
        {
            if (!Matches(cached.Stamp, snapshot))
            {
                return null;
            }
            var service = CompletionService.GetService(document);
            if (service is null) return null;
            var description = await service.GetDescriptionAsync(document, cached.Item, token).ConfigureAwait(false);
            char? commit = string.IsNullOrEmpty(request.CommitCharacter) ? null : request.CommitCharacter[0];
            var change = await service.GetChangeAsync(document, cached.Item, commit, token).ConfigureAwait(false);
            var changes = change.TextChanges.IsDefaultOrEmpty ? [change.TextChange] : change.TextChanges;
            var primary = changes.FirstOrDefault(textChange =>
                textChange.Span.IntersectsWith(cached.Item.Span) || textChange.Span == cached.Item.Span);
            if (primary == default) primary = change.TextChange;
            var edits = changes.Where(textChange => textChange != primary).Select(ToEdit).ToArray();
            return new LanguageCompletionDetails(
                request.Request.RequestId,
                request.Request.SourceVersion,
                request.Request.Sequence,
                request.ItemId,
                cached.Item.InlineDescription,
                description is null ? null : string.Concat(description.TaggedParts.Select(part => part.Text)),
                primary.NewText ?? cached.Item.DisplayText,
                ToEdit(primary),
                edits);
        }, cancellationToken);
    }

    public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(LanguageRequest request, CancellationToken cancellationToken = default) =>
        RunLatestAsync(request, "signature", async (_, document, token) =>
        {
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            var position = ClampPosition(request.Position, text);
            var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
            if (root is null || model is null) return null;

            var argumentList = root.DescendantNodes(descendIntoTrivia: false)
                .OfType<BaseArgumentListSyntax>()
                .Where(list => list.SpanStart <= position && position <= list.FullSpan.End)
                .OrderBy(list => list.Span.Length)
                .FirstOrDefault();
            if (argumentList is null) return null;

            var expression = argumentList.Parent switch
            {
                InvocationExpressionSyntax invocation => invocation.Expression,
                ObjectCreationExpressionSyntax creation => creation.Type,
                ConstructorInitializerSyntax initializer => initializer,
                ElementAccessExpressionSyntax access => access.Expression,
                _ => argumentList.Parent,
            };
            if (expression is null) return null;
            var callInfo = model.GetSymbolInfo(argumentList.Parent!, token);
            var expressionInfo = model.GetSymbolInfo(expression, token);
            var selectedSymbol = callInfo.Symbol ?? expressionInfo.Symbol;
            IEnumerable<ISymbol> symbols = argumentList.Parent is ObjectCreationExpressionSyntax
                && selectedSymbol is IMethodSymbol constructor
                    ? constructor.ContainingType.InstanceConstructors
                    : model.GetMemberGroup(expression, token);
            if (!symbols.Any())
                symbols = callInfo.CandidateSymbols.IsDefaultOrEmpty ? expressionInfo.CandidateSymbols : callInfo.CandidateSymbols;
            if (!symbols.Any() && selectedSymbol is not null) symbols = [selectedSymbol];
            var callables = symbols
                .Where(symbol => symbol is IMethodSymbol or IPropertySymbol { IsIndexer: true })
                .Distinct(SymbolEqualityComparer.Default)
                .ToArray();
            if (callables.Length == 0) return null;

            var activeParameter = argumentList.Arguments.Count(argument => argument.Span.End < position);
            var signatures = callables.Select(method => new LanguageSignature(
                method.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Documentation(method),
                Parameters(method).Select(parameter => new LanguageSignatureParameter(
                    parameter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Documentation(parameter))).ToArray())).ToArray();
            var selected = Math.Max(0, Array.FindIndex(callables, method => SymbolEqualityComparer.Default.Equals(method, selectedSymbol)));
            return new LanguageSignatureHelp(request.RequestId, request.SourceVersion, request.Sequence, signatures, selected, activeParameter);
        }, cancellationToken);

    public Task<LanguageHover?> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken = default) =>
        RunLatestAsync(request, "hover", async (snapshot, document, token) =>
        {
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            var position = ClampPosition(request.Position, text);
            var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
            if (root is null || model is null) return null;
            var syntaxToken = root.FindToken(position);
            var node = syntaxToken.Parent;
            if (node is null) return null;
            var symbol = model.GetSymbolInfo(node, token).Symbol ?? model.GetDeclaredSymbol(node, token);
            if (symbol is null) return null;
            var origin = symbol.ContainingAssembly?.Name is { Length: > 0 } assembly
                ? $"{snapshot.ProjectName} · {assembly}"
                : snapshot.ProjectName;
            return new LanguageHover(
                request.RequestId,
                request.SourceVersion,
                request.Sequence,
                syntaxToken.SpanStart,
                syntaxToken.Span.End,
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                Documentation(symbol),
                origin);
        }, cancellationToken);

    public Task<LanguageFormatResult?> FormatAsync(LanguageRequest request, CancellationToken cancellationToken = default) =>
        RunLatestAsync(request, "format", async (_, document, token) =>
        {
            var oldText = await document.GetTextAsync(token).ConfigureAwait(false);
            var span = request.RangeStart is { } start && request.RangeEnd is { } end
                ? TextSpan.FromBounds(ClampPosition(start, oldText), ClampPosition(end, oldText))
                : new TextSpan(0, oldText.Length);
            var formatted = await Formatter.FormatAsync(document, span, cancellationToken: token).ConfigureAwait(false);
            var changes = await formatted.GetTextChangesAsync(document, token).ConfigureAwait(false);
            return new LanguageFormatResult(request.RequestId, request.SourceVersion, request.Sequence, changes.Select(ToEdit).ToArray());
        }, cancellationToken);

    public Task<LanguageSemanticTokens?> GetSemanticTokensAsync(LanguageRequest request, CancellationToken cancellationToken = default) =>
        RunLatestAsync(request, "semantic", async (_, document, token) =>
        {
            var text = await document.GetTextAsync(token).ConfigureAwait(false);
            var root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            var model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
            if (root is null || model is null) return null;
            var start = ClampPosition(request.RangeStart ?? 0, text);
            var end = ClampPosition(request.RangeEnd ?? text.Length, text);
            var span = TextSpan.FromBounds(Math.Min(start, end), Math.Max(start, end));
            var result = new List<LanguageSemanticToken>();
            foreach (var syntaxToken in root.DescendantTokens(span, descendIntoTrivia: false))
            {
                token.ThrowIfCancellationRequested();
                if (result.Count == MaximumSemanticTokens) break;
                if (!syntaxToken.IsKind(SyntaxKind.IdentifierToken)) continue;
                var symbol = syntaxToken.Parent is { } node
                    ? model.GetSymbolInfo(node, token).Symbol ?? model.GetDeclaredSymbol(node, token)
                    : null;
                var type = SemanticType(symbol);
                if (type is null || syntaxToken.Span.Length == 0) continue;
                var modifiers = new List<string>(2);
                if (symbol?.IsStatic == true) modifiers.Add("static");
                if (symbol is IFieldSymbol { IsReadOnly: true }) modifiers.Add("readonly");
                if (syntaxToken.Parent is { } parent && model.GetDeclaredSymbol(parent, token) is not null) modifiers.Add("declaration");
                result.Add(new(syntaxToken.SpanStart, syntaxToken.Span.Length, type, modifiers));
            }
            return new LanguageSemanticTokens(
                request.RequestId,
                request.SourceVersion,
                request.Sequence,
                $"{request.SourceVersion}:{request.Sequence}",
                result);
        }, cancellationToken, foreground: false);

    public void Cancel(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        CancellationTokenSource? source;
        lock (_gate)
        {
            _requests.TryGetValue(requestId, out source);
        }
        source?.Cancel();
    }

    private async Task<T?> RunLatestAsync<T>(
        LanguageRequest request,
        string capability,
        Func<RoslynLanguageSnapshot, Document, CancellationToken, Task<T?>> action,
        CancellationToken cancellationToken,
        bool foreground = true) where T : class
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var totalStarted = Stopwatch.GetTimestamp();
        if (!Uri.TryCreate(request.DocumentUri, UriKind.Absolute, out var uri)) return null;
        var key = $"{request.DocumentUri}\n{capability}";
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _latest.Remove(key, out previous);
            _latest[key] = source;
            _requests[request.RequestId] = source;
        }
        previous?.Cancel();
        var admitted = false;
        try
        {
            await _admission.WaitAsync(source.Token).ConfigureAwait(false);
            admitted = true;
            var pending = Interlocked.Increment(ref _pending);
            UpdateMaximum(ref _maximumPending, pending);
            var queuedAt = Stopwatch.GetTimestamp();
            async Task<T?> ExecuteAsync(CancellationToken token)
            {
                Interlocked.Exchange(ref _lastQueueDelayTicks, Stopwatch.GetElapsedTime(queuedAt).Ticks);
                var barrierStarted = Stopwatch.GetTimestamp();
                var snapshot = await _solutions.GetLanguageSnapshotAsync(
                    uri, request.ProjectContextId, request.SourceVersion, request.Sequence, token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastReplicaBarrierTicks, Stopwatch.GetElapsedTime(barrierStarted).Ticks);
                if (snapshot is null)
                {
                    Interlocked.Increment(ref _rejectedStale);
                    return null;
                }
                var document = snapshot.Solution.GetDocument(snapshot.DocumentId);
                if (document is null) return null;
                var roslynStarted = Stopwatch.GetTimestamp();
                var result = await action(snapshot, document, token).ConfigureAwait(false);
                Interlocked.Exchange(ref _lastRoslynTicks, Stopwatch.GetElapsedTime(roslynStarted).Ticks);
                if (!_solutions.IsLanguageSnapshotCurrent(snapshot))
                {
                    Interlocked.Increment(ref _rejectedStale);
                    return null;
                }
                Interlocked.Increment(ref _completed);
                return result;
            }
            return await (foreground
                ? _work.EnqueueForegroundAsync(ExecuteAsync, source.Token)
                : _work.EnqueueAsync(ExecuteAsync, source.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            Interlocked.Increment(ref _canceled);
            return null;
        }
        catch
        {
            Interlocked.Increment(ref _failed);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _lastTotalTicks, Stopwatch.GetElapsedTime(totalStarted).Ticks);
            if (admitted)
            {
                Interlocked.Decrement(ref _pending);
                _admission.Release();
            }
            lock (_gate)
            {
                if (_latest.GetValueOrDefault(key) == source) _latest.Remove(key);
                if (_requests.GetValueOrDefault(request.RequestId) == source) _requests.Remove(request.RequestId);
            }
            source.Dispose();
        }
    }

    public Task WarmCompletionAsync(
        Uri documentUri,
        string projectContextId,
        long sourceVersion,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectContextId);
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
        var key = new CompletionWarmupKey(documentUri.AbsoluteUri, projectContextId, sourceVersion, sequence);
        lock (_gate)
        {
            if (_completionWarmups.TryGetValue(key, out var existing)) return existing;
            var task = WarmCompletionCoreAsync(key, cancellationToken);
            _completionWarmups[key] = task;
            _completionWarmupOrder.Enqueue(key);
            while (_completionWarmupOrder.Count > CompletionListCacheCapacity)
                _completionWarmups.Remove(_completionWarmupOrder.Dequeue());
            return task;
        }
    }

    private async Task WarmCompletionCoreAsync(CompletionWarmupKey key, CancellationToken cancellationToken)
    {
        try
        {
            await _work.EnqueueAsync(async token =>
            {
                var snapshot = await _solutions.GetLanguageSnapshotAsync(
                    new Uri(key.DocumentUri), key.ProjectContextId, key.SourceVersion, key.Sequence, token).ConfigureAwait(false);
                var document = snapshot?.Solution.GetDocument(snapshot.DocumentId);
                if (document is null || CompletionService.GetService(document) is not { } service) return false;
                var text = await document.GetTextAsync(token).ConfigureAwait(false);
                _ = await service.GetCompletionsAsync(
                    document,
                    text.Length,
                    trigger: CompletionTrigger.Invoke,
                    cancellationToken: token).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
        }
        catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) != 0)
        {
        }
        catch
        {
            Interlocked.Increment(ref _completionWarmupFailures);
        }
    }

    private void CacheCompletionList(CompletionListKey key, CachedCompletionList completion)
    {
        lock (_gate)
        {
            if (_completionListCache.ContainsKey(key))
            {
                _completionListCache[key] = completion;
                return;
            }
            _completionListCache[key] = completion;
            _completionListOrder.Enqueue(key);
            while (_completionListOrder.Count > CompletionListCacheCapacity)
                _completionListCache.Remove(_completionListOrder.Dequeue());
        }
    }

    private LanguageCompletionList CreateCompletionList(LanguageRequest request, CachedCompletionList completion)
    {
        var cacheEntries = new List<KeyValuePair<string, CachedCompletion>>(completion.Items.Length);
        var items = completion.Items.Select(item =>
        {
            var id = $"{request.RequestId}:{Guid.NewGuid():N}";
            cacheEntries.Add(new(id, new(completion.Stamp, item)));
            return new LanguageCompletionItem(
                id,
                item.DisplayText,
                CompletionKind(item.Tags),
                item.InlineDescription,
                item.SortText,
                item.FilterText,
                item.DisplayText,
                CommitCharacters(item),
                [],
                Preselect: item.Rules.MatchPriority >= MatchPriority.Preselect);
        }).ToList();
        CacheCompletions(cacheEntries);
        items.AddRange(CSharpSnippets());
        return new LanguageCompletionList(
            request.RequestId,
            request.SourceVersion,
            request.Sequence,
            completion.IsIncomplete,
            items);
    }

    private void CacheCompletions(IReadOnlyList<KeyValuePair<string, CachedCompletion>> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                _completionCache[entry.Key] = entry.Value;
                _completionOrder.Enqueue(entry.Key);
            }
            while (_completionOrder.Count > CompletionCacheCapacity)
                _completionCache.Remove(_completionOrder.Dequeue());
        }
    }

    private static CompletionListKey CompletionKey(LanguageRequest request) => new(
        request.DocumentUri,
        request.ProjectContextId,
        request.SourceVersion,
        request.Sequence,
        request.Position,
        request.TriggerCharacter,
        request.IsExplicit);

    private static CompletionStamp Stamp(RoslynLanguageSnapshot snapshot) => new(
        snapshot.DocumentId,
        snapshot.ProjectId,
        snapshot.DocumentUri,
        snapshot.SourceVersion,
        snapshot.Sequence,
        snapshot.ReplicaVersion);

    private static bool Matches(CompletionStamp stamp, RoslynLanguageSnapshot snapshot) =>
        stamp.DocumentId == snapshot.DocumentId
        && stamp.ProjectId == snapshot.ProjectId
        && string.Equals(stamp.DocumentUri, snapshot.DocumentUri, StringComparison.Ordinal)
        && stamp.SourceVersion == snapshot.SourceVersion
        && stamp.Sequence == snapshot.Sequence
        && stamp.ReplicaVersion == snapshot.ReplicaVersion;

    private static int ClampPosition(int position, SourceText text) => Math.Clamp(position, 0, text.Length);
    private static LanguageTextEdit ToEdit(TextChange change) => new(change.Span.Start, change.Span.End, change.NewText ?? string.Empty);

    private static (ImmutableArray<CompletionItem> Items, bool IsIncomplete) RankCompletionItems(
        CompletionService service,
        Document document,
        CompletionList list,
        SourceText text,
        int position)
    {
        var allItems = list.ItemsList;
        if (allItems.Count <= MaximumCompletionItems)
        {
            return ([.. allItems], false);
        }

        var filterStart = Math.Clamp(list.Span.Start, 0, position);
        var filterText = text.ToString(TextSpan.FromBounds(filterStart, position));
        if (filterText.Length == 0)
        {
            return ([.. allItems.Take(MaximumCompletionItems)], true);
        }

        var bestMatches = service.FilterItems(document, [.. allItems], filterText);
        var prefixMatches = allItems.Where(item => MatchesPrefix(item, filterText)).ToArray();
        var ranked = new List<CompletionItem>(MaximumCompletionItems);
        var included = new HashSet<CompletionItem>(ReferenceEqualityComparer.Instance);
        var matchingItemCount = 0;

        AddMatches(allItems.Where(item => item.Rules.MatchPriority >= MatchPriority.Preselect));
        AddMatches(bestMatches);
        AddMatches(prefixMatches);

        return ([.. ranked], matchingItemCount > MaximumCompletionItems);

        void AddMatches(IEnumerable<CompletionItem> items)
        {
            foreach (var item in items)
            {
                if (!included.Add(item)) continue;
                matchingItemCount++;
                if (ranked.Count < MaximumCompletionItems) ranked.Add(item);
            }
        }
    }

    private static bool MatchesPrefix(CompletionItem item, string filterText) =>
        item.FilterText.StartsWith(filterText, StringComparison.OrdinalIgnoreCase);

    private static string CompletionKind(ImmutableArray<string> tags)
    {
        if (tags.Contains(WellKnownTags.Method) || tags.Contains(WellKnownTags.ExtensionMethod)) return "method";
        if (tags.Contains(WellKnownTags.Property)) return "property";
        if (tags.Contains(WellKnownTags.Local) || tags.Contains(WellKnownTags.Parameter)) return "variable";
        if (tags.Contains(WellKnownTags.Constant)) return "constant";
        if (tags.Contains(WellKnownTags.EnumMember)) return "enumMember";
        if (tags.Contains(WellKnownTags.Field)) return "field";
        if (tags.Contains(WellKnownTags.Event)) return "event";
        if (tags.Contains(WellKnownTags.Delegate)) return "function";
        if (tags.Contains(WellKnownTags.Class)) return "class";
        if (tags.Contains(WellKnownTags.Structure)) return "struct";
        if (tags.Contains(WellKnownTags.Interface)) return "interface";
        if (tags.Contains(WellKnownTags.Enum)) return "enum";
        if (tags.Contains(WellKnownTags.TypeParameter)) return "typeParameter";
        if (tags.Contains(WellKnownTags.Namespace)) return "module";
        if (tags.Contains(WellKnownTags.Keyword)) return "keyword";
        return "text";
    }

    private static IReadOnlyList<string> CommitCharacters(CompletionItem item)
    {
        var characters = new HashSet<char>(['.', ';', '(', ')', '[', ']', ' ', '=']);
        foreach (var rule in item.Rules.CommitCharacterRules)
        {
            if (rule.Kind == CharacterSetModificationKind.Replace) characters.Clear();
            if (rule.Kind == CharacterSetModificationKind.Remove)
            {
                characters.ExceptWith(rule.Characters);
            }
            else
            {
                characters.UnionWith(rule.Characters);
            }
        }
        return characters.Select(character => character.ToString()).ToArray();
    }

    private static IReadOnlyList<LanguageCompletionItem> CSharpSnippets() =>
    [
        new("snippet:if", "if", "snippet", "if statement", "zz-if", "if", "if (${1:condition})\n{\n\t$0\n}", [], [], true),
        new("snippet:for", "for", "snippet", "for loop", "zz-for", "for", "for (var ${1:i} = 0; ${1:i} < ${2:length}; ${1:i}++)\n{\n\t$0\n}", [], [], true),
        new("snippet:foreach", "foreach", "snippet", "foreach loop", "zz-foreach", "foreach", "foreach (var ${1:item} in ${2:items})\n{\n\t$0\n}", [], [], true),
        new("snippet:prop", "prop", "snippet", "auto property", "zz-prop", "prop", "public ${1:string} ${2:Name} { get; set; }$0", [], [], true),
    ];

    private static string? SemanticType(ISymbol? symbol) => symbol switch
    {
        INamespaceSymbol => "namespace",
        INamedTypeSymbol { TypeKind: TypeKind.Interface } => "interface",
        INamedTypeSymbol { TypeKind: TypeKind.Enum } => "enum",
        INamedTypeSymbol { TypeKind: TypeKind.Struct } => "struct",
        INamedTypeSymbol => "class",
        IMethodSymbol => "method",
        IPropertySymbol => "property",
        IEventSymbol => "event",
        IFieldSymbol => "field",
        IParameterSymbol => "parameter",
        ILocalSymbol => "variable",
        ITypeParameterSymbol => "typeParameter",
        _ => null,
    };

    private static ImmutableArray<IParameterSymbol> Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol property => property.Parameters,
        _ => [],
    };

    private static string? Documentation(ISymbol symbol)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: default, expandIncludes: true);
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var root = XElement.Parse(xml);
            return string.Join(" ", root.DescendantNodes().OfType<XText>()
                .Select(text => text.Value.Trim()).Where(value => value.Length > 0));
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        CancellationTokenSource[] sources;
        lock (_gate)
        {
            sources = _requests.Values.Distinct().ToArray();
            _requests.Clear();
            _latest.Clear();
            _completionCache.Clear();
            _completionOrder.Clear();
            _completionListCache.Clear();
            _completionListOrder.Clear();
            _completionWarmups.Clear();
            _completionWarmupOrder.Clear();
        }
        foreach (var source in sources) source.Cancel();
        await _work.DisposeAsync().ConfigureAwait(false);
        for (var permit = 0; permit < _work.TotalCapacity; permit++)
            await _admission.WaitAsync().ConfigureAwait(false);
        _admission.Dispose();
    }
}
