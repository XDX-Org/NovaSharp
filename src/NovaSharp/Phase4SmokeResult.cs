namespace NovaSharp;

public sealed record Phase4SmokeResult(bool TabsPresent, bool PointerReordered, bool OverflowScrollable,
    bool MiddleClickClosed, bool AccessibleLabels, bool ContextCommandsPresent, string? Error = null);
