namespace NovaSharp;

internal sealed record Phase7SmokeResult(bool CompletionVisible, bool CompletionKeyboardOwned,
    bool SignatureVisible, bool HoverVisible, bool SemanticTokensPresent, bool AutoIndent,
    bool CommentToggle, bool FormattingApplied, bool LoadingStateCleared, string? Error = null);
