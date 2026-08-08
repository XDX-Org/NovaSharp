# ADR 0006: Phase 15 web-language services

## Status

Accepted for the preview workbench.

## Decision

Use bounded in-process providers behind the common `ILanguageProvider` contract. A registry maps file extensions to providers and exposes explicit capability metadata; the editor never depends on a concrete provider. Unknown languages remain editable and report an unavailable provider instead of falling back to C#.

Razor documents are split into versioned HTML, CSS, and C# projection segments. Every projected result must map wholly through one segment and match the current host version before it can affect the editor. Razor component completion and navigation use project files, while HTML and CSS features use the same request/response and stale-result rules.

The .NET Razor SDK remains the authority for project build and generated C# output. Generated Razor files under `obj` are visible but read-only in Solution view. NovaSharp does not redistribute or start the proprietary editor-side Razor server. The provider is stateless apart from bounded completion entries, so a failed request cannot poison later requests; restart clears those entries and diagnostics without touching editor buffers.

Additional languages register extensions, capabilities, and an `ILanguageProvider` implementation. Adding one must not require changes to `CodeEditor` or the workbench.

## Consequences

- Razor, HTML, and CSS share cancellation, version rejection, diagnostics, formatting, navigation, and UI behavior with C#.
- Preview support is capability-driven and intentionally does not claim full Visual Studio Razor parity.
- JavaScript/TypeScript, external language-server lifecycle, and a public extension API remain outside Phase 15.
- The parser scans at most 200 project C# files for component-context type completion and retains no document projections between requests.
