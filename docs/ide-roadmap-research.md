# IDE roadmap research and architecture

## Product model

NovaSharp separates four kinds of state:

1. A **workspace** owns folders, solutions, projects, files, configuration, and services.
2. A **document record** owns canonical identity, encoding, line endings, dirty/save state, disk metadata, and the lease on one Monaco model.
3. An **editor view** owns an editor instance, cursor/selection, scroll position, and a reference to a document.
4. An **editor group** owns an ordered tab list and one active editor view.

This separation allows a tab to move without recreating its model, two editors to show one shared model, and resources to be disposed only after the last lease closes. VS Code likewise treats splits as editor groups that contain items, while Visual Studio distinguishes tab groups from independently scrolling views ([VS Code UI](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio editor windows](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-manage-editor-windows?view=visualstudio)).

Use canonical URI-like document IDs. A dirty Monaco model wins over filesystem watcher events until the user explicitly reloads or resolves the conflict.
Since Monaco model URIs are immutable, a file relocation drains that document's ordered replica pump, rekeys the
model from its live text, restores the view, and establishes the new sequence from one snapshot resynchronization.

## Monaco editor boundary

NovaSharp uses Monaco Editor from phase 1. Monaco is the editor extracted from VS Code, its models hold content and edit history, and each model has a unique URI ([Monaco concepts](https://github.com/microsoft/monaco-editor#concepts)). Use only Monaco's public, versioned API.

```text
Monaco ITextModel (one per document URI; live text and undo)
       |                         |
       +--> editor view A       +--> editor view B
       |
       +-- ordered edit journal --> bounded .NET replica
                                           |
                          +----------------+----------------+
                          |                                 |
                    file lifecycle                    language services
```

Monaco owns the latency-sensitive browser-side work: text mutation, undo/redo, caret, selections, multi-cursor behavior, clipboard, composition/IME, hit testing, wrapping, viewport rendering, lexical token colors, editor widgets, and editor accessibility. NovaSharp must not reproduce the text, token rows, caret, selection, completion, hover, or signature UI as aligned Blazor layers.

NovaSharp owns file/workspace policy, encoding, line endings, dirty/save state, external conflicts, project context, language-service routing, transactional workspace edits, recovery, and validated persistence. `ITextModel.createSnapshot()` is safe to consume asynchronously, and editor instances can save/restore view state or attach to a model without destroying it ([model API](https://microsoft.github.io/monaco-editor/typedoc/interfaces/editor_editor_api.editor.ITextModel.html), [editor API](https://microsoft.github.io/monaco-editor/typedoc/interfaces/editor_editor_api.editor.ICodeEditor.html)).

Baseline C# colors come from Monaco's C# language definition. Roslyn supplies project-aware semantics. Register semantic tokens and language providers through Monaco's public language API; publish diagnostics as model markers and debugger/editor adornments as decoration collections. Do not merge colors into custom rendered rows. Monaco does not itself provide project-aware C# completion merely because the C# tokenizer is registered.

Ship an exact lockfile-pinned ESM build and its workers locally. AMD is deprecated. Monaco documents that heavy language features use web workers and that failed worker loading can fall back to the main thread, which is unacceptable for NovaSharp's performance target ([Monaco README](https://github.com/microsoft/monaco-editor), [ESM integration](https://github.com/microsoft/monaco-editor/blob/main/docs/integrate-esm.md)). Phase 1 therefore tests the packaged application origin and verifies that the worker actually starts on every supported WebView.

## Edit replication and consistency

Typing changes the Monaco model immediately and never waits for .NET. The JavaScript host assigns a monotonically increasing sequence to each `onDidChangeModelContent` batch. A per-document pump coalesces changes, keeps at most one interop call in flight, and submits ordered UTF-16 range edits to a bounded .NET channel. A single .NET consumer applies batches in order to a shadow snapshot.

The shadow supports Roslyn synchronization, dirty-buffer search, recovery, and workbench commands; it is not a second interactive editor. Save, build, refactor, recovery checkpoint, and workspace-edit operations await a sequence barrier before reading it. Missing/out-of-order sequences reject the incremental batch and request one full Monaco snapshot. They never guess or block typing.

Edits originating in .NET—reload, formatting, rename, code actions, or external conflict resolution—are applied through Monaco edit operations with an origin token and deliberate undo stops. Do not use full `setValue` for ordinary edits because it discards useful edit/undo behavior and moves whole documents across interop.

## Language-service boundary

Roslyn's Workspace API is the solution-wide starting point for C# analysis and refactoring. Its immutable solution snapshots, syntax/semantic models, and diagnostics fit a versioned document service ([Roslyn SDK model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).

```text
Monaco provider request -> NovaSharp provider -> current Roslyn snapshot
          ^                                         |
          +------ response only if version matches -+
```

Completion, hover, signature help, formatting, semantic tokens, diagnostics, definition, references, rename, and code actions use Monaco providers. The provider call captures document URI, active project context, model/shadow version, position/range, cancellation, and request priority. Explicit foreground requests start immediately; speculative requests are cancellable and lower priority. Resolve expensive item details lazily. A response for an old version is discarded before it reaches Monaco.

Use one semantic authority per language and feature. C# uses Roslyn. Razor uses the selected project-aware Razor service. Monaco's packaged HTML/CSS/JSON/TypeScript services may be used where their capabilities meet the roadmap, but disable overlapping providers so duplicate diagnostics or completion cannot appear.

## Async and concurrency model

- The Monaco/browser and Blazor renderer threads only perform short UI work. They never perform file I/O, wait on a worker, parse process output, evaluate projects, or run Roslyn analysis.
- I/O is asynchronous end to end. CPU-bound work runs on explicitly bounded workers, not an unbounded collection of `Task.Run` calls.
- Partition state by ownership: one edit consumer per document, one solution-mutation coordinator, one process-session owner, and immutable result snapshots. This preserves order without global locking.
- Parallelize independent reads such as directory enumeration, project analysis, search shards, and diagnostic producers up to measured limits. Serialize conflicting writes and apply results deterministically.
- Foreground editing/navigation work has priority. Cancel or coalesce superseded diagnostics, indexing, and preview work. Never let background saturation delay typing or explicit completion.
- Bound channels, queues, caches, output, snapshots, models, and concurrency. Expose queue depth, active workers, cancellation, dropped/coalesced work, and end-to-end latency to debug diagnostics.
- Avoid thread-pool starvation: no sync-over-async, long blocking waits, or holding locks across `await`. Libraries that only expose blocking APIs run on a dedicated bounded scheduler.
- Shutdown cancels producers, completes channels, awaits owned consumers with a deadline, and disposes models/workers/processes in dependency order.

Multithreading is a means, not a blanket rule: small ordered mutations stay single-writer, because adding parallel writers would increase contention and correctness risk. Performance gates measure the result rather than thread count.

## Workbench rules

- Every command is invocable independently of its button so menus, shortcuts, Monaco actions, and the command palette share behavior.
- Tree, tabs, splitters, dialogs, editor widgets, and completion lists are keyboard operable with visible focus.
- Services return structured results; UI components do not parse console text when a structured API exists.
- Persist paths portably when they are inside the workspace and validate all restored state.
- Add telemetry only through explicit opt-in; never include source text, paths, or secrets by default.

## Deferred features

Source control UI, remote workspaces, notebooks, collaboration, AI completion, visual designers, profiling, and multiple native windows remain outside the preview roadmap. Razor/HTML/CSS services and extensions follow the stable C# workbench. Internal command, language-provider, document, and workbench boundaries should allow deferred features later without defining speculative public APIs now.
