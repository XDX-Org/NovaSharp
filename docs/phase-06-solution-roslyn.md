# Phase 6: solution model and Roslyn

## Status

In progress. The original phase passed [qualification run 33088968049](https://github.com/XDX-Org/NovaSharp/actions/runs/33088968049)
on all six supported runtime rows from commit `47b0965`. The warm-reopen implementation and local gates are present; completion now
requires one qualification run of the current commit passing the cached-display and live-validation gates on all six rows.

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

## Delivered implementation

- [ADR 0006](decisions/0006-solution-and-roslyn-hosting.md) selects the in-process `MSBuildWorkspace` authority, SDK-style project boundary,
  and separate target-framework contexts. The unused phase 7 Roslyn Features dependency was removed.
- `SolutionWorkspaceService` supersedes stale loads and publishes replacements only through one mutation writer. The current workspace, its
  replica-signal channel, project progress, raw build log, diagnostics, display documents, and retained Roslyn solution are all explicitly bounded.
- Solution evaluation has its own bounded worker, so cancellation cleanup and watcher-driven reloads cannot consume the foreground file-I/O workers.
  Changes observed while evaluation is already loading are covered by that evaluation instead of feeding a reload loop. Completed debounce state is
  released before disposal, and delayed watcher batches stamped before the successful publication are discarded. Closing a clean editor releases its
  overlay without reloading; closing a discarded dirty overlay reloads the disk-backed Roslyn text.
- `.sln`, `.slnx`, and `.csproj` inputs open from the Explorer, native picker, Workspace menu, or command palette. A single top-level solution is
  discovered when a folder opens. An accessible Explorer dropdown switches between folder and solution views. Solution view uses the same
  hierarchical tree rows, icons, keyboard navigation, incremental rendering, and collapse behavior as folder view; project-relative folders,
  linked files, target frameworks, dependencies, load progress, and failures remain visible without exposing intermediate build directories.
- Solution loading can be cancelled from its accessible progress control or the Workspace command palette. Closing the window cancels Roslyn
  evaluation immediately, awaits its cleanup, and keeps overall application shutdown within one explicit deadline.
- Physical file URIs map to every linked and target-framework `DocumentId`. The active editor exposes a project-context selector when a mapping
  is ambiguous.
- Ordered replicas signal Roslyn only after the .NET shadow advances. Foreground callers can await the Monaco sequence barrier; reload overlays
  the newest dirty snapshots before atomic publication and never writes them to disk.
- Workspace watcher changes to solutions, projects, imports, restore assets, and source-set membership coalesce into a reload. Content changes to
  existing C# and generated C# inputs update mapped Roslyn documents through the mutation coordinator without project reevaluation, preventing
  evaluation outputs from feeding a watcher/reload loop. Reload reports added and removed contexts while open Monaco models remain alive.
- `DiagnosticStore` keys bounded structured results by producer, context, source version, and stable identity. Concise failures reach notifications;
  the separately bounded raw MSBuild log remains available for investigation.
- The last successful solution tree is stored as one bounded, atomic, schema-versioned warm display cache. A later process restores workspace-relative
  project/document paths and the exact solution identity before live evaluation completes, while keeping `CurrentSolution` null and language services
  unavailable until a fresh `MSBuildWorkspace` publishes. A solution outside the restored workspace is never cached for that workspace. Changed
  solution/project/restore/analyzer and conventional imported inputs reject the cache; every cache hit is still validated by live evaluation. Startup
  overlaps cache restoration and solution discovery with configuration loading.
- Native-smoke mode neither restores the user's workspace nor reads or writes its warm cache, so local qualification fixtures cannot replace the
  next solution that the interactive application will reopen.
- Default MSBuild capture uses normal verbosity rather than paying detailed-event costs on every launch; the bounded raw log and structured workspace
  diagnostics remain available.
- The representative fixture contains console, library, Web/Razor, multi-project, multi-target, linked-file, project-reference, defines, nullable,
  language-version, framework/package assembly, and analyzer inputs. Real `MSBuildWorkspace` integration tests use the selected repository SDK.

## Performance budgets

Each CI matrix row records these against its named hosted-runner/RID fixture in `phase-01-06-native.json`:

| Gate | Budget |
|---|---:|
| Representative solution cold load | ≤ 20,000 ms |
| Representative solution reload with dirty overlay | ≤ 15,000 ms |
| Warm cached solution-tree display from a fresh service | ≤ 500 ms |
| Warm live validation from a fresh service | ≤ 15,000 ms |
| Foreground replica-to-Roslyn barrier | ≤ 500 ms |
| First semantic model | ≤ 5,000 ms |
| First phase 7 completion result | ≤ 750 ms |
| Managed memory added by the solution workspace | ≤ 384 MB |
| Retained current Roslyn solutions | 1 |
| Mutation queue | 128 items; pending count never exceeds capacity |
| Open-replica overlay cache | 1,024 sources; closed sources are released |

The first-completion budget is fixed here but becomes executable with the completion provider in phase 7. Solution measurements run after the
fixture projects have restored and built, so acquisition time is not hidden inside load time. Phase completion still requires retained records
from every supported runtime identifier; a local measurement is development evidence only.

## Verification

- Managed behavior tests cover real SDK evaluation, `.slnx`, multi-target progress, linked mappings, project and package/framework references,
  dirty synchronization, explicit context selection, edit-during-reload, removed contexts, stale progress, user cancellation, shutdown cleanup, diagnostic redaction,
  discovery, bounded saturation, and accessible project-tree contracts.
- `tools/NovaSharp.PhaseVerification` records load/reload, dirty barrier, first semantic model, memory, mapping, queue, and snapshot gates alongside
  the existing native/editor/Explorer budgets on every CI row. It also creates a cache, constructs a fresh solution service over that persisted file,
  gates cached-tree display and live validation separately, and asserts that the cached state has no Roslyn authority.
- CI continues to run identical bootstrap, managed/browser tests, explicit-RID publish, packaged native smoke, performance, disposal, and retained
  evidence gates for all six matrix rows.

[Qualification run 33088968049](https://github.com/XDX-Org/NovaSharp/actions/runs/33088968049) remains the retained evidence for the
original phase contract. All six jobs passed from commit `47b0965`, but that run predates the warm-reopen gates and cannot qualify this revision.

## Next phase

Register project-aware C# language providers with Monaco.
