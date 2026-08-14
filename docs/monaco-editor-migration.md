# Monaco editor migration

## Implementation status

Monaco is the default editor on the `monaco` branch. Local asset packaging, shared bounded models, versioned edit
batches, view-state restoration, language-feature routing, semantic presentation, diagnostics, debugger decorations,
settings, commands, packaged native smoke coverage, licenses, and release validation are implemented.

The old textarea/presentation renderer and its runtime switch have been removed. Monaco is the sole editor path.

## Goal

Replace NovaSharp's transparent full-document `textarea` and Blazor-rendered source rows with Monaco Editor while preserving NovaSharp's document lifecycle, workspace, language-server, debugging, settings, and persistence behavior.

The migration is complete when Monaco owns text editing and visual presentation, while `EditorDocumentState` remains the application-level authority for open documents, dirty state, disk conflict handling, save encoding, and line endings.

## Current boundary

`CodeEditor.razor` currently owns both editor presentation and language-feature UI:

- Blazor virtualizes `SourceLineView` rows and renders syntax, semantic spans, diagnostics, brace guides, breakpoints, and the execution line.
- A transparent full-size `textarea` owns caret, selection, clipboard, composition, and scrolling.
- `editor.js` keeps the textarea and presentation scroll positions aligned and forwards complete text values and commands to .NET.
- `EditorDocumentState` owns content, versions, dirty state, undo/redo snapshots, encoding, line endings, saves, and external-change conflicts.
- `LspLanguageProvider` and `LanguageDocumentCoordinator` own the packaged language-server connection and document synchronization.
- Each tab view has an `EditorViewState`; duplicate views can share one `EditorDocumentState`.

Monaco replaces the first three items. It must not replace the remaining application and language-server boundaries accidentally.

## Target boundary

```text
EditorDocumentState (.NET, one per open document)
  | edit batches / external replacements
  v
Monaco model (JavaScript, one per document URI)
  | shared by
  +-- Monaco editor instance (one per visible tab view)
  +-- Monaco editor instance (split/duplicate view)

NovaSharp LSP providers (.NET) <-> Monaco feature adapters (JS/.NET interop)
NovaSharp diagnostics/debug state -> Monaco markers and decorations
Monaco selection/scroll state -> EditorViewState
```

Use one Monaco model per `EditorDocumentState`, not one model per editor instance. A split view gets another Monaco editor attached to the same model and retains independent selection and scroll state.

## Decisions to lock before implementation

1. **Package and version:** pin `monaco-editor` to an exact version and commit its lockfile. Do not load scripts, styles, or workers from a CDN.
2. **Bundling:** add a small frontend build that emits static, hashed Monaco assets into `wwwroot`. The .NET build and release qualification must fail when required generated assets are absent or stale.
3. **Workers:** prove local worker loading in Photino/WebView2 on Linux, Windows, and macOS before replacing the current editor. Record whether workers can use emitted URLs directly or require a `MonacoEnvironment.getWorkerUrl/getWorker` hook.
4. **Language ownership:** retain NovaSharp's packaged Roslyn/Razor/HTML/CSS/TypeScript LSP processes. Disable or omit overlapping Monaco language services so two providers do not produce competing completion, hover, formatting, or diagnostics.
5. **Document authority:** keep `EditorDocumentState` authoritative for persistence and dirty state. Monaco owns interactive undo/redo and edit geometry. Synchronization must use edit batches and origin/revision guards, not repeated full-model `setValue` calls.
6. **Fallback:** keep the existing editor available behind a temporary development setting until the Monaco path passes native interaction and release gates. Remove it and the setting in the final cleanup, rather than maintaining two production editors.

## Work required

### 1. Asset acquisition and release compliance

- Add `package.json` and a lockfile containing only the frontend build dependencies required for Monaco.
- Bundle Monaco's ESM entry points, CSS, fonts, and workers locally. Use tree-shaken language contributions where possible.
- Add deterministic install/build commands suitable for local development and CI; no runtime network access.
- Add Monaco's MIT license and required third-party notices to the packaged notices and SPDX inventory.
- Include generated assets in `dotnet publish` for every supported RID.
- Add dependency vulnerability and license checks to `tools/qualify-release.sh` and CI.
- Document the Node/npm version used to reproduce the bundle. Reuse the repository's pinned asset-acquisition conventions where practical.

### 2. Monaco JavaScript host

Create a focused module, for example `wwwroot/monaco-editor-host.js`, with a narrow interop API:

- `createEditor(element, documentId, uri, languageId, value, options, viewState, dotNet)`
- `attachModel(editorId, documentId)` and `releaseEditor(editorId)`
- `releaseModel(documentId)` only after the last view and document lease close
- `applyExternalEdits(documentId, edits, expectedRevision)`
- `setDiagnostics`, `setSemanticTokens`, `setBreakpoints`, and `setExecutionLine`
- `updateOptions` and `layout`
- `saveViewState`, `restoreViewState`, `focus`, and reveal-position/range operations

Keep registries for editor instances, models, disposables, decorations, and callback handles. Every creation path needs an idempotent disposal path.

