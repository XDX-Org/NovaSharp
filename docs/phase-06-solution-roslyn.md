# Phase 6: solution model and Roslyn

## Status

Complete — all completion criteria and the four-platform verification matrix pass.

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

## Supported inputs and budgets

- SDK-style C# console, library, ASP.NET Core, Razor, multi-project, and multi-target projects supported by the selected installed SDK.
- On the Phase 6 fixture (8 projects, including 4 multi-target projects, and 500 documents), initial solution load should complete within 10 seconds, reload within 5 seconds, and retain no more than 3 live Roslyn solution snapshots after pending editor updates settle. Measure on the Linux CI `ubuntu-24.04` x64 runner; other supported platforms report the same counters without using them as hard timing gates.
- Project-system decisions are recorded in [ADR 0003](decisions/0003-phase-06-project-system.md).

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

## Implementation

- `Microsoft.Build.Locator` selects the installed SDK and `MSBuildWorkspace` evaluates solutions and projects.
- One `RoslynProjectSystem` owns immutable solution snapshots, physical-path/document-ID mappings, active linked-file contexts, project watching, reload, and stale editor-update cancellation.
- The Explorer shows solution, evaluated project/target-framework, file, project-reference, assembly-reference, and analyzer nodes. Load progress, concise structured diagnostics, and raw MSBuild messages are separate views.
- Editor mutations publish versioned snapshots. Dirty text is applied to every linked Roslyn document context without changing disk and is reapplied after reload.

## Verification

On 2026-08-02, run 30771058164 passed warning-free builds, all 54 tests, and packaging on Windows x64, Linux x64, macOS arm64, and macOS x64, plus the Linux Phase 2–6 packaged native interactions. Phase 6 fixtures cover library, console, ASP.NET Core/Razor, multi-project, multi-target, project references, compiler settings, analyzers, linked files, dirty snapshots, reload preservation, and the named 500-document load budget.

## Known limitations

- Only SDK-style C# projects supported by the selected installed SDK are loaded.
- Configuration defaults to MSBuild's `Debug` evaluation; configuration selection is deferred until build/run commands exist.
- Solution folders are represented by evaluated projects rather than as buildable project contexts.

## Next phase

Feed semantic language results into the native Blazor editor presentation.
