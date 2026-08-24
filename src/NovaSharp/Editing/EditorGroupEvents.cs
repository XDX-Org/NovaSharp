namespace NovaSharp.Editing;

public sealed record EditorGroupTabEvent(string GroupId, string ViewId);
public sealed record EditorGroupDropEvent(string TargetGroupId, int TargetIndex, bool Copy);
public sealed record EditorGroupEdgeDropEvent(string TargetGroupId, EditorSplitDirection Direction, bool Copy);
public sealed record EditorSplitResizeEvent(string SplitId, double Ratio);
