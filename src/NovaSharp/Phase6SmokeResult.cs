namespace NovaSharp;

public sealed record Phase6SmokeResult(bool SolutionTreePresent, int ProjectNodes, bool LoadCompleted,
    bool SemanticDocumentsMapped, bool LinkedContextsPresent, bool EvaluatedTargetContexts, string? Error = null);
