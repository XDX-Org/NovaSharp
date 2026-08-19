# NovaSharp agent requirements

These requirements apply to the entire repository. The words **must** and **must not** are mandatory.

## Required reading

Before planning or changing NovaSharp, read:

1. `docs/decisions/0001-monaco-editor.md`
2. `docs/ide-roadmap-research.md`
3. `docs/delivery-plan.md`
4. The phase document governing the requested work

If implementation and documentation disagree, stop and resolve the architecture discrepancy in the same change.

## Editor architecture

- Monaco Editor must be the only source editor from phase 1. Do not add a textarea, custom token-row renderer, or second production
  editor path.
- Monaco must own live text, undo/redo, selections, IME, viewport rendering, lexical/semantic token painting, editor-local widgets,
  and editor accessibility.
- NovaSharp must use Monaco public APIs: language providers, semantic tokens, markers, and decoration collections. Do not recreate
  syntax/semantic colours or completion/hover/signature UI as Blazor overlays.
- Use one Monaco `ITextModel` per canonical document URI. Split views must attach separate editor instances to the shared model.
- Typing must not synchronously call .NET, send full document values, or trigger Blazor rendering. Replicate ordered incremental edits
  through the bounded asynchronous pump defined by ADR 0001.
- Monaco ESM assets, selected languages, workers, fonts, and notices must be pinned and packaged locally. Do not use a runtime CDN,
  the deprecated AMD build, private VS Code APIs, or main-thread worker fallback.
- Roslyn remains the project-aware C# semantic authority. Registering Monaco's C# language definition does not replace Roslyn IntelliSense.

## Async, concurrency, and performance

- Use asynchronous APIs end to end for file, watcher, process, terminal, adapter, and network I/O. Do not use `.Result`, `.Wait()`,
  sync-over-async, synchronous polling, or UI-thread I/O.
- Keep Monaco/browser and Blazor renderer callbacks short. CPU-heavy parsing, project evaluation, Roslyn analysis, indexing, search,
  serialization, and protocol work must run on bounded background workers.
- Parallelize independent work only up to measured limits. Shared mutable state must have an explicit owner and normally use a
  single-writer coordinator.
- Every supersedable operation must carry cancellation and a source version. Reject stale results before publishing them.
- Queues, channels, workers, caches, output, models, snapshots, and pending requests must have explicit bounds and backpressure
  behavior. Never trade UI latency for an unbounded backlog.
- Foreground editing and navigation must take priority over diagnostics, indexing, restore, and speculative work.
- Do not hold locks across `await`. Shutdown must cancel producers, complete channels, await owned consumers with deadlines, and
  dispose resources in dependency order.
- Performance claims require numeric budgets on named fixtures/hardware. Test typing while background workloads are active, not only
  at idle.

## Changes and verification

- Follow `.editorconfig`; keep files LF-terminated with a final newline.
- Preserve unrelated user changes and keep changes within the requested phase/scope.
- Update the governing phase document and ADR when an architectural contract changes.
- Prefer behavior/integration tests over tests tied to private Monaco DOM or implementation details.
- Before handoff, run `git diff --check`, the relevant tests, and `dotnet build NovaSharp.slnx`. When `package.json` exists, also run the
  pinned frontend install/build and worker/package checks documented by phase 1. Dependency/bootstrap changes must run the platform
  setup script successfully from acquisition through build.
- A phase is not complete while its performance, cancellation, disposal, packaged-worker, accessibility, or supported-platform gates
  remain unverified.
