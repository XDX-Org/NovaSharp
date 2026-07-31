# IDE roadmap research and architecture

## Product model

NovaSharp should separate four kinds of state:

1. A **workspace** owns folders, solutions, projects, files, configuration, and services.
2. A **document** owns identity, text, encoding, dirty state, version, and one editor model.
3. An **editor view** owns cursor, selection, scroll position, and a reference to a document.
4. An **editor group** owns an ordered tab list and one active editor view.

This separation is the prerequisite for moving a tab without recreating its document, showing one document in two views, and disposing resources only when the last view closes. VS Code likewise treats splits as editor groups that contain items and supports moving or copying tabs between groups. Visual Studio distinguishes tab groups from splitting one document into independently scrolling views ([VS Code UI](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio editor windows](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-manage-editor-windows?view=visualstudio)).

Use stable URI-like document IDs based on canonical paths. Keep the in-memory buffer authoritative while dirty; filesystem watchers must never silently replace unsaved text.

## Editor presentation

NovaSharp will use a pure C#/Blazor editor derived from DnSpyXDX's production source-presentation design, with no third-party editor runtime, frontend build toolchain, web worker, or CDN asset.

The relevant DnSpyXDX structure is:

```text
plain document text
        |
        v
indexed document model <--------------+
  line starts, lengths, token state    |
        |                               |
        v                               |
Virtualize<TItem> -> visible line rows |
        |                               |
        v                               |
presentation cache --------------------+
```

DnSpyXDX's `SourceDocumentModel` indexes UTF-16 lines and positions, maintains tokenizer checkpoints, and produces cancellable immutable line batches. `SourceView` renders only the visible range through `Virtualize<TItem>`, `SourceLineView` renders classified token fragments, `SourcePresentationCache` bounds models and token batches by count and estimated bytes, and `SourceViewStateStore` separates per-tab scroll state from document identity. JavaScript is limited to focus, measurement, scroll, selection, and other browser operations Blazor cannot express directly.

NovaSharp must adapt this read-only design for editing. A mutable `EditorDocument` owns authoritative text, version, undo history, line index, and dirty state. Immutable versioned presentation snapshots feed a virtualized `EditorView`. A browser-native input surface owns caret, selection, clipboard, keyboard composition, and IME; C# applies the resulting edits and retokenizes only invalidated ranges. Highlighted visible rows and the input surface share exact font, tab, line-height, wrap, and scroll geometry.

One document model may have multiple views. Each view independently owns cursor, selection, scroll, completion UI, and view state, while edits and undo history remain document-wide.

## Language-service boundary

Roslyn's Workspace API is the solution-wide starting point for code analysis and refactoring. Its immutable solution snapshots, syntax/semantic models, and diagnostics fit a versioned document service ([Roslyn SDK model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model)).

Language features should use cancellable, versioned requests:

```text
editor request -> C# language service -> Roslyn snapshot
       ^                                      |
       +------ response if version matches ---+
```

Discard stale results after further edits. Completion and hover should not block typing; diagnostics should be debounced. Keep a provider-shaped internal API so Razor/HTML/CSS services or a future Language Server Protocol client can implement the same contracts. The common feature set includes diagnostics, completion, hover, signature help, definitions, references, symbols, code actions, formatting, and rename ([VS Code language features](https://code.visualstudio.com/api/language-extensions/programmatic-language-features)).

## Workbench rules

- Every command must be invocable independently of its button so menus, shortcuts, and command palette share behavior.
- Tree, tabs, splitters, dialogs, and completion lists must be keyboard operable with visible focus.
- Long-running I/O, restore, analysis, search, build, and debugger operations are cancellable and report status.
- Services return structured results; UI components do not parse console text when a structured API exists.
- Persist paths portably when they are inside the workspace and validate all restored state.
- Add telemetry only through an explicit opt-in design; never include source text, paths, or secrets by default.

## Presentation constraints inherited from DnSpyXDX

- All offsets are UTF-16 code-unit offsets so .NET strings, Roslyn spans, and browser selections agree.
- Tokenization is stateful across lines; checkpoints allow an invalidated or newly visible range to resume without rescanning from the start.
- Rendering remains proportional to viewport height plus overscan, not document length.
- Fixed-height, unwrapped lines are the first implementation. Wrapping requires a separately measured variable-height path.
- Background indexing, tokenization, search, and semantic classification are cancellable and return immutable results.
- Caches have explicit count and byte limits, protect active models, cancel evicted work, and expose debug counters.
- A tab ID is view identity, never document identity.

## Deferred features

Extensions, source control UI, remote workspaces, notebooks, collaboration, AI completion, visual designers, profiling, and multiple native windows remain outside phases 1–10. The internal command, language-provider, document, and workbench boundaries should allow them later without defining speculative public APIs now.
