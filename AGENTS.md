# NovaSharp agent requirements

These requirements apply to the entire repository. The words **must** and **must not** are mandatory.

## Required reading

Before planning or changing NovaSharp, read:

1. `docs/decisions/0001-monaco-editor.md`
2. `docs/ide-roadmap-research.md`
3. `docs/delivery-plan.md`, including the supported platform matrix and its parity rule
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

## Cross-platform requirements

NovaSharp is a cross-platform IDE. Every runtime identifier in the supported platform matrix is a first-class target; none is the
reference platform, and none is the one that gets fixed first.

- Product code must not branch on the host operating system. Platform differences belong behind one abstraction seam covering paths,
  URIs, path comparison and casing, line endings, file watching, dialogs, and process launch. That seam is tested directly.
- Do not hard-code path separators, drive letters, home-directory shapes, executable extensions, case-sensitivity assumptions, or
  shell names. Do not assume a file system is case-insensitive or case-sensitive.
- Bootstrap, build, packaging, and CI entry points must stay equivalent across shells. Adding a capability to one entry point without
  the other is an incomplete change, as is a prerequisite check that exists in only one.
- A feature is not complete while it works on some platforms and not others. When a capability genuinely cannot exist somewhere, record
  the gap and its user-visible behavior in the same change that introduces the capability.
- Documentation must present platforms symmetrically: alphabetical or otherwise neutral ordering, no operating system named as the
  default, and no instructions written for one platform with the others handled as an aside.
- Performance budgets, smoke tests, and completion evidence are per platform. A result from one operating system is not a result for
  the others.
- Publishing requires an explicit runtime identifier. A publish without one must fail rather than emit an application missing its
  runtime-identifier-specific assets.
- Shipped binaries must not contain build-machine paths or developer directory layouts. Development-only asset roots belong to Debug
  configurations.

## Async, concurrency, and performance

- Use asynchronous APIs end to end for file, watcher, process, terminal, adapter, and network I/O. Do not use `.Result`, `.Wait()`,
  sync-over-async, synchronous polling, or UI-thread I/O.
- The single exception is final shutdown at the process entry point, which must block with an explicit deadline. `Main` must remain a
  synchronous `[STAThread]` method: the compiler does not carry `[STAThread]` onto the entry point it synthesises for an
  `async Task Main`, so an asynchronous entry point starts the process in a multi-threaded apartment and the window host fails with
  `RPC_E_CHANGED_MODE`. A test asserts the built entry point still carries the attribute.
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

- Follow `.editorconfig`; keep files LF-terminated with a final newline. After changing `.gitattributes`, renormalize the tree in the
  same change so later edits do not produce whole-file whitespace diffs.
- Preserve unrelated user changes and keep changes within the requested phase/scope.
- Update the governing phase document and ADR when an architectural contract changes. Documentation that describes a layout, command, or
  file the change did not produce is a discrepancy to resolve, not a plan to leave in place.
- The commit subject must describe what the change actually does. A change that packages a dependency without using it is a bootstrap
  change, not the feature the dependency is for.
- Do not add a dependency that presupposes an unrecorded architectural decision. If the governing ADR does not exist yet, write it or
  leave the dependency out.
- `NovaSharp.slnx` must contain a test project that `dotnet test NovaSharp.slnx` executes. Do not add a quality gate to a phase document
  without somewhere for it to run.
- Prefer behavior/integration tests over tests tied to private Monaco DOM or implementation details.
- Before handoff, run `git diff --check`, `dotnet test NovaSharp.slnx`, and `dotnet build NovaSharp.slnx`. When `package.json` exists,
  also run the pinned frontend install/build and worker/package checks documented by phase 1. Dependency/bootstrap changes must run the
  bootstrap entry point successfully from acquisition through build, and must be applied to every entry point.
- Leave no stale build output, orphaned project directories, or artifacts from other branches in the working tree.
- A phase is not complete while its performance, cancellation, disposal, packaged-worker, accessibility, or supported-platform gates
  remain unverified, or while any of that evidence comes from a single operating system.
