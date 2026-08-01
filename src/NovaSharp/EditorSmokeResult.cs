namespace NovaSharp;

internal sealed record EditorSmokeResult(
    bool InputPresent,
    bool SelectionReplacement,
    bool BracketPairing,
    bool TabInsertion,
    bool CompositionCommittedOnce,
    bool RowsBounded,
    int RenderedRows);
