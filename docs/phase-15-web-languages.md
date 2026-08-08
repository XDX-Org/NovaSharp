# Phase 15: Razor, HTML, and CSS

## Goal

Make Razor/Blazor, HTML, and CSS projects first-class editing targets rather than merely loadable projects.

## Scope

- Syntax highlighting, completion, hover, diagnostics, formatting, symbols, navigation, and rename where the selected service supports them.
- Razor host/projected-document mapping for C#, HTML, and CSS regions with versioned range translation.
- Project-aware Razor/Blazor configuration, generated document visibility, and component/tag-helper completion.
- Standalone HTML and CSS documents through the same internal provider contracts.
- Clear degraded states when a language service is unavailable or a project is still loading.

JavaScript/TypeScript language services and browser debugging are not preview requirements.

## Implementation

- A language-provider registry selects C#, Razor, HTML, and CSS by extension and advertises each optional capability. Unregistered languages remain editable with a clear degraded state.
- Razor uses versioned HTML, CSS, and C# projection segments. Stale or cross-segment mappings are rejected before edits or ranges reach the host document.
- Razor component completion and definition navigation discover project `.razor` files. Project C# types are offered inside projected C# blocks with a 200-file scan bound.
- HTML/Razor provide tag and attribute completion, hover, structural diagnostics, formatting, symbols, component navigation, and tag rename. CSS provides properties, semantic classification, brace diagnostics, formatting, and embedded `<style>` support.
- Solution view exposes generated Razor `.g.cs` documents as non-editable generated files.
- The service is in-process and request-isolated. Restart clears bounded completion state and diagnostics while preserving editor buffers. See [ADR 0006](decisions/0006-phase-15-web-language-services.md).

## Budgets

On the 2,000-element Phase 15 fixture, projection updates must finish within 250 ms and first completion and semantic results within 1 second each. Providers retain zero projection snapshots; component/type discovery scans at most 200 C# files and retains at most 200 completion entries. A service restart must finish within 50 ms and preserve open document text.

## Design constraints

- Decide whether services are in-process or protocol-based and document redistribution, lifecycle, and version compatibility.
- Host-to-projection edits, diagnostics, selections, and navigation must round-trip through one tested mapping service.
- Never apply stale projected edits to a newer host document.
- Bound language-service processes, caches, requests, and restart loops.
- Keep feature availability capability-driven rather than claiming unsupported parity across all languages.

## Completion criteria

- Representative Razor Pages, Blazor Web App, component library, HTML, and CSS fixtures receive documented language features.
- Mixed-language edits, directives, embedded expressions, generated files, rename, formatting, and stale mapping cases are tested.
- Service crash/restart and project reload preserve editor text and recover without application restart.
- Diagnostic and navigation ranges map to the correct host text after rapid edits.
- Numeric first-result, projection-update, service-restart, and retained-memory budgets pass.

## Next phase

Establish the managed debugger protocol and lifecycle independently of its full UI.
