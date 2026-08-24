# Phase 3: workspace explorer

## Status

Complete. [Qualification run 32581336852](https://github.com/XDX-Org/NovaSharp/actions/runs/32581336852)
passed the retained gates on all six supported runtime identifiers from commit `6dbe097`.

## Goal

Open a folder and navigate its contents through a resizable Explorer panel.

## Scope

- Open/close one workspace folder and show its canonical path.
- Resizable, collapsible right sidebar with an accessible hierarchical tree.
- Lazy directory expansion, refresh, reveal active file, and persisted expansion state.
- Create, rename, delete, and move files/folders with confirmation and error recovery.
- Open files by mouse or keyboard; distinguish folders, supported files, unknown files, and symlinks.
- Ignore configurable paths plus `.git`, `bin`, and `obj` by default without hiding explicitly opened files.
- Filesystem watcher updates that preserve selection and expansion where possible.
- Introduce versioned persistence for workspace identity, expansion, sidebar, and restore state.

Solution/project semantics and source-control decorations are deferred. The tree represents the filesystem in this phase.

## Design constraints

- A tool window stays alive when hidden, rather than losing its state. This matches the established role of Explorer-like tool windows ([Visual Studio tool windows](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/tool-window/tool-window?view=visualstudio)).
- Tree nodes use stable canonical-path IDs. Expansion awaits batched enumeration on the bounded background scheduler and publishes immutable child batches; it never performs filesystem work in a render callback.
- Watcher events enter a bounded channel, are coalesced by canonical path, and update only affected branches. Overflow triggers a cancellable scoped rescan rather than unbounded event retention.
- Independent directory reads may run concurrently up to a measured limit. Mutations affecting the same path/document registry are serialized and revalidated immediately before commit.
- Detect symlink cycles and do not traverse outside the workspace through links unless explicitly allowed.
- File operations update the document registry atomically so an open renamed file keeps its buffer.
- Use virtualization or incremental rendering for large directories.

## Completion criteria

- A workspace containing at least 20,000 entries remains responsive while expanding nodes.
- Keyboard users can traverse, expand, collapse, activate, rename, and invoke context actions.
- External create/rename/delete events update affected branches without rebuilding the whole tree.
- Renaming an open dirty file retains its text, dirty state, and selection.
- Invalid paths, permissions, symlink loops, and watcher overflow produce recoverable UI errors.
- Expanding/canceling rapidly under watcher load stays within queue, worker, latency, and memory budgets without delaying Monaco input.

## Delivered implementation

- One canonical workspace root, lazy immutable tree snapshots, selection, expansion/collapse, refresh, active-file reveal,
  configurable ignores, and explicit reveal of ignored files.
- Supported, unknown, directory, and symbolic-link node kinds. Directory links are visible but are never traversed, so
  neither an outside-workspace link nor a cycle can escape the tree.
- A 1,024-event watcher channel with 50 ms path coalescing. Normal batches rescan only expanded affected parents;
  overflow rescans expanded branches and raises a recoverable warning.
- A single-writer, 32-operation mutation queue for create, rename, move, and confirmed delete. A rename or move updates
  an open document's URI, path, disk state, and watcher without replacing its Monaco model, text, dirty sequence, or view.
- A live collapsible Explorer tool window with an unrestricted persisted width, accessible tree semantics,
  keyboard activation/expand/collapse/rename/delete, item-specific pointer and keyboard context menus, and incremental
  rendering in batches of 250 rows. Folder context menus include creation actions; file context menus do not. A
  labelled header action collapses the workspace root and every expanded descendant while retaining loaded children.
- Versioned `workspace-state.json` persistence in the platform configuration directory. Workspace identity is stored as
  a canonical root; expansion, selection, and active-document paths are separator-neutral paths relative to that root.
  Writes are atomic. Invalid JSON is retained, copied to `.invalid`, reported, and treated as empty state.

Settings schema version 2 adds `workspaceIgnoredPaths`. Version 1 needs no rewrite: the absent field resolves to an
empty additional-ignore list, while `.git`, `bin`, and `obj` remain built-in. Rooted or escaping ignore patterns are
reported and ignored.

## Performance budgets

These gates run per supported runtime identifier and are retained in `phase-01-03-native.json` with the runner/RID
fixture name. A result on one row is not evidence for another.

| Budget | Limit | Fixture |
|---|---:|---|
| Enumerate and publish one expanded directory | 2,000 ms | Generated workspace with 20,000 C# files |
| Added managed memory for that tree | 48 MB | Same fixture after a compacting collection |
| External create to updated expanded branch | 2,000 ms | Same fixture through the real filesystem watcher |
| Watcher backlog | 1,024 events | Bounded channel; overflow rescans expanded branches |
| Initial rendered rows per expanded directory | 250 | Remaining rows exposed in 250-row incremental batches |

The .NET suite covers lazy expansion, default/configured ignores, explicit ignored-file reveal, branch-scoped watcher
updates, overflow recovery, selection preservation, serialized mutations, dirty open-file relocation, link
non-traversal, corruption fallback, portable state, and the 20,000-entry fixture. The existing browser typing workload
continues to gate Monaco paint, long-task, replication, and queue budgets while these services are present.

## Qualification

[Qualification run 32581336852](https://github.com/XDX-Org/NovaSharp/actions/runs/32581336852) passes the bootstrap,
234 .NET tests, 64 browser gates, RID publish, packaged native smoke, Explorer measurements, cancellation/disposal,
and retained artifact gates on all six supported runtime identifiers from commit `6dbe097`.

## Known limitations

- Phase 3 intentionally supports one folder. Multi-root workspaces and solution/project semantics are deferred.
- Links are shown but cannot be expanded. Allowing trusted in-workspace links would require an explicit policy change.
- Delete is permanent after confirmation; platform trash/recycle-bin integration belongs behind a later dialog/filesystem seam.

## Next phase

Generalize the single document into a registry and add movable document tabs.
