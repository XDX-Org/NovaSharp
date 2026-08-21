# Phase 1: Monaco single-file editor shell

## Goal

Prove the desktop host, locally packaged Monaco Editor, and the first complete interaction: launch NovaSharp, choose one C# file, and edit it without a network dependency or UI-thread I/O.

## Scope

- .NET 10 desktop executable hosted by PhotinoXDX and Blazor.
- An exact lockfile-pinned Monaco ESM build, C# language definition, CSS/fonts, editor worker, license, and deterministic asset build.
- One window, toolbar, and Monaco editor instance.
- Native file dialog restricted to one `.cs` selection.
- Asynchronous UTF-8-compatible text loading with a visible read/permission error.
- In-memory editing only; Monaco owns live text, undo/redo, selection, IME, lexical token colors, and viewport rendering.
- The lifetime/task coordinator and bounded async work scheduler required by the delivery plan.

Not included: saving, .NET edit replication, tabs, Roslyn/project-aware language services, project loading, build/run, debugging, or persisted settings.

## Target repository layout

```text
NovaSharp/
├── docs/
├── package.json
├── package-lock.json
├── NovaSharp.slnx
├── tests/
│   ├── NovaSharp.Tests/          # unit and contract tests, run by dotnet test
│   └── editor-host/              # browser gates for the packaged editor
└── src/NovaSharp/
    ├── Async/                    # bounded work queue, superseding operations
    ├── Components/EditorPanel.razor
    ├── Editing/                  # document loading and the editor host abstraction
    ├── Platform/                 # path and document-identity seam
    ├── Program.cs
    ├── Workbench.cs              # composition root
    └── wwwroot/
        ├── monaco-editor-host.js
        └── monaco/               # generated; never committed
```

`wwwroot/monaco/` holds the generated ESM bundle, editor worker, CSS, fonts, licenses, and `asset-manifest.json`. It is build output,
ignored by Git, and produced only by the pinned bundler. `monaco-editor-host.js` is the hand-written host module that loads that bundle,
owns the editor lifetime, and is the single interop surface between Monaco and .NET.

Generated assets may be build output rather than committed files, but restore, build, and publish must fail clearly when the pinned assets
cannot be produced. Runtime CDN access and Monaco's deprecated AMD build are not permitted.

## Design constraints

- Follow [ADR 0001](decisions/0001-monaco-editor.md). There is no textarea or custom source-row fallback.
- Create Monaco only after its empty container is mounted; use a `ResizeObserver` for layout and dispose the editor, observer, callbacks, and model deterministically.
- Use one `ITextModel` with a canonical file URI and the `csharp` language ID. Do not copy the file into Blazor component state or rerender Blazor on each edit.
- File dialog and file reads are awaited. Loading work may not block Monaco's browser thread or the Blazor renderer.
- Bundle and start the editor worker under the packaged application's actual origin. A silent fallback to main-thread worker code fails the phase.
- Keep JavaScript interop narrow and cancellation/lifetime-aware. Phase 1 may request the initial text once; it must not exchange full content on every change.
- Load the bundle as an ES module. The worker URL is resolved from `import.meta.url`, so a classic `<script>` tag silently breaks worker
  creation; the host page must use `<script type="module">`.
- Nothing in this phase may branch on the host operating system. Paths, URIs, line endings, and dialog behavior go through one
  abstraction that behaves identically on every runtime identifier in the supported platform table.

## Run

Prerequisites and per-platform packages are listed in the root [README](../README.md). Node and npm are acquired automatically and do not
need to be installed separately.

Run the bootstrap entry point for your shell — the two are equivalent, and neither platform is the reference:

```bash
# POSIX shell
./tools/setup.sh
```

```powershell
# PowerShell
pwsh -File tools/setup.ps1
```

The bootstrap builds and verifies Monaco before the .NET build begins. Then, on every platform:

```bash
dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build
```

Select **Open C# file**, choose one `.cs` file, and confirm its name and Monaco-rendered contents appear. Edits are deliberately not
written to disk in this phase.

## Completion criteria

Criteria already met are marked. The rest are what still stands between this phase and `complete`.

- **Met.** `tests/NovaSharp.Tests` exists, is referenced by `NovaSharp.slnx`, and `dotnet test NovaSharp.slnx` runs in the bootstrap.
  Both it and the `tests/editor-host` browser suite are wired into [CI](../.github/workflows/ci.yml) for every supported
  runtime identifier; a green run on every row is the evidence that is still outstanding.
- Clean checkout restore, frontend asset build, `dotnet build`, and a runtime-identifier-specific `dotnet publish` succeed reproducibly.
- **Met in code, unverified on a device.** NovaSharp opens as a 1200×800 native window and Monaco is the only editor. The placeholder
  text area is deleted, not disabled, and a contract test fails the build if one reappears.
- The packaged editor and editor worker start locally on every runtime identifier in the supported platform table; a test detects
  main-thread worker fallback and any runtime network request. `tests/editor-host` makes both assertions; running it per platform in CI
  is the remaining work.
- **Met in code, unverified on a device.** Canceling the dialog leaves the current model unchanged; selecting one `.cs` file displays it
  with Monaco C# lexical colors.
- **Met in code, unverified on a device.** Read and permission failures appear over the editor without terminating the app or replacing
  the current model.
- Typing, selection, undo/redo, find, IME, surrogate pairs, long lines, and scrolling work without synchronous .NET calls or Blazor
  rerenders per keystroke. The browser suite covers typing, surrogate pairs, and undo; IME, find, and long-line behavior are not yet
  asserted.
- First-editor latency, typing latency, UI-thread long tasks, worker startup, and idle memory have recorded budgets and pass on named
  hardware for each supported platform, not one representative platform.
- Closing and reopening repeatedly releases editors, models, observers, callbacks, and worker resources within the defined bounds. The
  browser suite asserts single-cycle disposal; the repeated-cycle bound is not yet measured.
- No completion criterion above is satisfied by evidence from a single operating system. Nothing here has run on more than one yet.

## Next phase

Add the asynchronous document replica and safe save/reload lifecycle around the proven Monaco model.
