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

- The .NET diagnostic store is authoritative. Publish exact-version diagnostics to Monaco with `setModelMarkers`, using a stable owner per producer; the independent Problems store is not limited to visible lines.
- Use Monaco providers for definition/type/implementation/references, rename, code actions, symbols, and Peek where supported. Monaco owns editor-local result UI; NovaSharp owns cross-file navigation history and validates workspace edits.
- Do not overlay syntax or semantic colors. Reserve Monaco decoration collections for non-token adornments that markers/providers do not cover, and update collections rather than recreating the editor.
- Diagnostics are keyed by document version and producer. Publishing one producer's result must not erase another's.
- Navigation chooses an existing view when sensible; Peek is transient and must not alter tab history until promoted.
- Preview all multi-file edits. Reject or recompute when any affected document version differs.
- Roslyn diagnostics naturally include compiler and pluggable analyzer results ([Roslyn diagnostic APIs](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).
- Run diagnostic producers, reference search, symbols, and workspace-edit preparation asynchronously on bounded workers. Prioritize the active document, cancel superseded versions, and merge independent producer results deterministically through a single diagnostic-store writer.

## Completion criteria

- Squiggles and Problems remain consistent through edits, reloads, project changes, and tab movement.
- Navigation across projects opens the correct file, group, line, and project context.
- Back/Forward restores the prior view without corrupting the tab layout.
- Rename updates open dirty documents and unopened files as one reviewed operation.
- Canceling or failing a workspace edit leaves every buffer and disk file unchanged.
- Accessibility announcements cover new errors and navigation results without overwhelming screen readers.
- Diagnostics/navigation under rapid edits and concurrent project analysis stay within queue, latency, marker-count, result-count, and memory budgets without degrading Monaco typing.

## Next phase

Connect editing to [workspace-wide search and replacement](phase-09-search-replace.md).
