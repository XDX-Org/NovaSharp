# NovaSharp documentation

NovaSharp is being built in small, runnable phases. Each phase must leave the application usable and define a narrow foundation for the next one. A phase is complete only when its completion criteria and the applicable [delivery gates](delivery-plan.md) pass on every row of the supported platform matrix. NovaSharp is a cross-platform IDE: no operating system is the reference platform, and evidence from one platform is not evidence for the others.

## Phases

- **Phase 1:** [Monaco single-file editor shell](phase-01-single-file-editor.md) — Photino, locally packaged Monaco and its worker, and opening one `.cs` file.
- **Phase 2:** [Editor and file lifecycle](phase-02-editor-file-lifecycle.md) — Monaco-backed models, asynchronous edit replication, safe save/reload, dirty state, and shortcuts.
- **Phase 3:** [Workspace explorer](phase-03-workspace-explorer.md) — folder/project tree, file operations, and scalable tree behavior.
- **Phase 4:** [Documents and movable tabs](phase-04-documents-tabs.md) — multiple files, tab ordering, close flows, and document identity.
- **Phase 4.5:** [Workbench shell and visual system](phase-04-5-workbench-shell.md) — application regions, command surfaces, icons, design tokens, and responsive accessibility.
- **Phase 5:** [Editor groups and split views](phase-05-editor-groups.md) — horizontal/vertical groups, drag targets, and side-by-side editing.
- **Phase 6:** [Solution model and Roslyn](phase-06-solution-roslyn.md) — `.sln`/`.csproj` loading and synchronized Roslyn documents.
- **Phase 7:** [C# IntelliSense](phase-07-csharp-intellisense.md) — completion, signature help, hover, and semantic highlighting.
- **Phase 8:** [Diagnostics and code navigation](phase-08-diagnostics-navigation.md) — squiggles, Problems, definitions, references, rename, and code actions.
- **Phase 9:** [Workspace search and replace](phase-09-search-replace.md) — Quick Open, streamed search, and transactional replacement.
- **Phase 10:** [Build, run, and output](phase-10-build-run.md) — MSBuild orchestration, startup profiles, process control, and structured output.
- **Phase 11:** [Integrated terminal](phase-11-terminal.md) — terminal emulation and cross-platform process sessions.
- **Phase 12:** [Debug adapter foundation](phase-12-debug-adapter.md) — debugger protocol, lifecycle, source identity, and breakpoint binding.
- **Phase 13:** [Debugging experience](phase-13-debugging-experience.md) — stepping, stacks, variables, watches, evaluation, and debug console.
- **Phase 14:** [Durable workbench](phase-14-durable-workbench.md) — settings, layouts, accessibility, recovery, and performance hardening.
- **Phase 15:** [Razor, HTML, and CSS](phase-15-web-languages.md) — project-aware Razor/Blazor editing and web-language services.
- **Phase 16:** [Extension architecture](phase-16-extensions.md) — a versioned, permission-aware extension host and SDK.
- **Phase 17:** [Preview release](phase-17-preview-release.md) — packaging, signing, updates, support policy, and release qualification.

The sequence is dependency order, not a promise that every phase has equal size. See:

- [Delivery plan](delivery-plan.md) for status, cross-cutting foundations, quality gates, risks, and open decisions.
- [Research and architecture notes](ide-roadmap-research.md) for the design rationale and source material.
- [Monaco decision](decisions/0001-monaco-editor.md) for editor ownership, packaging, and integration boundaries.
- [Document lifecycle decision](decisions/0002-document-lifecycle.md) for the edit-journal boundary, the text-encoding
  catalogue and its fallback, and where settings are stored.
- [Desktop host decision](decisions/0003-desktop-host.md) for the pinned cross-platform window host and why the
  preview fork was replaced.
- [Workbench shell decision](decisions/0004-workbench-shell.md) for stable regions, command surfaces, semantic assets,
  responsive behavior, and accessibility fixtures.
- [Editor groups decision](decisions/0005-editor-groups.md) for bounded split layout ownership, shared Monaco models,
  per-view state, and persistence.
- [Solution and Roslyn decision](decisions/0006-solution-and-roslyn-hosting.md) for in-process MSBuild evaluation, SDK-style inputs,
  target-framework contexts, and single-writer workspace mutation.
- [Workbench shell contract](workbench-shell.md) for region ownership, keyboard behavior, tokens, and visual fixtures.
- [Supported platform matrix](delivery-plan.md#supported-platform-matrix) and its parity rule, which govern every phase equally.
- [Language-server assets](language-server-assets.md) for automatic acquisition, pinned versions, hashes, and runtime policy.

The former combined phases 9 and 10 are retained as redirect pages so existing links do not silently point at obsolete scope.
