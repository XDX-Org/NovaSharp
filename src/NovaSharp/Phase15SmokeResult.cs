namespace NovaSharp;

internal sealed record Phase15SmokeResult(bool LanguageSelected, bool ComponentCompletion,
    bool SemanticTokens, bool Diagnostics, bool Formatting, string? Error = null);
