namespace NovaSharp;

public sealed record Phase3SmokeResult(
    bool TreePresent,
    bool RowsBounded,
    bool KeyboardNavigation,
    bool ContextActionsRelevant,
    bool ContextMenuInsideViewport,
    bool ContextMenuDismissed,
    bool RenamePreservedDirtySelection,
    int RenderedRows);
