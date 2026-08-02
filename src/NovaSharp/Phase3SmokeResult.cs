namespace NovaSharp;

public sealed record Phase3SmokeResult(
    bool TreePresent,
    bool RowsBounded,
    bool KeyboardNavigation,
    bool ContextActionsRelevant,
    bool ContextMenuInsideViewport,
    bool ContextMenuDismissed,
    bool NativeContextBypass,
    bool RenamePreservedDirtySelection,
    int RenderedRows,
    string? Error = null);
