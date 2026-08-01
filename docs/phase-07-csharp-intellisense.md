# Phase 7: C# IntelliSense

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

## Next phase

Add persistent diagnostics, navigation, and safe workspace edits.
