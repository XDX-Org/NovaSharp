namespace NovaSharp;

public sealed record Phase5SmokeResult(bool GroupsPresent, bool SharedEditsImmediate,
    bool IndependentSelections, bool SplitterAccessible, bool SplitterResized,
    bool DropZonesPresent, bool EdgeDropSplit, bool NarrowLayoutOperable,
    bool EscapeCancelsDrag, bool FractionalPointerResize, bool DirectionalFocus, string? Error = null);
