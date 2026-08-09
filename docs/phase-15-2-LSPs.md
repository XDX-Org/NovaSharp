# Phase 15.2: real language servers

## Status

Planned. This phase reopens the language-service completion claims from phases 7, 8, and 15. Those features exist, but are not production-complete until backed by the servers selected here.

## Goal

Make Language Server Protocol processes the sole source of editor language intelligence for every supported programming or web language. Remove NovaSharp's handwritten Razor/HTML/CSS parser and direct in-process Roslyn feature provider. If a server is missing or unhealthy, documents remain editable with an explicit unavailable state; NovaSharp must not fabricate partial results.

Build diagnostics remain a separate producer because they describe an explicit build, not live editor intelligence.

## Non-goals

- Implementing language semantics, Razor projections, or HTML/CSS parsing in NovaSharp.
- Reusing binaries from an installed VS Code or another IDE.
- Downloading executables or packages at application runtime.
- Adding JavaScript, TypeScript, JSON, XML, or user-configurable third-party servers in this phase.
- Exposing this client as the phase 16 extension API before its trust model is settled.

## Server selection

| Documents | Server | Required source | NovaSharp responsibility |
|---|---|---|---|
| `.cs` | Microsoft Roslyn Language Server | Pinned redistributable Roslyn server artifact | Hosting, standard/Roslyn client methods, solution selection, synchronization, conversion |
| `.razor`, `.cshtml` | Razor cohost extension in the Roslyn server | Version-matched redistributable Razor extension | Extension activation, required client methods, project readiness, ordinary LSP routing |
| `.html`, `.htm` | VS Code HTML language server | Pinned build of Microsoft's MIT-licensed server and language service | Runtime packaging, settings/custom-data policy, LSP routing |
| `.css` | VS Code CSS language server | Pinned build of Microsoft's MIT-licensed server and language service | Runtime packaging, settings, LSP routing |
| Unregistered text | None | None | Editing only; no language-service claims |

Razor compiler and tooling development moved into Roslyn. Current VS Code C# support loads Razor as a Roslyn language-server extension rather than obtaining Razor support from the installed .NET SDK. The HTML and CSS language services contain VS Code's language intelligence, while VS Code supplies executable LSP hosts around them.

Before implementation, an acquisition spike must record exact versions, artifact URLs/feeds, licenses, notices, hashes, supported RIDs, commands, and the Razor extension contract. A preview/private feed is not acceptable for release unless policy explicitly permits it and packages pinned artifacts reproducibly.

Primary references:

