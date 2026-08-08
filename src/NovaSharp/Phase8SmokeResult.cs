namespace NovaSharp;

internal sealed record Phase8SmokeResult(bool DiagnosticSquiggle, bool DiagnosticGlyph, bool ProblemsPanel,
    bool DefinitionPeek, bool Breadcrumbs, bool Outline, bool CodeActions, string? Error = null);
