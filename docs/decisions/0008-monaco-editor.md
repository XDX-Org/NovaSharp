# 0008: Monaco editor presentation and input

## Status

Accepted on 2026-08-14.

## Decision

Use the pinned, locally packaged Monaco Editor as NovaSharp's default editor presentation and input engine.
`EditorDocumentState` remains authoritative for content versions, dirty state, undo/redo, encoding, line endings,
saving, recovery, and disk conflicts. NovaSharp's existing language providers and `LanguageDocumentCoordinator`
remain the only LSP route.

One bounded Monaco model is keyed by each `EditorDocumentState.Id` and shared by duplicate views. Monaco sends
ordered UTF-16 edit batches with a base document version; .NET rejects stale, invalid, or overlapping batches and
records each accepted batch as one undo transaction. .NET-originated changes use guarded model updates to prevent
interop echo loops.

Monaco owns caret geometry, selection, scrolling, wrapping, composition, viewport rendering, and editor-local
accessibility. NovaSharp supplies settings, semantic classifications, diagnostics, breakpoints, execution state,
commands, workspace-edit validation, and persisted view state through the host boundary.

All Monaco scripts, workers, styles, and license texts are generated from the exact npm lockfile and packaged under
`wwwroot/monaco`; runtime CDN or network access is prohibited. Published builds fail when those assets are absent.

## Consequences

- Native browser input and Monaco handle IME, bidi text, wrapping, hit testing, multiple selections, and viewport
  rendering without maintaining two aligned full-document layers.
- The frontend asset build and worker startup become release dependencies and are qualified in packaged Linux hosts
  plus the existing multi-RID build matrix.
- NovaSharp retains control over persistence, external conflicts, LSP process ownership, stale-result rejection,
  workspace transactions, and bounded runtime state.
- Full-model `setValue` updates are reserved for .NET-originated replacements; interactive typing uses incremental
  edit batches.
- The former textarea/presentation renderer and its runtime switch are removed; Monaco is the sole editor path.
