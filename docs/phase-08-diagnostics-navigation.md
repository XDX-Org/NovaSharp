# Phase 8: diagnostics and code navigation

## Goal

Turn language analysis into an IDE workflow for finding, understanding, and fixing code.

## Scope

- Syntax, compiler, and analyzer diagnostics as editor squiggles, glyphs, hover details, and a Problems panel.
- Filter/group Problems by severity, project, file, and source; activation navigates to the exact range.
- Go to definition/type definition/implementation, Peek Definition, find references, and document/workspace symbols.
- Breadcrumbs and outline for the active document.
- Rename with preview, formatting, code actions, quick fixes, and fix-all where supported.
- Navigation history with Back/Forward and restoration of group, tab, cursor, and selection.
- Multi-file workspace-edit transaction with conflict detection and rollback before disk writes.

## Design constraints

- The .NET diagnostic store is authoritative. Visible line rows project intersecting diagnostic spans into underlines, gutter glyphs, overview marks, and accessible hover/focus details; virtualization must not limit the Problems list.
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
