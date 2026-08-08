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

## Verification pending

The local host does not include `xvfb-run`. The four-platform build/test/package matrix and packaged Linux Phase 15 interaction gate must pass after push before Phase 15 is marked complete.
