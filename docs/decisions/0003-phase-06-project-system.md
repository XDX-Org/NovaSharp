# ADR 0003: Phase 6 project system

## Decision

Use `Microsoft.Build.Locator` to select an installed .NET SDK and `MSBuildWorkspace` to evaluate SDK-style C# projects. NovaSharp supports `.sln`, `.slnx`, and `.csproj` inputs targeting the installed SDK's supported frameworks.

Roslyn projects are the evaluated contexts. Multi-target projects therefore expose one context per Roslyn project produced by MSBuild. A physical file may map to several Roslyn document IDs; the active project context selects one, with a deterministic first-project fallback until the user selects another context.

One coordinator owns workspace mutations. Editor snapshots carry monotonically increasing versions; superseded updates are cancelled and stale versions are rejected. Reload replaces the evaluated workspace, rebuilds mappings, and reapplies dirty editor snapshots.

Project-load diagnostics use a versioned diagnostic store. Concise messages are shown in the workbench; raw MSBuild workspace failures remain separately available for troubleshooting.

## Consequences

- References, compiler options, analyzers, generated files, and project references come from evaluated MSBuild state rather than output-directory scanning.
- Machine-global Visual Studio discovery is not required. Tests select the current `dotnet` SDK and use isolated fixture projects without restore-time package dependencies.
- Legacy/non-SDK projects, Visual Basic editing, solution folders as build units, and custom project systems are outside the preview scope.
