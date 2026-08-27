namespace NovaSharp.LanguageServices;

public interface ICSharpLanguageService
{
    Task<LanguageCompletionList?> GetCompletionsAsync(LanguageRequest request, CancellationToken cancellationToken = default);
    Task<LanguageCompletionDetails?> ResolveCompletionAsync(LanguageCompletionResolveRequest request, CancellationToken cancellationToken = default);
    Task<LanguageSignatureHelp?> GetSignatureHelpAsync(LanguageRequest request, CancellationToken cancellationToken = default);
    Task<LanguageHover?> GetHoverAsync(LanguageRequest request, CancellationToken cancellationToken = default);
    Task<LanguageFormatResult?> FormatAsync(LanguageRequest request, CancellationToken cancellationToken = default);
    Task<LanguageSemanticTokens?> GetSemanticTokensAsync(LanguageRequest request, CancellationToken cancellationToken = default);
    void Cancel(string requestId);
    LanguageServiceMetrics Metrics { get; }
}