- [Language Server Protocol](https://microsoft.github.io/language-server-protocol/)
- [Roslyn](https://github.com/dotnet/roslyn)
- [Razor tooling relocation](https://github.com/dotnet/razor)
- [Official VS Code C# client](https://github.com/dotnet/vscode-csharp)
- [VS Code HTML server](https://github.com/microsoft/vscode/tree/main/extensions/html-language-features/server)
- [VS Code HTML language service](https://github.com/microsoft/vscode-html-languageservice)
- [VS Code CSS server](https://github.com/microsoft/vscode/tree/main/extensions/css-language-features/server)
- [VS Code CSS language service](https://github.com/microsoft/vscode-css-languageservice)

## Architecture

### Process and session ownership

Add one application-owned Roslyn/Razor process per workspace and one process per required web server. Processes are shared across groups and tabs. `LanguageServerManager` owns discovery, launch, initialize, health, restart, shutdown, and disposal; UI components never own them.

Use stdio with LSP `Content-Length` framing unless a selected server requires a documented pipe transport. Standard output is protocol-only; bounded stderr goes to a language-server log. Launch with explicit paths, argument arrays, working directories, environment allowlists, and NovaSharp's process ID. Kill only the owned process tree after failed initialization, forced restart, workspace replacement, or shutdown.

Extract or add a shared child-process abstraction instead of extending the build/run service's private process code. It needs asynchronous stdio, bounded stderr, graceful deadlines, tree termination, and exit observation.

### Protocol client

Use a maintained JSON-RPC transport, not a handwritten request loop. Keep strict LSP models and conversion tests. Support at minimum:

- `initialize`, `initialized`, `shutdown`, and `exit`;
- request IDs, errors, `$/cancelRequest`, and late-response rejection;
- `window/logMessage`, `window/showMessage`, `$/progress`, and work-done progress creation;
- dynamic capability registration/unregistration;
- configuration, workspace folders, apply-edit, and watched-file requests/notifications;
- `didOpen`, negotiated full/incremental `didChange`, `didSave`, and `didClose`;
- push diagnostics and pull diagnostics when advertised;
- UTF-16 positions, encoded file URIs, line-ending conversion, and surrogate pairs;
- server capability negotiation rather than hard-coded feature claims.

Unknown requests receive method-not-found and a bounded log entry. Optional features stay disabled until advertised. Experimental/vendor methods live in server-specific adapters.

### Razor cohosting

Razor must use the version-matched cohost extension loaded into Roslyn. NovaSharp will not recreate projected documents. The acquisition spike must trace the official client's initialization options, extension arguments, project-context requests, delegated HTML behavior, generated-document requests, dynamic registrations, and workspace notifications.

Implement every required client-side Razor method found in the pinned open-source client/server pair. Contract tests replay source-redacted message sequences. `.razor` and `.cshtml` are not ready until the cohost reports usable project context; regex or generated-file fallbacks are forbidden.

### HTML and CSS

Package the official VS Code HTML/CSS server implementations or reproducible NovaSharp-owned executable wrappers around the official language-service packages. A wrapper may expose the library through LSP but may not implement language semantics.

Prefer one pinned Node runtime shared by both servers if required. Bundle it for every RID; never require system Node. Lock dependencies, use production-only installs, retain notices, and prohibit unaudited lifecycle scripts. Custom HTML data and remote fetching remain disabled until workspace-trust and network policies exist.

### Document and workspace synchronization

Add `LanguageDocumentCoordinator` as the sole bridge from `EditorDocumentState` to servers:

1. Normalize each path to one canonical file URI.
2. Assign an LSP version independent of disk timestamps.
3. Send `didOpen` once after initialization.
4. Send ordered `didChange` before dependent requests.
5. Send `didSave` only after a successful write.
6. Send `didClose` after the final view closes or workspace changes.

Views of the same document share one synchronized instance. Preview tabs, Save As, rename, external reload/deletion, project reload, and dirty-buffer preservation need explicit transitions. Restarts replay current memory text; disk must never replace a dirty buffer.

`RoslynProjectSystem` may temporarily remain for explorer/build data and generated-file display, but language requests must not query its Roslyn documents. Measure duplicate workspace cost; replace its language-oriented workspace with lightweight project evaluation if the memory budget fails.

### Feature routing

Replace provider implementations with an LSP adapter. A small editor-facing abstraction may remain only as a lossless adapter over negotiated capabilities.

| NovaSharp feature | LSP method/notification |
|---|---|
| Completion/details | `textDocument/completion`, `completionItem/resolve` |
| Signature help | `textDocument/signatureHelp` |
| Hover | `textDocument/hover` |
| Semantic highlighting | `textDocument/semanticTokens/full`, `/delta`, or `/range` |
| Live diagnostics | `publishDiagnostics` and/or `textDocument/diagnostic` |
| Formatting | `textDocument/formatting`, `textDocument/rangeFormatting` |
| Definitions | `definition`, `typeDefinition`, `implementation` |
| References | `textDocument/references` |
| Symbols | `documentSymbol`, `workspace/symbol`, `workspaceSymbol/resolve` |
| Rename | `prepareRename`, `rename` |
| Code actions | `codeAction`, `codeAction/resolve` |

Preserve insert-text format/mode, snippets, text and additional edits, commands, markup, location links, diagnostic metadata, disabled reasons, and change annotations. Expand the current reduced records wherever flattening changes behavior.

### Workspace edits

Support `changes` and `documentChanges`, including ordered create/rename/delete operations, versioned edits, annotations, and negotiated resource operations. Validate targets against the workspace, reject stale/overlapping edits, preview multi-file changes, apply atomically where possible, and report partial failure honestly.

Server `workspace/applyEdit`, rename, and code actions use one validator/preview path. Servers never write through a NovaSharp callback without policy checks.

### Diagnostics

Replace polling with server push or negotiated pull diagnostics. Key by server instance, URI, version/result ID, and identity; clear only that producer's prior result. Reject closed, stale, or mismatched results.

Map severity, code, source, tags, related information, and code-description URLs. Build diagnostics coexist independently. After a crash, mark retained diagnostics stale; clear them if restart fails or after resynchronization.

### UI and configuration

Add per-server status/output with `Starting`, `Loading workspace`, `Ready`, `Restarting`, `Unavailable`, and `Stopped`. Show negotiated name/version and actionable failures. Add restart, open-log, and source-redacted support-report commands.

Settings cover trace level (default off), development-only executable/extension overrides, server configuration, and bounded automatic restart. Overrides warn about untrusted binaries and never become packaged defaults. Protocol traces are bounded/off by default because payloads contain source and paths.

## Files and components

### Add

- `LanguageServers/LanguageServerManager.cs`
- `LanguageServers/LanguageServerProcess.cs`
- `LanguageServers/LspClient.cs`, `LspProtocol.cs`, and `LspConverters.cs`
- `LanguageServers/LanguageDocumentCoordinator.cs`
- `LanguageServers/LspLanguageProvider.cs`
- `LanguageServers/RoslynRazorServerAdapter.cs` and `WebServerAdapter.cs`
- `LanguageServers/LspWorkspaceEditHandler.cs`
- `LanguageServers/LspDiagnosticPublisher.cs`
- `LanguageServers/LanguageServerCatalog.cs`
- `Components/LanguageServerStatus.razor`
- controllable fake-server and real-server integration fixtures
- reproducible acquisition/verification tooling and third-party notices

Names may change, but each responsibility stays separately testable.

### Change

- `NovaSharp.csproj`: JSON-RPC dependency, assets, publish rules, notices, RID-specific files.
- `Program.cs` and workbench composition: manager construction, workspace lifecycle, asynchronous disposal.
- `EditorDocumentState.cs`, tabs, and file operations: canonical lifecycle events.
- `CodeEditor.razor`: negotiated triggers, snippets/edits, push diagnostics, token legends/deltas, capability changes.
- `LanguageFeatures.cs`: replace direct Roslyn implementations and expand lossy records.
- `LanguageDiagnostics.cs`: server identity, metadata, result IDs, stale state.
- `WorkspaceEdits.cs`: complete LSP edit/resource-operation support.
- `ProjectSystem.cs`: notify the server and stop serving editor language requests.
- settings schema, status/output UI, smoke models, CI, manifests, and docs.

### Remove after parity gates

- `CSharpLanguageProvider` and direct editor-feature calls into Roslyn.
- `WebLanguageProvider`, `WebProjectionParser`, regex diagnostics, manual discovery, and handcrafted web features.
- approximation retention metrics and tests.
- phase 7/8/15 smoke assumptions tied to in-process providers.

Old providers may exist temporarily behind a build-only comparison harness. Release builds must contain no fallback or setting that re-enables them.

## Delivery slices

1. **Artifact/license spike:** prove acquisition, launch, initialization, Razor loading, and redistribution on all RIDs.
2. **Generic client:** lifecycle, framing, cancellation, registration, configuration, progress, logs, fake-server tests, cleanup.
3. **Document coordinator:** URIs, versions, sync, multi-view ownership, Save As/reload/replay, watched files.
4. **C# migration:** all currently advertised language features through Roslyn LSP.
5. **Razor migration:** cohost/project context, components/tag helpers, mixed languages, formatting/navigation/rename.
6. **HTML/CSS migration:** packaged official servers and all advertised capabilities.
7. **Edit fidelity:** snippets, extra edits, commands, annotated multi-file edits, resource operations, stale rejection.
8. **Operations/UX:** status, logs, backoff, crash recovery, settings migration, offline behavior.
9. **Removal/release:** delete approximations, supersede ADR 0006, replace tests, verify notices/SBOM and packages.

Each slice must leave release builds wholly on the old route or a complete LSP route; no release commit silently mixes providers by feature.

## Failure and security policy

- Initialization is timed/cancellable; typing never waits synchronously.
- Exponential restart backoff stops after five crashes in 180 seconds; manual restart resets it.
- Hung requests cancel/time out without killing a healthy server; transport failure restarts it.
- Shutdown sends `shutdown`, then `exit`, waits briefly, then terminates only the owned tree.
- Assets are pinned and hash-verified at build time; runtime acquisition is forbidden.
- Environment variables are allowlisted; secrets are not inherited blindly.
- File reads, settings, edits, commands, and network requests pass explicit policy.
- Logs redact source, secrets, user names, and absolute paths by default.
- Unknown commands/URI schemes are rejected; file operations cannot escape the workspace without approval.

## Tests

### Protocol/unit

- Fragmented framing, UTF-8 lengths, concurrency, out-of-order/error responses, cancellation, malformed messages, EOF, stderr floods.
- Negotiation, dynamic registration, progress, configuration, apply-edit, and unknown methods.
- UTF-16 around emoji/combining text, CRLF, encoded paths, Windows drives/UNC, case sensitivity.
- Edit ordering, multiple views, restart replay, Save As, rename, reload/deletion, stale responses.
- Snippets/additional edits, token deltas, location links, diagnostic metadata, workspace operations.
- Crash loops, timeouts, graceful/forced cleanup, workspace replacement, disposal.

### Real-server integration

- C# console/library, multi-project/target, linked files, analyzers, nullable, broken projects.
- Blazor Web App, Razor Pages, libraries, tag helpers, imports/layouts, generic components, embedded expressions, `@code`, generated documents, rapid mixed edits.
- HTML/CSS malformed syntax, completion, hover, formatting, symbols, navigation, rename, actions.
- Unsaved cross-file changes affect completion, diagnostics, navigation, references, and rename.
- Restart/reload preserves dirty text and recovers without restarting NovaSharp.
- No `WEB001`, `WEB002`, `CSS001`, or `CSS002` originates from NovaSharp.

Fake servers prove client correctness; pinned real servers prove compatibility. Mock-only acceptance is insufficient.

### Packaged interaction

Run published artifacts on Windows x64, Linux x64, macOS x64, and macOS arm64 with networking disabled. Verify C#/Razor readiness, completion, diagnostics, navigation, formatting, clean shutdown, and no orphan processes.

## Budgets

| Metric | Budget |
|---|---:|
| UI work added by one buffer change | 16 ms p95 |
| Process launch to initialized | 5 s |
| Workspace open to C# ready | 15 s |
| Workspace open to Razor ready | 20 s |
| Warm completion | 750 ms p95 |
| Cold completion after ready | 3 s |
| Hover/signature after ready | 1 s p95 |
| Diagnostics after settled edit | 3 s |
| Crash restart and resync | 10 s |
| Graceful shutdown | 3 s, then tree termination |
| Retained protocol log | 2 MiB/server |
| Combined server working set after settling | 1.5 GiB on medium fixture |

CI records distributions and process working sets; one fast request cannot hide slow startup or growth.

## Completion criteria

- Every advertised C#, Razor/CSHTML, HTML, and CSS feature comes from a negotiated capability and pinned server.
- Release binaries contain no handwritten or direct-Roslyn editor provider and no silent fallback.
- Razor behavior matches the pinned official server on representative projects.
- Synchronization, diagnostics, edits, Unicode, cancellation, restart, and shutdown pass fake/real tests.
- Packages work offline and include assets, licenses, notices, hashes, and SBOM entries.
- Four-platform tests and packaged gates pass without orphan processes or dirty-buffer loss.
- ADR 0006 is superseded; phase 7, 8, and 15 docs identify the new authority.
- All budgets pass with named fixture/runner evidence.

## Blocking decisions

Resolve in the artifact spike before feature work:

1. Exact Roslyn/Razor versions and stable redistributable source.
2. Direct consumption versus reproducible source builds for HTML/CSS servers.
3. Bundled Node/runtime strategy and package-size impact.
4. JSON-RPC library after cancellation/server-request/progress/framing prototypes.
5. Custom Roslyn/Razor solution and client methods for the pinned pair.
6. Whether duplicate `MSBuildWorkspace` state meets memory limits.

No unresolved item may be hidden behind a local parser.
