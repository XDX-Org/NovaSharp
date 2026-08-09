# Phase 15 handoff

Branch: `phase-15` (from `phase-11` at `a24cf72`)

## Implemented

- Extensible capability-driven provider registry; editor/workbench no longer cast to the C# provider.
- Razor/CSHTML, HTML, and CSS completion, hover, semantic classification, diagnostics, formatting, symbols, navigation, and rename where advertised.
- Versioned Razor HTML/CSS/C# projection mapping with stale and cross-segment rejection.
- Project component and C# type discovery with bounded results and scans.
- Generated Razor document visibility in Solution view.
- Open/save filters and workspace support for `.razor`, `.cshtml`, `.html`, `.htm`, and `.css`.
- Unit, integration, performance-budget, recovery, project-tree, and packaged native interaction coverage.

## Architecture

ADR 0006 records the in-process service, projection ownership, bounds, degraded behavior, and provider-registration boundary for future languages.

## Verification

- Local Release builds pass with warnings as errors.
- All 99 tests pass locally.
- Windows x64, macOS Intel, and macOS Apple Silicon build/test/package jobs pass.
- The full Windows x64, Linux x64, macOS Intel, and macOS Apple Silicon matrix passes.
- Linux packaged interaction smokes pass through the Phase 15 gate.
- Successful run: https://github.com/XDX-Org/NovaSharp/actions/runs/31310379518

Phase 15 is complete.

## Resolved blocker

The Linux Phase 9 smoke no longer relies on synthetic search-input events under hosted WebKit. It invokes an internal `SearchPanel` bridge while retaining visible-panel, rendered-results, and replace-preview assertions. The previously unreached Phase 11 smoke similarly uses the terminal service bridge for PTY input/output while retaining visible-host, resize, and process-exit assertions.

Final fixes:

- `a347216` replaces synthetic Phase 9 search typing with the `SearchPanel` bridge.
- `e2d683f` refreshes the bridge's workspace parameters before running the search.
- `80fd600` stabilizes the previously unreached Phase 11 packaged interaction smoke.
- `9d32b0e` adds the final Razor Pages, component-library, standalone CSS, rapid-edit, reload, discovery-bound, and retained-state acceptance coverage.
- Run 31310379518 passes the complete supported-platform matrix and every Linux packaged smoke through Phase 15.

## Commit range

Phase 15 starts at `26b0292` on top of Phase 11 commit `a24cf72`. Completion evidence is recorded by run 31310379518.
