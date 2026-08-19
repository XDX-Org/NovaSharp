# Phase 7: C# IntelliSense

## Goal

Provide responsive, project-aware C# editing assistance from Roslyn through Monaco's public language-provider APIs.

## Scope

- Completion on typing and explicit invocation, with filtering, ranking, lazy details, commit characters, additional edits, and snippets.
- Signature help with active overload and parameter.
- Hover with symbol signature, type, documentation, and containing assembly/project.
- Roslyn semantic tokens layered by Monaco over its baseline C# lexical tokenization.
- Automatic indentation, paired delimiters, comment toggling, and formatting selection/document.
- Configurable suggestion behavior and a visible busy/degraded state while projects load.
- Capability-based internal language-provider contracts suitable for Razor/HTML/CSS and later extensions without exposing Roslyn types.

AI/inline prediction, Razor projection, and third-party language servers are deferred.

## Monaco provider boundary

Register Monaco completion, signature-help, hover, document/range-formatting, and document/range-semantic-token providers. Monaco owns their editor-local UI, focus, keyboard handling, placement, accessibility, and token painting. NovaSharp must not render completion, signature, hover, or colored source as Blazor overlays.

Every request carries document URI, active project context, Monaco sequence, synchronized replica/Roslyn version, position/range, priority, and cancellation. Every response identifies its source version and is discarded before publication if the Monaco model or project context changed.

Monaco's C# language definition supplies lexical colorization and language configuration; Roslyn supplies semantic meaning and project-aware results. Do not register two providers for the same capability. Sanitize Markdown and leave HTML disabled unless a narrowly reviewed use requires it.

## Async and concurrency requirements

- Provider callbacks are asynchronous. They never block the browser/renderer thread and never require a Blazor component render.
- Explicit completion, signature help, formatting, and hover enter the foreground lane immediately. Background semantic work is debounced/coalesced and runs at lower priority.
- A latest-request coordinator per document/capability cancels superseded requests. Bounded global and per-document limits prevent rapid typing across tabs from starting unbounded Roslyn work.
- Roslyn reads use immutable snapshots concurrently. Solution mutations remain on the phase 6 single-writer coordinator.
- Resolve expensive completion documentation lazily for the focused item. Stream or page large results only where Monaco's provider contract permits; otherwise cap them deterministically.
- Semantic providers return Monaco token data for an exact version. Do not translate semantic colors into decorations or custom DOM rows.
- Capture queue delay, replica-barrier delay, Roslyn time, interop time, first-result latency, cancellation, stale rejection, and retained snapshot counts separately.

## Completion criteria

- Completion respects project references, usings, accessibility, nullable context, linked-file project context, and unsaved edits.
- Stale results never flash after rapid typing, cancel/retrigger, tab changes, or project reload.
- Signature help changes overload and parameter correctly as a call is edited.
- Hover and formatting failures are recoverable and do not interrupt typing.
- Monaco paints baseline and semantic C# colors without a parallel Blazor source/token layer.
- Fixture tests assert semantic results; interaction tests cover cancellation, out-of-order completion, queue saturation, provider disposal/reregistration, and cross-project concurrency.
- Cold/warm completion, first result, hover, signature, formatting, semantic refresh, typing latency under analysis load, queue depth, snapshot count, and memory budgets pass on a named medium solution and named hardware.

## Next phase

Publish diagnostics through Monaco markers and add navigation/workspace edits through Monaco providers.