Use a `ResizeObserver` to call `editor.layout()`. Dispose it with the editor. A remounted Blazor component must reconnect to an existing shared model without recreating its undo stack.

### 3. Document and edit synchronization

Define an interop edit contract using UTF-16 offsets because Monaco, LSP, and .NET strings all expose UTF-16 positions:

```text
EditorEditBatch(documentId, baseVersion, edits[], selections[], source)
EditorEdit(start, length, text)
```

- Translate Monaco `onDidChangeModelContent` changes into one ordered .NET transaction.
- Apply the batch to `EditorDocumentState`, increment its version once, update dirty state and publish one content-change notification.
- Return/acknowledge the resulting document version so stale callbacks can be rejected.
- Tag .NET-originated changes and suppress their echo when applying them to Monaco.
- Apply reloads, workspace edits, formatting, completion additional edits, rename, and code actions with `executeEdits` or model edit operations so undo stops are intentional.
- Use `setValue` only for initial model creation or an explicit history-reset operation.
- Specify ordering for multi-edit batches; validate bounds, overlap, document identity, and base version before mutation.
- Preserve CRLF and surrogate-pair correctness already covered by the language coordinator.

`EditorDocumentState` currently stores whole-text undo/redo snapshots. Replace or adapt that behavior so application commands delegate to the active Monaco model while it is attached. Define the fallback for a document with no active view, and verify that duplicate views share the same undo stack.

### 4. Blazor component replacement

Refactor `CodeEditor.razor` into a lifecycle wrapper around one Monaco container:

- Preserve its document, settings, diagnostics, breakpoint, execution-line, navigation, and command inputs initially to limit workbench churn.
- Move editor creation, option updates, model attachment, and disposal behind the host module.
- Stop rendering `SourceLineView`, the transparent textarea, diagnostic overview, and editor-owned Blazor popups once equivalent Monaco features are active.
- Do not key/remount the editor for ordinary option changes such as word wrap or brace guides; call `updateOptions` instead.
- Keep `EditorGroupLayoutView` responsible for selecting the document/view pairing and `WorkbenchPanel` responsible for workspace-level commands.

Retain Blazor overlays only where they are intentionally workbench UI rather than editor UI. Prefer Monaco contributions for completion, hover, signature help, code actions, glyph margin, peek, and editor-local accessibility.

### 5. Language-feature adapters

Register Monaco providers that call the existing `ILanguageProvider` / `IExtendedLanguageProvider` implementations:

| NovaSharp capability | Monaco integration |
|---|---|
| Completion and resolve | completion item provider; preserve snippets, commit characters, ranges, additional edits, resolve data, and commands |
| Signature help | signature help provider with trigger/retrigger characters |
| Hover | hover provider; combine language and diagnostic sections safely as Markdown |
| Formatting | document/range formatting edit providers |
| Definition/type/implementation/references | definition and reference providers; keep cross-file navigation in the workbench |
| Rename | rename provider returning the existing previewed workspace edit flow |
| Code actions | code action provider; retain transactional preview for resource operations |
| Document symbols | document symbol provider and existing workbench outline command |
| Semantic tokens | semantic tokens provider or Monaco decorations using the negotiated LSP legend |
| Diagnostics | `monaco.editor.setModelMarkers` from NovaSharp's retained pull/push diagnostic store |

Provider callbacks must carry the `EditorDocumentState.Version`, project context, cancellation token, and model URI. Reject results for a changed version exactly as the current editor does. Dispose and reregister providers when capabilities or language registrations change.

Keep `LanguageDocumentCoordinator` as the sole LSP document synchronization path. Monaco providers request features through NovaSharp; they must not open a second direct LSP connection.

### 6. Language IDs, URIs, and projects

- Define a stable mapping from file extension/provider information to Monaco language IDs.
- Create canonical `file:` model URIs from normalized paths; use a collision-free NovaSharp URI scheme for untitled documents.
- Keep URI-to-document lookup scoped and bounded. Paths must not leak through logs or exception text beyond current redaction policy.
- Pass the active Roslyn project context for linked files and multi-project documents.
- Update model language when a file is saved with a new extension without losing content or view state.

### 7. Themes and settings

Map existing NovaSharp settings to Monaco options:

- theme and high contrast
- font family, size, ligatures, line height, and tab size
- word wrap/hard-wrap-related behavior
- suggestions and semantic highlighting
- brace guides, whitespace, minimap, line numbers, and accessibility options
- reduced motion and popup placement where Monaco exposes an equivalent

Define NovaSharp dark, light, and high-contrast Monaco themes using the existing CSS palette. Monaco owns token colours after migration; remove duplicate `.token-*` CSS only after visual comparison passes.

### 8. Diagnostics and debugging presentation

- Publish Problems data as Monaco markers without changing the shared Problems store.
- Render pending/verified/rejected breakpoints in the glyph margin and forward glyph-margin clicks through `BreakpointToggled`.
- Render the paused execution line and instruction pointer as decorations.
- Update decorations by stable collection/delta APIs rather than recreating the editor.
- Preserve diagnostic navigation, hover details, overview ruler marks, breakpoint conditions, and source mapping.

### 9. Commands, focus, and workbench integration

