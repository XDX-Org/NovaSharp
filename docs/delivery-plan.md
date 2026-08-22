# Delivery plan

## Product boundary

The preview target is a cross-platform IDE for local, SDK-style .NET development with C#, Razor/Blazor, HTML, and CSS. It includes editing, project loading, language services, search, build/run, terminal, managed debugging, durable settings/layouts, and a minimal extension SDK.

Source control UI, remote development, collaboration, notebooks, AI completion, designers, profiling, multiple native windows, non-.NET debugging, and non-SDK project systems are not preview requirements.

## Status

| Phase | Status | Exit evidence |
|---|---|---|
| 1. Monaco single-file editor shell | In progress | Monaco is the only editor. `dotnet test` runs 217 assertions; the browser suite runs 64 interaction, worker, bounded-replication, performance, and disposal gates; and the published native application has an unattended cold/warm smoke path. The local `win-x64` upstream-host fixture passes. Outstanding: one green qualification run of the gates on every matrix row |
| 2. Editor and file lifecycle | In progress | The document lifecycle and all three cross-cutting foundations are implemented. CI contains the native smoke, browser metrics, managed replication/save measurements, six RID-specific publishes, and retained JSON evidence. The named local `win-x64` upstream-host fixture passes every phase-2 budget. Outstanding: one green qualification run of the gates on every matrix row |
| 3–17 | Planned | Completion criteria not yet met |

Status values are `planned`, `in progress`, `blocked`, and `complete`. Update this table only from test or release evidence; documentation, packaged dependencies, or partial UI alone does not complete a phase. A phase is never `complete` on evidence from a single operating system.

## Cross-cutting foundations

These are not separate feature phases. Introduce each boundary by the indicated phase and retain it thereafter.

| Foundation | Required by | Minimum contract |
|---|---:|---|
| Dependency bootstrap | 1 | One documented command acquires hash-pinned Monaco, Roslyn/Razor, Node, and web-language assets for the current RID |
| Monaco host and asset pipeline | 1 | Exact npm lock, local ESM bundle, functioning editor worker, deterministic disposal, no runtime CDN |
| Async work scheduler | 1 | UI/background priority lanes, bounded queues, cancellation, backpressure, ownership, and observable saturation |
| Platform abstraction | 1 | One seam for paths, URIs, line endings, file casing, dialogs, and process launch. Product code contains no operating-system branches |
| Command registry | 2 | Stable command ID, handler, enablement, keybinding, menu/palette metadata |
| Configuration service | 2 | Typed defaults, user/workspace scopes, validation, atomic versioned storage |
| Notification and logging | 2 | Structured severity, actionable errors, bounded local logs, source-text redaction |
| Lifetime/task coordinator | 1 | Ownership, cancellation, stale-result rejection, disposal diagnostics |
| Persistence service | 3 | Versioned schemas, portable workspace paths, atomic writes, corruption fallback |
| Diagnostic store | 6 | Results keyed by producer, context, document version, and stable identity |
| Process service | 10 | Argument arrays, explicit environment/working directory, process-tree ownership |
| Capability/extension boundary | 7 | Internal provider contracts that can later be exposed selectively by phase 16 |

Record decisions that constrain multiple phases as short ADRs under `docs/decisions/`. The editor decision is fixed by
[ADR 0001](decisions/0001-monaco-editor.md), the document lifecycle — replication durability, text encoding, and
settings storage — by [ADR 0002](decisions/0002-document-lifecycle.md), and the desktop host by
[ADR 0003](decisions/0003-desktop-host.md). Decide C# language-service hosting before phase 6, terminal engine before
phase 11, and debug adapter before phase 12. A dependency that presupposes one of those answers must not be added to a
project file before its ADR exists.

## Required execution model

