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

- A document registry owns buffers by canonical URI. Tabs reference documents; they do not own file text.
- Closing a tab closes one view. Dispose the document only when no view, background operation, or explicit owner retains it.
- Model dirty state as saved-version versus current-version, not a manually toggled Boolean.
- Keep drag state transient and commit one reorder operation on drop.
- Established IDEs support tab reordering, preview tabs, grouping, and overflow management ([VS Code tabs](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio tabs and layouts](https://learn.microsoft.com/en-us/visualstudio/ide/customizing-window-layouts-in-visual-studio?view=visualstudio)).

## Completion criteria

- Opening the same path focuses its existing document instead of loading another buffer.
- Reordering works by pointer and keyboard, including while the strip overflows.
- Every multi-close command prompts once with an exact list of dirty documents and supports cancel.
- Restoring a session tolerates moved or deleted files and malformed state.
- Automated tests cover tab ordering, preview promotion, close decisions, duplicate names, and restoration.

## Progress

Implemented on `phase-4`:

- Canonical-path document registry with one buffer per open file.
- Ordered tabs with active-document and per-tab view state.
- Multi-file opening, tab switching, close buttons, middle-click close, and dirty-close prompts.
- Pointer and keyboard reordering, editor-wide drop targeting, drag feedback, and horizontal overflow.
- Dirty, preview, pinned, read-only, missing-file, and duplicate-name presentation foundations.
- Automated coverage for path deduplication, ordering, preview promotion, dirty-close protection, and duplicate names.
- Tab context menu and close-others/right/saved/all commands with one aggregated dirty-document prompt.
- Explorer single-click preview navigation and explicit pin controls.
- Session persistence for open files, active tab, ordering, cursor, selection, and scroll state.
- Restoration recovery tests for moved, deleted, and malformed session entries.
- Automated UI interaction coverage for pointer reordering, overflow, middle-click, and accessible labels.

Automated unit and recovery tests pass locally. The Linux/Xvfb Phase 4 interaction smoke is wired into CI;
local execution requires the native GTK/WebKit/Xvfb dependencies and remains pending on this machine.

## Next phase

Make tab ownership explicit through editor groups and split layouts.
