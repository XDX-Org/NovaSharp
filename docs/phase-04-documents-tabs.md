# Phase 4: documents and movable tabs

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

- Opening the same path focuses its existing document instead of loading another buffer.
- Reordering works by pointer and keyboard, including while the strip overflows.
- Every multi-close command prompts once with an exact list of dirty documents and supports cancel.
- Restoring a session tolerates moved or deleted files and malformed state.
- Automated tests cover tab ordering, preview promotion, close decisions, duplicate names, and restoration.
- Rapid tab switching/restoration does not leak models, enqueue duplicate loads, or delay typing in the active Monaco instance.

## Next phase

Make tab ownership explicit through editor groups and split layouts.
