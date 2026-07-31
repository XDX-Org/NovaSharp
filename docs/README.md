# NovaSharp documentation

NovaSharp is being built in small, runnable phases. Each phase must leave the application usable and define a narrow foundation for the next one.

## Phases

1. [Single-file editor shell](phase-01-single-file-editor.md) — PhotinoXDX window, one editor panel, and opening one `.cs` file.
2. [Editor and file lifecycle](phase-02-editor-file-lifecycle.md) — a DnSpyXDX-style Blazor editor, safe save/reload, dirty state, and shortcuts.
3. [Workspace explorer](phase-03-workspace-explorer.md) — folder/project tree, file operations, and scalable tree behavior.
4. [Documents and movable tabs](phase-04-documents-tabs.md) — multiple files, tab ordering, close flows, and document identity.
5. [Editor groups and split views](phase-05-editor-groups.md) — horizontal/vertical groups, drag targets, and side-by-side editing.
6. [Solution model and Roslyn](phase-06-solution-roslyn.md) — `.sln`/`.csproj` loading and synchronized Roslyn documents.
7. [C# IntelliSense](phase-07-csharp-intellisense.md) — completion, signature help, hover, and semantic highlighting.
8. [Diagnostics and code navigation](phase-08-diagnostics-navigation.md) — squiggles, Problems, definitions, references, rename, and code actions.
9. [Search, build, run, and terminal](phase-09-search-build-run.md) — workspace search, task output, process control, and terminal sessions.
10. [Debugging and durable workbench](phase-10-debugging-workbench.md) — debugger UI, persisted layouts, accessibility, and release hardening.

The sequence is dependency order, not a promise that every phase has equal size. See the [research and architecture notes](ide-roadmap-research.md) for the design rationale and source material.