- Route Save, Open, Find, Undo, Redo, formatting, navigation, rename, code actions, comment, and completion through the existing command system.
- Resolve conflicts between Monaco default keybindings and NovaSharp global shortcuts explicitly.
- Update layout and smoke-test selectors that currently query `.editor-input`.
- Preserve focus tracking across groups, tab moves, split creation, drag/drop, quick access, terminal focus, and restored sessions.
- Expose only the minimum command surface from JavaScript; do not let Monaco bypass workspace edit validation or file conflict policy.

### 10. View state and persistence

Expand `EditorViewState` from raw textarea offsets and pixels to a versioned Monaco-compatible representation:

- cursor selections, including direction and future multiple selections
- scroll top/left and visible position
- view zones/contribution state only when safe and bounded

Persist a validated subset, not Monaco's opaque state wholesale. Add schema migration from the current single selection offsets and scroll coordinates. Clamp all restored positions to the current model.

### 11. Accessibility, security, and performance

- Test screen-reader mode, keyboard-only operation, IME/composition, clipboard, high contrast, zoom, reduced motion, and large text scaling.
- Test Unicode surrogate pairs, combining characters, bidi text, tabs, ligatures, long lines, mixed line endings, and wrapped lines.
- Keep all assets local and compatible with the application's content-security policy. Do not enable arbitrary HTML in hover or completion documentation.
- Bound retained models, decorations, provider results, and view state. Closing the last document lease must release the Monaco model and its undo history.
- Measure startup bundle cost, first-editor latency, typing latency, memory per shared model/view, large-file scrolling, and time to close/reopen workspaces.

## Migration sequence

1. **Acquisition spike:** pin and bundle Monaco; prove editor and worker startup in packaged Debug and Release builds on all supported platforms.
2. **Host shell:** mount Monaco behind a development setting with plain text, shared models, view state, resizing, and deterministic disposal.
3. **Edit bridge:** implement versioned incremental batches, external edits, undo/redo, dirty state, save, reload, and duplicate views.
4. **Core language loop:** wire language IDs, coordinator synchronization, diagnostics, semantic tokens, completion, hover, signature help, and formatting.
5. **Advanced features:** navigation, rename, code actions, symbols, project context, snippets, and workspace edits.
6. **Workbench integration:** commands, settings, themes, breakpoints, execution line, focus, splits, drag/drop, and persistence migration.
7. **Qualification:** replace DOM-specific smoke tests, add native interaction coverage, run packaging/license/accessibility/performance gates on all RIDs.
8. **Cleanup:** make Monaco the only editor, delete the temporary switch and obsolete presentation/input code, then update architecture decisions and release inventories.

## Expected removals after cutover

- `Components/SourceLineView.razor`
- Most presentation and popup rendering in `Components/CodeEditor.razor`
- The textarea implementation and smoke helpers in `wwwroot/editor.js`
- `.presentation`, `.editor-input`, `.source-line`, `.token-*`, and related editor CSS
- `EditorLine`, `ClassifiedSpan`, `BraceGuide`, `TokenKind`, and `CSharpTokenizer` when no non-editor consumer remains
- Tests that validate the old tokenizer/DOM implementation rather than user-visible behavior

Do not remove the old path until Monaco covers its behavior. Some tokenizer code may remain temporarily as a fallback semantic presentation source, but it should not survive merely because tests reference it.

## Acceptance gates

- Opening, editing, saving, Save As, reload, external conflicts, encoding, and line endings retain their current behavior.
- Undo/redo remains correct across typing, completion, formatting, code actions, workspace edits, duplicate views, and external replacements.
- Split views share content and undo history while retaining independent cursor and scroll state.
- Existing packaged language servers remain the sole semantic authority; stale responses never update a newer document version.
- Completion, signature help, hover, formatting, navigation, rename, code actions, symbols, semantic highlighting, and Problems pass parity tests.
- Breakpoints and execution state render and navigate correctly.
- No runtime network request is required; licenses, notices, hashes, and SPDX data ship with every package.
- Native interaction gates pass on Linux, Windows, macOS arm64, and macOS x64.
- Large files remain bounded and responsive; closing documents releases models, workers, callbacks, and decorations.
- Accessibility qualification covers screen readers, IME, keyboard-only navigation, high contrast, and zoom.

## Main risks

| Risk | Required control |
|---|---|
| Full-value .NET/JS echo loops destroy undo and cause latency | Incremental batches, origin tags, revision acknowledgements |
| One Monaco model per view causes divergent duplicate tabs | Model registry keyed by document identity with explicit leases |
| Monaco and NovaSharp both supply language features | Disable overlapping built-ins and keep one provider route |
| Web workers fail in packaged Photino origins | Cross-platform packaged spike before editor replacement |
| Workspace edits bypass NovaSharp validation | Adapt providers through existing preview/transaction boundary |
| Models and providers leak after tab/workspace changes | Idempotent disposal plus bounded lifecycle tests |
| Frontend assets drift or disappear from packages | Exact lockfile, deterministic build, publish validation, offline smoke test |
| DOM-based tests give false confidence | Native Monaco interaction and behavior-level tests |
