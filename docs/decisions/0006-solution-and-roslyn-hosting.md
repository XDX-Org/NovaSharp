# 0006: in-process Roslyn solution workspace

## Status

Accepted.

## Decision

NovaSharp hosts C# project evaluation and semantic state in-process with the exact `Microsoft.CodeAnalysis.Workspaces.MSBuild`,
`Microsoft.CodeAnalysis.CSharp.Workspaces`, and `Microsoft.Build.Locator` package versions restored by the repository lock inputs.
`MSBuildLocator` selects the SDK resolved for the opened workspace, including its `global.json`; NovaSharp does not scan output folders or
construct compilation references itself.

The executable Roslyn/Razor payload acquired by bootstrap is not a second C# semantic authority. It remains packaged for the later Razor
protocol boundary. C# completion and other phase 7 providers will read the in-process workspace.

Only SDK-style `.csproj`, `.sln`, and `.slnx` inputs are supported for preview. `MSBuildWorkspace` evaluates every target-framework context it
returns. Each context is a distinct Roslyn project and the first evaluated target framework is initially active. A physical file can map to
several Roslyn document IDs through linked files or target frameworks; commands use the explicitly active project context and the workbench
offers context switching when that mapping is ambiguous.

One bounded, single-writer coordinator owns workspace replacement and document mutations. Solution/project evaluation runs through the bounded
background scheduler. A reload builds a replacement workspace, overlays the newest open-document replicas, then atomically publishes it; stale
or cancelled loads are disposed without publication. Project, restore, and source-set changes coalesce into reload requests. Content changes to
existing C# inputs update their mapped Roslyn documents through the same coordinator; they must not reevaluate the project because evaluation can
itself rewrite generated C# outputs and create a watcher/reload loop.

Live and externally refreshed text is retained as an immutable `Solution` overlay owned by the coordinator. NovaSharp must not publish those
text changes through `MSBuildWorkspace.TryApplyChanges`: that API is an apply-to-host operation and can write an unsaved editor replica to disk.
Only the document save path may persist editor text.

Project-load diagnostics are stored by producer, project context, source version, and stable identity. Concise diagnostics are published to the
workbench; a separately bounded raw MSBuild log remains available for investigation.

## Consequences

- Phase 7 adds `Microsoft.CodeAnalysis.CSharp.Features` because its completion provider consumes Roslyn's feature service; unused feature
  packages remain excluded.
- The active target framework is policy, not a compilation merge. Switching context changes which immutable Roslyn project answers a request.
- Reload does not close Monaco models or write dirty buffers to disk.
- Non-SDK and unsupported project types produce structured load diagnostics instead of partial inferred compilations.
- Every supported runtime uses the same loader and coordinator. SDK discovery differences stay inside `MSBuildLocator` and the selected SDK.
