# Phase 6: solution model and Roslyn

## Goal

Load a .NET solution or project accurately enough for semantic C# services.

## Scope

- Open `.sln`, `.slnx`, or `.csproj` and display solution/project/file/reference nodes.
- Discover and register MSBuild, evaluate target frameworks and configurations, and report load failures.
- Maintain a Roslyn workspace with stable mappings between workspace paths, NovaSharp documents, and Roslyn document IDs.
- Synchronize the versioned .NET document replicas into Roslyn asynchronously; foreground language requests may await the required edit-sequence barrier.
- Handle linked files and the same physical file included in multiple project contexts.
- Refresh after project-file changes, restore/package changes, and generated-file changes.
- Show project-load progress and structured diagnostics without blocking the UI.

Build execution and IntelliSense presentation are deferred; this phase establishes correct semantic inputs.

## Design constraints

- Roslyn Workspaces are the solution-wide entry point for analysis and refactoring; syntax trees and semantic models belong to immutable snapshots ([Roslyn SDK model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).
- Do not infer compilation references by scanning output folders. Use evaluated project information.
- A physical file can map to multiple Roslyn documents. Choose context from the active project and expose context switching when ambiguous.
- Queue workspace mutations through one single-writer coordinator; run independent evaluation/analysis on bounded background workers and cancel derived work when its source version becomes stale.
- Keep raw MSBuild logs available separately from concise user-facing project diagnostics.
- Never invoke MSBuild evaluation, restore inspection, or Roslyn analysis on the renderer/browser thread. Publish immutable progress/results and prioritize the active document over unopened-project analysis.
- Restore exact Roslyn/MSBuild package versions through `PackageReference`; acquire the pinned executable Roslyn server and matching
  Razor payload through the repository bootstrap described in [language-server assets](language-server-assets.md).

## Completion criteria

- Representative SDK-style console, library, ASP.NET Core, Razor, multi-project, and multi-target solutions load.
- Project references, package references, defines, language version, nullable settings, and analyzer references reach Roslyn.
- Unsaved changes appear in the active Roslyn snapshot without touching disk.
- Reload preserves open documents and reports removed/changed project contexts.
- Integration tests use fixture solutions and run without relying on machine-global state beyond the selected SDK.
- Load/reload cancellation, concurrent project completion order, edit-during-reload, worker saturation, snapshot bounds, and UI responsiveness meet numeric budgets.

## Next phase

Register project-aware C# language providers with Monaco.
