namespace NovaSharp;

public sealed record Phase5SmokeResult(bool GroupsPresent, bool SharedEditsImmediate,
    bool IndependentSelections, bool SplitterAccessible, bool SplitterResized,
    bool DropZonesPresent, bool EdgeDropSplit, bool NarrowLayoutOperable, string? Error = null);
