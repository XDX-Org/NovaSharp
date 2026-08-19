# Delivery plan

## Product boundary

The preview target is a cross-platform IDE for local, SDK-style .NET development with C#, Razor/Blazor, HTML, and CSS. It includes editing, project loading, language services, search, build/run, terminal, managed debugging, durable settings/layouts, and a minimal extension SDK.

Source control UI, remote development, collaboration, notebooks, AI completion, designers, profiling, multiple native windows, non-.NET debugging, and non-SDK project systems are not preview requirements.

## Status

| Phase | Status | Exit evidence |
|---|---|---|
| 1. Monaco single-file editor shell | Planned | The existing textarea must be replaced; it does not meet the Monaco-first phase gate |
| 2–17 | Planned | Completion criteria not yet met |

Status values are `planned`, `in progress`, `blocked`, and `complete`. Update this table only from test or release evidence; documentation or partial UI alone does not complete a phase.

## Cross-cutting foundations

These are not separate feature phases. Introduce each boundary by the indicated phase and retain it thereafter.

| Foundation | Required by | Minimum contract |
|---|---:|---|
| Dependency bootstrap | 1 | One documented command acquires hash-pinned Monaco, Roslyn/Razor, Node, and web-language assets for the current RID |
| Monaco host and asset pipeline | 1 | Exact npm lock, local ESM bundle, functioning editor worker, deterministic disposal, no runtime CDN |
| Async work scheduler | 1 | UI/background priority lanes, bounded queues, cancellation, backpressure, ownership, and observable saturation |
| Command registry | 2 | Stable command ID, handler, enablement, keybinding, menu/palette metadata |
| Configuration service | 2 | Typed defaults, user/workspace scopes, validation, atomic versioned storage |
| Notification and logging | 2 | Structured severity, actionable errors, bounded local logs, source-text redaction |
| Lifetime/task coordinator | 1 | Ownership, cancellation, stale-result rejection, disposal diagnostics |
| Persistence service | 3 | Versioned schemas, portable workspace paths, atomic writes, corruption fallback |
| Diagnostic store | 6 | Results keyed by producer, context, document version, and stable identity |
| Process service | 10 | Argument arrays, explicit environment/working directory, process-tree ownership |
| Capability/extension boundary | 7 | Internal provider contracts that can later be exposed selectively by phase 16 |

Record decisions that constrain multiple phases as short ADRs under `docs/decisions/`. The editor decision is fixed by [ADR 0001](decisions/0001-monaco-editor.md). Decide project-system strategy before phase 6, terminal engine before phase 11, and debug adapter before phase 12.

## Required execution model

- Monaco's browser thread owns typing, selection, IME, undo/redo, token rendering, editor widgets, and viewport work. No .NET round trip is allowed in the keystroke-to-paint path.
- JavaScript sends ordered incremental change batches to .NET without waiting for Blazor rendering. The receiver is a bounded single-writer queue per document; coalesce safe adjacent notifications and request a full resynchronization if a sequence gap occurs.
- File, watcher, process, terminal, adapter, and network I/O use asynchronous APIs end to end. Do not block with `.Result`, `.Wait()`, synchronous polling, or UI-thread file access.
- CPU-heavy project evaluation, Roslyn analysis, indexing, search, parsing, and serialization run on bounded background workers. Parallelize independent work only; coordinators that mutate shared state remain single-writer.
- Foreground requests have priority over speculative/background work. Every supersedable operation carries cancellation and a source version; stale results are discarded before publication.
- Queues, caches, task counts, worker counts, and retained snapshots are bounded. Overload degrades by canceling/coalescing background work, never by accumulating an unbounded backlog.
- UI state is published as small immutable snapshots. Background workers do not mutate Blazor component state and marshal only the final state change to the renderer.

## Supported platform matrix

Before phase 2 completes, record exact minimum OS versions and CI images for Windows x64, Linux x64, and macOS arm64/x64. A platform is supported only when its native host prerequisites, packaging format, and automated smoke-test route are documented. Other architectures are best effort until added to this matrix.

