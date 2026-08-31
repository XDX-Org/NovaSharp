# Phase 7: C# IntelliSense

## Status

Implementation and phase-specific local verification are complete. Formal phase completion is pending one retained CI qualification run from the same commit on all six supported runtime identifiers.

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

## Delivered architecture

- `CSharpLanguageService` reads exact-version immutable documents from the phase 6 workspace. Its public capability DTOs contain no Roslyn types.
- A bounded two-lane worker queue prioritizes completion, resolve, signature, hover, and formatting over semantic refresh. A latest-request owner per document/capability cancels superseded work.
- When an open C# document gains a live project context, one deduplicated background item primes Roslyn completion for that exact document, project, solution, replica version, and active caret. The resulting exact-position list can satisfy the first matching explicit request. The 16-entry warm-up registry and background lane are bounded, stale work publishes no result, and foreground completion retains priority and exact-version validation.
- Requests and responses carry canonical URI, active project ID, solution source version, Monaco model sequence, position/range, trigger, and request ID. Both .NET and JavaScript reject changed stamps.
- JavaScript retains the host canonical URI separately from Monaco's normalized model-map key, so replication, resynchronization, and language requests preserve Windows, macOS, and Linux identities exactly.
- Completion applies Roslyn's typed-prefix ranking before the 500-item cap, preserves preselected items, and reports an incomplete list only when the ranked matches exceed the cap. It keeps at most 512 lazy resolve entries and 16 exact-version completion lists, includes host snippets, and transports commit characters and additional edits. Cached lists are reused only while the document, project context, solution, replica version, position, and trigger still match; documentation and final changes resolve only for the focused item.
- Monaco registers one disposable language configuration plus completion, signature, hover, document/range formatting, and document/range semantic-token providers. Provider disposal is tied to the editor host.
- The `cSharpSuggestions` user/workspace setting controls completion without disabling hover, signature, formatting, or semantic tokens. The status bar reports loading or unavailable project services.

Settings schema version 4 adds `cSharpSuggestions`; version 3 files migrate by resolving the absent key to `true` and are stamped as version 4 on their next write.

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

## Performance budgets

Each CI row records the following in `phase-01-07-native.json`; browser typing/paint, interop, queue, and lifecycle measurements remain in the paired browser record.

| Gate | Budget |
|---|---:|
| Active-document completion warm-up | ≤ 1,500 ms |
| First project-aware completion | ≤ 750 ms |
| Warm completion | ≤ 200 ms |
| Warm signature help | ≤ 250 ms |
| Warm hover | ≤ 250 ms |
| Format selection | ≤ 1,000 ms |
| Semantic token refresh | ≤ 1,000 ms |
| Language work queue | 128 total queued items; observed pending count returns to zero |
| Lazy completion cache | 512 items |
| Exact-version completion-list cache | 16 lists; 500 items per list |

The named medium fixture is `tests/fixtures/phase-06/Workspace.slnx`, restored and built before measurement on the hosted-runner fixtures listed in the delivery plan. The verifier adds an unsaved C# probe through the normal replica path, so these results cannot pass by reading stale disk text.

## Verification

- Managed tests cover unsaved project-aware completion and lazy resolution, snippets, signature parameter selection, hover, formatting, semantic classifications, stale-sequence rejection, metrics, and Roslyn-free provider contracts.
- Browser interaction tests prove public Monaco registration, platform-shaped canonical URIs across replication/resynchronization/language requests, stamped completion requests, Monaco-owned suggestion UI, deterministic provider disposal/reregistration, typing/paint latency, bounded replication, and 100-cycle heap retention.
- `tools/NovaSharp.PhaseVerification` executes the feature budgets, exact-replica result checks, solution/snapshot bounds, packaged native smoke, and existing startup/editor/Explorer gates per RID.
- Verification records separate queue delay, replica barrier, Roslyn execution, total provider latency, and browser interop latency.
- Qualification remains incomplete until all six matrix rows retain passing records from the same commit. A local result is development evidence only.

## Known limitations

- Project-aware providers require a successfully loaded SDK-style C# project; standalone or failed-project documents keep Monaco lexical editing and show the unavailable status.
- AI/inline prediction, Razor projection, diagnostics, navigation, rename, and code actions remain assigned to later phases.

## Next phase

Publish diagnostics through Monaco markers and add navigation/workspace edits through Monaco providers.
