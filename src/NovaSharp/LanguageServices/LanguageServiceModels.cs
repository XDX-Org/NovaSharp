namespace NovaSharp.LanguageServices;

public sealed record LanguageRequest(
    string RequestId,
    string DocumentUri,
    string? ProjectContextId,
    long SourceVersion,
    long Sequence,
    int Position,
    int? RangeStart = null,
    int? RangeEnd = null,
    string? TriggerCharacter = null,
    bool IsExplicit = false,
    string Priority = "foreground",
    bool SuggestionsEnabled = true);

public sealed record LanguageTextEdit(int Start, int End, string Text);

public sealed record LanguageCompletionItem(
    string Id,
    string Label,
    string Kind,
    string? Detail,
    string SortText,
    string FilterText,
    string InsertText,
    IReadOnlyList<string> CommitCharacters,
    IReadOnlyList<LanguageTextEdit> AdditionalTextEdits,
    bool IsSnippet = false);

public sealed record LanguageCompletionList(
    string RequestId,
    long SourceVersion,
    long Sequence,
    bool IsIncomplete,
    IReadOnlyList<LanguageCompletionItem> Items);

public sealed record LanguageCompletionDetails(
    string RequestId,
    long SourceVersion,
    long Sequence,
    string ItemId,
    string? Detail,
    string? Documentation,
    string InsertText,
    LanguageTextEdit? TextEdit,
    IReadOnlyList<LanguageTextEdit> AdditionalTextEdits);

public sealed record LanguageCompletionResolveRequest(LanguageRequest Request, string ItemId, string? CommitCharacter = null);

public sealed record LanguageSignatureParameter(string Label, string? Documentation);

public sealed record LanguageSignature(
    string Label,
    string? Documentation,
    IReadOnlyList<LanguageSignatureParameter> Parameters);

public sealed record LanguageSignatureHelp(
    string RequestId,
    long SourceVersion,
    long Sequence,
    IReadOnlyList<LanguageSignature> Signatures,
    int ActiveSignature,
    int ActiveParameter);

public sealed record LanguageHover(
    string RequestId,
    long SourceVersion,
    long Sequence,
    int Start,
    int End,
    string Signature,
    string? Documentation,
    string? Origin);

public sealed record LanguageFormatResult(
    string RequestId,
    long SourceVersion,
    long Sequence,
    IReadOnlyList<LanguageTextEdit> Edits);

public sealed record LanguageSemanticToken(int Start, int Length, string Type, IReadOnlyList<string> Modifiers);

public sealed record LanguageSemanticTokens(
    string RequestId,
    long SourceVersion,
    long Sequence,
    string ResultId,
    IReadOnlyList<LanguageSemanticToken> Tokens);

public sealed record LanguageServiceMetrics(
    int Capacity,
    int Pending,
    int MaximumPending,
    long Completed,
    long Canceled,
    long RejectedStale,
    long Failed,
    double LastQueueDelayMilliseconds,
    double LastReplicaBarrierMilliseconds,
    double LastRoslynMilliseconds,
    double LastTotalMilliseconds);
