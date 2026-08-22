# Phase 4: documents and movable tabs

## Status

In progress. The implementation and local `win-x64` qualification gates pass. Phase completion still requires the
same commit to pass bootstrap, .NET/browser tests, RID publish, packaged native smoke, performance, disposal, and
retained-evidence gates on all six supported runtime identifiers.

## Goal

Work with many open files using predictable, reorderable tabs without duplicating document state.

## Scope

- Open multiple text files and switch through an ordered tab strip.
- Dirty, active, preview, pinned, read-only, and missing-file tab states.
- Close one, close others, close right, close saved, and close all.
- Drag tabs to reorder within the strip; keyboard commands provide equivalent movement.
- Middle-click close, overflow scrolling/list, duplicate-name disambiguation, and accessible labels.
- Restore open files, active tab, order, cursor, selection, and scroll state after restart.
- One reusable preview tab for single-click Explorer navigation; editing or pinning promotes it.

Splitting into multiple groups is deferred to phase 5, but tab state must not assume one permanent visual owner.

## Design constraints

- A document registry owns records, replicas, and Monaco model leases by canonical URI. Tabs reference documents; they do not own or copy file text.
- Closing a tab closes one view. Dispose the document only when no view, background operation, or explicit owner retains it.
- Model dirty state as saved sequence versus current Monaco sequence, not a manually toggled Boolean.
- Keep drag state transient and commit one reorder operation on drop.
- Switching tabs attaches the existing model and restores validated Monaco view state; it does not recreate the model, serialize its content, or wait for background services.
- Restore files concurrently within the global I/O limit, preserve deterministic tab order, and let the active tab use the foreground priority lane.
- Established IDEs support tab reordering, preview tabs, grouping, and overflow management ([VS Code tabs](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio tabs and layouts](https://learn.microsoft.com/en-us/visualstudio/ide/customizing-window-layouts-in-visual-studio?view=visualstudio)).

## Completion criteria

- **Implemented and locally verified.** Opening the same path focuses its existing document instead of loading another buffer.
- **Implemented and locally verified.** Reordering works by pointer and keyboard, including while the strip overflows.
- **Implemented and locally verified.** Every multi-close command prompts once with an exact list of dirty documents and supports cancel.
- **Implemented and locally verified.** Restoring a session tolerates moved or deleted files and malformed state.
- **Implemented and locally verified.** Automated tests cover tab ordering, preview promotion, close decisions, duplicate names, and restoration.
- **Implemented and locally verified.** Rapid tab switching/restoration does not leak models, enqueue duplicate loads, or delay typing in the active Monaco instance.

## Delivered implementation

- A document registry keyed by canonical URI owns sessions, replicas, model leases, and ordered editor-view tabs.
  Opening or concurrently restoring the same URI adopts the existing record and model.
- One Monaco editor retains leased models for every open document. Switching saves and restores validated cursor,
  selection, and scroll state, reattaches the existing model, and keeps per-document bounded replication pumps.
- Tabs expose active, dirty, preview, pinned, read-only, and missing-file states. Duplicate names use the shortest
  unique parent suffix and accessible labels name every state.
- Explorer single-click opens the reusable preview; double-click or keyboard activation pins it. Editing promotes a
  preview before another preview can replace it.
- Pointer drag/drop and platform-neutral keyboard commands reorder tabs. The scrollable strip and editor list cover
  overflow; middle click and the close button close one tab.
- Close, close others, close right, close saved, and close all resolve through the command registry. One prompt names
  the exact dirty subset and cancellation leaves every candidate open.
- Workspace state schema 2 adds ordered open documents, the active document, pin/preview flags, and portable view
  state. Schema 1 migrates additively; corrupt state still uses the existing backup-and-fallback path. Paths inside a
  workspace remain separator-neutral and relative.
- Restore starts the active document in the bounded foreground scheduler lane and loads remaining documents
  concurrently through the bounded background lane. Deleted entries remain visible as missing tabs.

## Local qualification

On 2026-08-22, the local `win-x64` fixture passed the full bootstrap, a warning-free solution build, 247 .NET tests,
69 real-browser gates, the pinned Monaco asset check, RID-specific Release publish, packaged native smoke, the existing
typing-under-background-load and 100-cycle model/heap budgets, and the negative RID-less/Debug publish gates. Native
cold/warm startup measured 952/903 ms, idle working set 79 MB, and the existing 10 MB, replication, save, Explorer,
and watcher budgets passed. This is local evidence only and does not satisfy the supported-platform parity rule.

## Known limitation

Open files and view state restore after an orderly restart. Unsaved text still has no crash-recovery journal; that
recorded limitation remains owned by phase 14.

## Next phase

Make tab ownership explicit through editor groups and split layouts.
