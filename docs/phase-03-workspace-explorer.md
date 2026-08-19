# Phase 3: workspace explorer

## Goal

Open a folder and navigate its contents through a resizable Explorer panel.

## Scope

- Open/close one workspace folder and show its canonical path.
- Resizable, collapsible left sidebar with an accessible hierarchical tree.
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

## Next phase

Generalize the single document into a registry and add movable document tabs.