## Quality gates

Every phase must pass:

- A clean build with warnings either fixed or linked to an accepted, time-bounded issue.
- Unit tests for state and algorithms; integration tests for service boundaries; interaction tests for the phase's primary user flow.
- Cancellation, disposal, error recovery, keyboard access, and restored-state tests where applicable.
- Tests prove the UI/Monaco thread is not blocked, stale work is rejected, queues remain bounded, and concurrent completion order cannot corrupt state.
- No secrets, source text, or absolute paths in telemetry; telemetry remains opt-in.
- Updated status, user documentation, known limitations, and migration notes for changed persisted schemas.

Budgets must be measured on named fixture hardware and repositories. Set numeric budgets before implementing the affected phase:

| Budget | Set no later than |
|---|---:|
| Startup time and idle memory | 2 |
| Typing/render latency and large-file memory | 2 |
| Explorer expansion and watcher recovery | 3 |
| Solution load, Roslyn snapshot count, completion first result | 6 |
| Search throughput/result memory | 9 |
| Build cancellation/process cleanup | 10 |
| Terminal input/resize latency | 11 |
| Debug step/evaluate latency | 12 |
| Crash recovery and full-workbench memory | 14 |

## Delivery records

For an active phase, track an owner, target release, dependencies, risks, and links to implementation issues. Each implementation issue should be small enough to review independently and name the completion criterion it advances.

## Principal risks

| Risk | Required mitigation |
|---|---|
| JS/.NET synchronization enters the typing hot path | Monaco remains locally responsive; replicate incremental edits asynchronously and use barriers only for consistency-sensitive commands |
| Monaco workers fail under a packaged WebView origin | Prove locally bundled ESM assets and worker creation on every supported host in phase 1 |
| Upstream executable assets change or disappear | Pin versions and hashes per RID, fail closed on mismatch, retain notices, and mirror only through an audited manifest change |
| Duplicate Monaco models diverge across split views | One model per document URI with explicit view/model leases |
| Unbounded background parallelism makes the IDE slower | Bounded priority queues, cancellation, measurements, and single-writer mutation boundaries |
| PhotinoXDX/WebView platform differences | Run host smoke tests on every supported OS from phase 1 onward |
| Roslyn/MSBuild state diverges from dirty buffers | One workspace coordinator with versioned mappings and fixture solutions |
| Terminal/debug child processes leak or target unrelated processes | Explicit process-tree ownership and adversarial cleanup tests |
| Razor projections map edits or diagnostics incorrectly | Versioned host/projected ranges and round-trip mapping fixtures |
| Extension API freezes internal design too early | Expose a small capability API only after internal providers have shipped |
| Late workbench persistence causes incompatible state | Version schemas from their first introduction and test migrations |

## Open decisions

Resolve these before the named phase starts:

1. Phase 1: Monaco version/build tool, packaged worker URLs, content security policy, and supported WebView versions.
2. Phase 2: edit-journal persistence boundary, encoding fallback, and settings format/location. Monaco owns live text and undo/redo.
3. Phase 6: MSBuild discovery/evaluation library, supported SDK/project types, and multi-target context policy.
4. Phase 11: terminal emulator implementation or dependency, licensing, and PTY/conpty strategy.
5. Phase 12: debug adapter/engine, protocol transport, redistribution/licensing, attach permissions, and capability fallback.
6. Phase 15: Razor projection ownership. Protocol-based, pinned Roslyn/Razor acquisition is fixed by the language-server asset manifest.
7. Phase 16: in-process versus isolated extension host, trust model, permissions, compatibility, and signing policy.
8. Phase 17: application identity, versioning, package formats, signing/notarization, update channel, and support lifetime.

## Preview definition

Preview is reached only when phases 1–17 are complete, the supported platform matrix is green, clean install/update/uninstall paths pass, persisted-state migration and crash recovery pass, security and license reviews have no release blockers, and known limitations are published.
