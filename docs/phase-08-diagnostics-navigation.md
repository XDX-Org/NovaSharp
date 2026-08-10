# Phase 8: diagnostics and code navigation

Status: Complete

Phase 15.2 replaced the editor-analysis producer with pinned language servers. Live diagnostics, navigation, symbols, rename, code actions, and workspace edits now enter the same stores and preview transactions through negotiated LSP capabilities; build diagnostics remain independent.

## Goal

Turn language analysis into an IDE workflow for finding, understanding, and fixing code.

## Scope

- Syntax, compiler, and analyzer diagnostics as editor squiggles, hover details, and a Problems panel.
- Filter/group Problems by severity, project, file, and source; activation navigates to the exact range.
- Go to definition/type definition/implementation, Peek Definition, find references, and document/workspace symbols.
- Document outline for the active document.
- Rename with preview, formatting, code actions, quick fixes, and fix-all where supported.
- Navigation history with Back/Forward and restoration of group, tab, cursor, and selection.
- Multi-file workspace-edit transaction with conflict detection and rollback before disk writes.

## Design constraints

- The .NET diagnostic store is authoritative. Visible line rows project intersecting diagnostic spans into underlines, overview marks, and accessible hover/focus details; virtualization must not limit the Problems list.
- Diagnostic and semantic decorations are versioned interval data merged during line presentation, following DnSpyXDX's existing visible-token decoration approach.
- Diagnostics are keyed by document version and producer. Publishing one producer's result must not erase another's.
- Navigation chooses an existing view when sensible; Peek is transient and must not alter tab history until promoted.
- Preview all multi-file edits. Reject or recompute when any affected document version differs.
- Roslyn diagnostics naturally include compiler and pluggable analyzer results ([Roslyn diagnostic APIs](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).

## Completion criteria

- Squiggles and Problems remain consistent through edits, reloads, project changes, and tab movement.
- Navigation across projects opens the correct file, group, line, and project context.
- Back/Forward restores the prior view without corrupting the tab layout.
- Rename updates open dirty documents and unopened files as one reviewed operation.
- Canceling or failing a workspace edit leaves every buffer and disk file unchanged.
- Accessibility announcements cover new errors and navigation results without overwhelming screen readers.

## Next phase

Connect editing to [workspace-wide search and replacement](phase-09-search-replace.md).

## Completion evidence

Completed on `phase-8`:

- versioned compiler/analyzer diagnostics with merged squiggles, overview marks, accessible details, and a non-virtualized Problems store;
- Problems filtering/grouping and exact-range activation;
- definition, type definition, implementation, Peek, references, document outline, and workspace symbols;
- Back/Forward view restoration across groups and project-context-aware navigation;
- rename and code-action previews, formatting, supported quick fixes/fix-all, and conflict-checked multi-file transactions with staged disk rollback;
- unit/acceptance coverage and a packaged Linux Phase 8 smoke gate.

All 68 tests, warning-as-error builds, packages, and the Linux Phase 2–8 native smoke gates pass across Windows x64, Linux x64, macOS Intel, and macOS Apple Silicon in [verification run 31268021847](https://github.com/XDX-Org/NovaSharp/actions/runs/31268021847).
