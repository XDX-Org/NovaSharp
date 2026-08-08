namespace NovaSharp;

internal sealed record Phase9SmokeResult(bool QuickOpenVisible, bool DuplicateFilesVisible,
    bool SearchVisible, bool ResultsStreamed, bool ReplacePreview, string? Error = null);
