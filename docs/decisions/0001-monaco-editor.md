# 0001: Monaco Editor from phase 1

## Status

Accepted.

## Decision

Use an exact, lockfile-pinned `monaco-editor` release as NovaSharp's only source editor from phase 1. Bundle its ESM code, CSS, fonts, selected language definitions, and workers into the application; runtime CDN access and the deprecated AMD build are prohibited.

One Monaco `ITextModel` exists per canonical document URI and may be attached to multiple editor instances. Monaco is authoritative for live text, selections, composition, viewport rendering, token colors, editor-local widgets, and undo/redo while the model is open. NovaSharp owns file identity, encoding, line endings, dirty/save state, external-conflict policy, workspace transactions, project context, and persisted validated view state.

Because a Monaco model's URI is immutable, renaming or saving a document under a new canonical URI drains the old
replica pump, creates the replacement URI model from Monaco's live text, restores validated view state, and returns
one full snapshot for the new replica as it releases the old lease. A concurrent edit is therefore included in the
new model or in the following ordered stream; it is never reconstructed from a stale .NET snapshot.

Typing must not synchronously call .NET. A JavaScript-side pump coalesces Monaco change events into an ordered, bounded asynchronous replication stream of UTF-16 range edits and permits at most one interop send in flight per document. .NET maintains a versioned shadow for Roslyn, dirty-buffer search, recovery, and commands. Save, build, refactor, and other consistency-sensitive operations await a sequence barrier before reading that shadow; overflow or a detected gap triggers one full snapshot resynchronization. .NET-originated edits return through Monaco edit APIs with origin guards and intentional undo stops.

Use Monaco's public APIs rather than recreating editor UI:

- the built-in C# language definition for baseline lexical token colors;
- language providers for completion, hover, signature help, semantic tokens, formatting, navigation, rename, and code actions;
- model markers for diagnostics;
- decorations/collections for breakpoints, execution state, and other editor adornments;
- Monaco content/peek/overlay widgets for editor-local UI when a public provider API does not already own it.

Normal and diff editors hide Monaco's horizontal scrollbar through the public construction options. Long-line caret,
selection, reveal, and programmatic horizontal navigation remain Monaco-owned; NovaSharp does not add a replacement
scrollbar or synchronized overlay.

Blazor remains responsible for workbench UI such as Explorer, tabs, Problems, output, and dialogs. Roslyn remains the semantic authority for C#; Monaco's C# package supplies lexical language configuration, not project-aware C# IntelliSense.

## Consequences

- Delete the textarea/custom token-row design rather than maintaining two production editors.
- Do not send full document values or trigger Blazor rendering on each keystroke.
- Asset building, worker startup, disposal, accessibility, and third-party notices become phase-1/release gates.
- One host module and one worker-startup path serve every supported platform. A per-platform loader, a per-WebView asset variant, or a
  fallback enabled on only some operating systems is a violation of this decision, not an accommodation.
- Each feature has one authority. Disable any Monaco built-in language service that would duplicate the selected NovaSharp/Roslyn provider.
- Keep integrations on Monaco's versioned public TypeScript API; private VS Code internals are out of scope.