- Monaco's browser thread owns typing, selection, IME, undo/redo, token rendering, editor widgets, and viewport work. No .NET round trip is allowed in the keystroke-to-paint path.
- JavaScript sends ordered incremental change batches to .NET without waiting for Blazor rendering. The receiver is a bounded single-writer queue per document; coalesce safe adjacent notifications and request a full resynchronization if a sequence gap occurs.
- File, watcher, process, terminal, adapter, and network I/O use asynchronous APIs end to end. Do not block with `.Result`, `.Wait()`, synchronous polling, or UI-thread file access.
- CPU-heavy project evaluation, Roslyn analysis, indexing, search, parsing, and serialization run on bounded background workers. Parallelize independent work only; coordinators that mutate shared state remain single-writer.
- Foreground requests have priority over speculative/background work. Every supersedable operation carries cancellation and a source version; stale results are discarded before publication.
- Queues, caches, task counts, worker counts, and retained snapshots are bounded. Overload degrades by canceling/coalescing background work, never by accumulating an unbounded backlog.
- UI state is published as small immutable snapshots. Background workers do not mutate Blazor component state and marshal only the final state change to the renderer.

## Supported platform matrix

Every runtime identifier in this table is a first-class target. Ordering is alphabetical and carries no priority. The root
[README](../README.md#supported-platforms) publishes the contributor-facing view of the same rows.

| Runtime identifier | Pinned assets | Minimum OS version | CI image | Packaging format | Automated smoke test |
|---|---|---|---|---|---|
| `linux-arm64` | Yes | Ubuntu 24.04 LTS | `ubuntu-24.04-arm` | Record before phase 17 | Wired; upstream-host run pending |
| `linux-x64` | Yes | Ubuntu 24.04 LTS | `ubuntu-24.04` | Record before phase 17 | Wired; upstream-host run pending |
| `osx-arm64` | Yes | macOS 15 | `macos-15` | Record before phase 17 | Wired; upstream-host run pending |
| `osx-x64` | Yes | macOS 15 | `macos-15-intel` | Record before phase 17 | Wired; upstream-host run pending |
| `win-arm64` | Yes | Windows 11 24H2 | `windows-11-arm` | Record before phase 17 | Wired; upstream-host run pending |
| `win-x64` | Yes | Windows 10 version 1809 | `windows-2025` | Record before phase 17 | Wired; upstream-host run pending |

CI images are the runner labels [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) uses. Every row runs the same
gates from the same commit: its bootstrap entry point end to end, `dotnet test`, the `tests/editor-host` browser suite,
RID-specific publish, and the published native-host smoke and budgets. The workflow retains the payload and JSON
measurement records per RID. Minimum OS versions are conservative product support boundaries, not aliases for runner
images; lowering one requires an equivalent smoke fixture on that older host.

For native qualification only, the macOS rows stage the RID publish in a minimal ad hoc-signed application bundle, as
required by the pinned window host. This test scaffold does not decide the release packaging or signing format owned by
phase 17. The app host remains under `Contents/MacOS`; publish data is sealed under `Contents/Resources` and linked into
the host's base directory. Unused npm command-shim directories receive resource-safe names because Apple otherwise
interprets their dotted names as nested bundles during signing. The smoke launches the upstream host executable from
the signed layout and waits on NovaSharp's bounded result-file/process contract, so the verifier owns the process and
its deadline directly.

### Parity rule

- A platform is supported only when its host prerequisites, bootstrap route, packaging format, and unattended smoke test are all
  documented and passing. Anything less is `planned`, never `partially supported`.
- A feature is not complete while it works on some rows and not others. If a capability genuinely cannot exist on a platform, record the
  gap and its user-visible behavior in the same change that introduces the capability.
- Product code must not branch on the host operating system. Differences belong behind the platform abstraction seam, which is tested
  directly.
- Bootstrap, build, and packaging entry points must be equivalent across shells. Adding a capability to one entry point without the other
  is an incomplete change.
- Documentation must present platforms symmetrically. No operating system is the default, the reference, or the one whose instructions
  appear first by convention.
- Performance budgets are recorded per platform. A budget met on one operating system says nothing about the others.

## Quality gates

Every phase must pass:

- A clean build with warnings either fixed or linked to an accepted, time-bounded issue.
- A test project in `NovaSharp.slnx` that `dotnet test NovaSharp.slnx` executes. A phase with no runnable test project cannot pass any gate below it.
- Unit tests for state and algorithms; integration tests for service boundaries; interaction tests for the phase's primary user flow.
- Cancellation, disposal, error recovery, keyboard access, and restored-state tests where applicable.
- Tests prove the UI/Monaco thread is not blocked, stale work is rejected, queues remain bounded, and concurrent completion order cannot corrupt state.
- Every gate above passes on every supported runtime identifier, from the same commit, without platform-specific test exclusions.
- A runtime-identifier-specific publish produces a complete payload, and a publish without one fails rather than shipping an incomplete application.
- No secrets, source text, absolute build-machine paths, or developer directory layouts in shipped binaries or telemetry; telemetry remains opt-in.
- Updated status, user documentation, known limitations, and migration notes for changed persisted schemas.

Budgets must be measured on named fixture hardware and repositories. Set numeric budgets before implementing the affected phase:

| Budget | Set no later than | Recorded in |
|---|---:|---|
| Startup time and idle memory | 2 | [Phase 2](phase-02-editor-file-lifecycle.md#performance-budgets) |
| Typing/render latency and large-file memory | 2 | [Phase 2](phase-02-editor-file-lifecycle.md#performance-budgets) |
| Explorer expansion and watcher recovery | 3 |
| Solution load, Roslyn snapshot count, completion first result | 6 |
| Search throughput/result memory | 9 |
| Build cancellation/process cleanup | 10 |
| Terminal input/resize latency | 11 |
| Debug step/evaluate latency | 12 |
| Crash recovery and full-workbench memory | 14 |

`tools/NovaSharp.PhaseVerification` first provisions a disposable browser profile, then launches fresh processes for
the cold, three warm samples, and generated 10 MB fixtures before measuring managed replication and a 1 MB atomic save.
Provisioning is retained as a separate functional result; the measured launches share its profile, and the warm gate
uses the retained samples' median. `tests/editor-host` records paint,
browser-thread, replication, queue, and 100-cycle heap measurements. CI names the runner fixture and uploads both JSON
records with their fixture-specific limits; a missing record or a budget failure fails that RID's job.

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
| Photino/WebView platform differences | Run host smoke tests on every supported OS from phase 1 onward |
| Development converges on one operating system and the others rot | Keep the platform matrix, both bootstrap entry points, and CI green together; treat a single-platform result as no result |
| Dependencies commit to an architecture before its ADR exists | Project files may not reference a language-service, terminal, or debugger implementation until the governing decision is recorded |
| Roslyn/MSBuild state diverges from dirty buffers | One workspace coordinator with versioned mappings and fixture solutions |
| Terminal/debug child processes leak or target unrelated processes | Explicit process-tree ownership and adversarial cleanup tests |
| Razor projections map edits or diagnostics incorrectly | Versioned host/projected ranges and round-trip mapping fixtures |
| Extension API freezes internal design too early | Expose a small capability API only after internal providers have shipped |
| Late workbench persistence causes incompatible state | Version schemas from their first introduction and test migrations |

## Open decisions

Resolve these before the named phase starts:

1. Phase 1: Monaco version/build tool, packaged worker URLs, content security policy, and the minimum WebView version on each supported platform.
2. ~~Phase 2: edit-journal persistence boundary, encoding fallback, and settings format/location.~~ Resolved by
   [ADR 0002](decisions/0002-document-lifecycle.md): the journal is in memory only and crash recovery is phase 14's,
   the encoding surface is the framework's whole catalogue with a byte-preserving fallback, and settings are versioned
   JSON in a user and a workspace scope.
3. Phase 6: C# language-service hosting — the pinned out-of-process Roslyn language server, in-process `Microsoft.CodeAnalysis.Workspaces.MSBuild`, or a defined split. The project file currently references both; that must be resolved to one recorded decision, with the unused dependencies removed.
4. Phase 6: MSBuild discovery/evaluation library, supported SDK/project types, and multi-target context policy.
5. Phase 11: terminal emulator implementation or dependency, licensing, and the pseudoterminal strategy for every supported platform.
6. Phase 12: debug adapter/engine, protocol transport, redistribution/licensing, attach permissions, and capability fallback.
7. Phase 15: Razor projection ownership. Protocol-based, pinned Roslyn/Razor acquisition is fixed by the language-server asset manifest.
8. Phase 16: in-process versus isolated extension host, trust model, permissions, compatibility, and signing policy.
9. Phase 17: application identity, versioning, package formats, signing/notarization, update channel, and support lifetime.

## Preview definition

Preview is reached only when phases 1–17 are complete, every row of the supported platform matrix is green, clean install/update/uninstall paths pass, persisted-state migration and crash recovery pass, security and license reviews have no release blockers, and known limitations are published.
