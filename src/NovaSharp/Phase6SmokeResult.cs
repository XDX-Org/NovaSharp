namespace NovaSharp;

public sealed record Phase6SmokeResult(bool SolutionTreePresent, int ProjectNodes, bool LoadCompleted,
    bool SemanticDocumentsMapped, bool LinkedContextsPresent, bool EvaluatedTargetContexts,
    bool ProjectFileEditable, bool ContextMenuPresent, bool DragSourcePresent, string? Error = null);
