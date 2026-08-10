# Phase 7: C# IntelliSense

## Status

Complete. Local verification and the Linux, Windows, macOS Intel, and macOS Apple Silicon packaging matrix pass.

The feature authority was replaced in Phase 15.2: release builds now obtain C# intelligence from the pinned Roslyn language server. Exact-version semantic tokens enrich the stable lexical presentation baseline without translating stale spans.

## Goal

Provide responsive, project-aware C# editing assistance from Roslyn through NovaSharp's Blazor editor.

## Scope

- Completion on typing and explicit invocation, with filtering, ranking, details, commit characters, and snippets.
- Signature help with active overload and parameter.
- Hover with symbol signature, type, documentation, and containing assembly/project.
- Semantic tokens layered over baseline C# syntax colorization.
- Automatic indentation, paired delimiters, comment toggling, and formatting selection/document.
- Configurable suggestion behavior and a visible busy/degraded state while projects load.
- Capability-based internal language-provider contracts suitable for later Razor/HTML/CSS and extension providers without exposing Roslyn types to the editor.

AI/inline prediction, Razor projection, and third-party language servers are deferred.

## Request contract

Every language request carries document ID, project context, document version, position/range, and cancellation. Responses carry the source version and are discarded when it no longer matches. Internal provider contracts cover completion, hover, signature help, formatting, and semantic spans; this matches the established language-feature breakdown without coupling the editor to another frontend runtime ([language-feature mapping](https://code.visualstudio.com/api/language-extensions/programmatic-language-features)).

Completion, signature-help, and hover popups are Blazor overlay components anchored through the editor's offset-to-visible-row geometry. Semantic spans are merged into visible token batches in C#, as DnSpyXDX merges semantic classifications into its line tokens. Keyboard ownership remains explicit: the editor retains typing and navigation keys except while an open popup consumes selection/accept/cancel commands.

## Design constraints

- Debounce only background work; explicit completion and signature help start immediately.
- Never block browser input or render callbacks on Roslyn analysis.
- Semantic updates invalidate only affected cached presentation batches, not the document or every rendered row.
- Cancel superseded requests and cap concurrently retained Roslyn snapshots.
- Resolve expensive completion descriptions lazily when an item is focused.
- Escape and sanitize all Markdown shown in hover/completion documentation.
- Track latency separately for first result and full result; do not log source content.

## Completion criteria

- Completion respects project references, usings, accessibility, nullable context, and unsaved edits.
- Stale results never flash after rapid typing or switching tabs/project contexts.
- Signature help changes overload and parameter correctly as the call is edited.
- Hover and formatting failures are recoverable and do not interrupt typing.
- Fixture tests assert semantic results; interaction tests cover cancellation and out-of-order responses.
- Define performance budgets and measure them on a medium fixture solution before marking complete.

## Performance budgets

On the 200-document Phase 7 fixture, cold explicit completion must finish within 2 seconds, hover and signature help within 1 second each, and full-document semantic classification within 3 seconds. Background semantic work is debounced by 150 ms and does not count that intentional delay toward provider latency. CI measures on the Linux `ubuntu-24.04` x64 runner; other platforms report the same measurements without hard timing gates.

## Implementation

- Provider-neutral, capability-based contracts carry an opaque document path, explicit project context, editor version, position/range, and cancellation without exposing Roslyn types to editor components.
- `LspLanguageProvider` supplies completion, lazy resolve details, signature help, hover, formatting, and exact-version semantic tokens through negotiated Roslyn capabilities.
- One latest-request coordinator per capability cancels superseded work and rejects responses whose sequence or source version is stale. Language requests await pending dirty-buffer synchronization before resolving their Roslyn document.
- The editor owns popup navigation and acceptance keys while overlays are open. Explicit completion/signature and formatting start immediately; semantic work is debounced. Hover and descriptions render as Blazor text, so markup is escaped rather than interpreted.
- Cached line presentations retain unchanged rows when semantic results are merged. Automatic indentation, delimiter pairing, line-comment toggling, and document/selection formatting are editor commands.
- Suggestion and semantic-highlighting behavior is persisted in editor settings. A visible loading state replaces language results while project evaluation is incomplete.

## Verification

The local Release suite covers project-aware and nullable-aware completion, accessibility, unsaved versions, lazy details, signature overloads and parameters, hover provenance, semantic classification, formatting, cancellation, out-of-order responses, and the medium-fixture budgets. Native interaction and four-platform package verification pass in CI.

## Next phase

Add persistent diagnostics, navigation, and safe workspace edits.
