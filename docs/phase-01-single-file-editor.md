# Phase 1: single-file editor shell

## Goal

Prove the desktop host and the first complete interaction: launch NovaSharp, choose one C# file, and display its text in one editable panel.

## Scope

- .NET 10 desktop executable hosted by PhotinoXDX and Blazor.
- One window, toolbar, and editor panel.
- Native file dialog restricted to a single `.cs` selection.
- UTF-8-compatible text loading with a visible read/permission error.
- In-memory editing only.

Not included: saving, syntax highlighting, tabs, language services, project loading, build/run, debugging, or persisted settings.

## Repository layout

```text
NovaSharp/
├── docs/
├── NovaSharp.slnx
└── src/NovaSharp/
    ├── Components/EditorPanel.razor
    ├── Program.cs
    └── wwwroot/
```

NovaSharp consumes `PhotinoXDX.Blazor` version `0.1.0-preview.6` from NuGet, so a local PhotinoXDX checkout is not required.

## Run

Requirements: .NET 10 SDK plus PhotinoXDX's platform prerequisites.

```bash
cd NovaSharp
dotnet restore NovaSharp.slnx
dotnet run --project src/NovaSharp/NovaSharp.csproj
```

Select **Open C# file**, choose one `.cs` file, and confirm its name and contents appear. Edits are deliberately not written to disk in this phase.

## Completion criteria

- `dotnet build NovaSharp.slnx` succeeds.
- NovaSharp opens as a 1200×800 native window.
- Canceling the file dialog leaves the current file unchanged.
- Selecting one `.cs` file displays its contents.
- Read and permission failures appear in the editor panel without terminating the app.

## Next phase

Introduce a real code-editor surface and safe save/reload behavior before adding Roslyn. Keeping file lifecycle separate from language services makes both easier to test.
