# Phase 6: solution model and Roslyn

## Goal

Load a .NET solution or project accurately enough for semantic C# services.

## Scope

- Open `.sln`, `.slnx`, or `.csproj` and display solution/project/file/reference nodes.
- Discover and register MSBuild, evaluate target frameworks and configurations, and report load failures.
- Maintain a Roslyn workspace with stable mappings between workspace paths, NovaSharp documents, and Roslyn document IDs.
- Synchronize unsaved editor snapshots into Roslyn using versioned, cancellable updates.
- Handle linked files and the same physical file included in multiple project contexts.
- Refresh after project-file changes, restore/package changes, and generated-file changes.
- Show project-load progress and structured diagnostics without blocking the UI.

Build execution and IntelliSense presentation are deferred; this phase establishes correct semantic inputs.

## Design constraints

- Roslyn Workspaces are the solution-wide entry point for analysis and refactoring; syntax trees and semantic models belong to immutable snapshots ([Roslyn SDK model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).
- Do not infer compilation references by scanning output folders. Use evaluated project information.
- A physical file can map to multiple Roslyn documents. Choose context from the active project and expose context switching when ambiguous.
- Queue workspace mutations through one coordinator; cancel derived analysis when its source version becomes stale.
- Keep raw MSBuild logs available separately from concise user-facing project diagnostics.

## Completion criteria

- Representative SDK-style console, library, ASP.NET Core, Razor, multi-project, and multi-target solutions load.
- Project references, package references, defines, language version, nullable settings, and analyzer references reach Roslyn.
- Unsaved changes appear in the active Roslyn snapshot without touching disk.
- Reload preserves open documents and reports removed/changed project contexts.
- Integration tests use fixture solutions and run without relying on machine-global state beyond the selected SDK.

## Next phase

Feed semantic language results into the native Blazor editor presentation.
