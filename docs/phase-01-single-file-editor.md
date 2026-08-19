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
└── src/NovaSharp/
    ├── Components/EditorPanel.razor
    ├── Program.cs
    └── wwwroot/
        ├── monaco-editor-host.js
        └── generated/monaco/
```

Generated assets may be build output rather than committed files, but restore/build/publish must fail clearly when the pinned assets cannot be produced. Runtime CDN access and Monaco's deprecated AMD build are not permitted.

## Design constraints

- Follow [ADR 0001](decisions/0001-monaco-editor.md). There is no textarea or custom source-row fallback.
- Create Monaco only after its empty container is mounted; use a `ResizeObserver` for layout and dispose the editor, observer, callbacks, and model deterministically.
- Use one `ITextModel` with a canonical file URI and the `csharp` language ID. Do not copy the file into Blazor component state or rerender Blazor on each edit.
- File dialog and file reads are awaited. Loading work may not block Monaco's browser thread or the Blazor renderer.
- Bundle and start the editor worker under the packaged application's actual origin. A silent fallback to main-thread worker code fails the phase.
- Keep JavaScript interop narrow and cancellation/lifetime-aware. Phase 1 may request the initial text once; it must not exchange full content on every change.

## Run

Requirements and platform packages are listed in the root [README](../README.md). Node/npm is acquired automatically and does not need
to be installed separately.

```bash
bash tools/setup.sh
dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build
```

On Windows use `powershell -ExecutionPolicy Bypass -File tools/setup.ps1` instead. The bootstrap builds and verifies Monaco before the
.NET build begins.

Select **Open C# file**, choose one `.cs` file, and confirm its name and Monaco-rendered contents appear. Edits are deliberately not written to disk in this phase.

## Completion criteria

- Clean checkout restore, frontend asset build, `dotnet build`, and `dotnet publish` succeed reproducibly.
- NovaSharp opens as a 1200×800 native window and Monaco is the only editor.
- The packaged editor and editor worker start locally on every supported OS; a test detects main-thread fallback and runtime network requests.
- Canceling the dialog leaves the current model unchanged; selecting one `.cs` file displays it with Monaco C# lexical colors.
- Read and permission failures appear without terminating the app or replacing the current model.
- Typing, selection, undo/redo, find, IME, surrogate pairs, long lines, and scrolling work without synchronous .NET calls or Blazor rerenders per keystroke.
- First-editor latency, typing latency, UI-thread long tasks, worker startup, and idle memory have recorded budgets and pass on named hardware.
- Closing/reopening repeatedly releases editors, models, observers, callbacks, and worker resources within the defined bounds.

## Next phase

Add the asynchronous document replica and safe save/reload lifecycle around the proven Monaco model.
