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
- All 95 tests pass locally.
- Windows x64, macOS Intel, and macOS Apple Silicon build/test/package jobs pass.
- Linux reaches the older Phase 9 packaged smoke but has not yet reached the Phase 11 or Phase 15 gates.
- Latest run: https://github.com/XDX-Org/NovaSharp/actions/runs/31278775518

Do not mark Phase 15 complete until the Linux Phase 15 packaged interaction smoke and the full matrix pass.

## Current blocker

The Linux Phase 9 smoke reports `ResultsStreamed = false`. Its Quick Open assertions now pass. The remaining failure is specific to synthetic search-input events under hosted WebKit; `SearchPanel` itself and `WorkspaceSearchService` are covered by passing tests.

Findings and fixes already pushed:

- `fd00606` rerenders Quick Open after its asynchronous file scan.
- `1976ba4` makes workspace features prefer the active solution directory over stale Explorer session state.
- `63a189b` verifies the two rendered `Shared.cs` entries without relying on synthetic Quick Open typing.
- `7f50b44` changes the search query to `oninput`, but hosted WebKit still drops the programmatic event.

## Next action

Remove synthetic typing from the Phase 9 smoke while retaining UI coverage:

1. Add `@ref` for `SearchPanel` in `WorkbenchPanel`.
2. Add an internal `SearchPanel.RunSmokeAsync(string query)` that assigns `_query`, calls the existing `StartAsync`, and rerenders.
3. Add a `[JSInvokable]` bridge method on `WorkbenchPanel` that calls it.
4. In `runPhase9Smoke`, invoke that bridge after confirming the Search panel is visible, then keep the existing rendered-results and replace-preview assertions.
5. Run all 95 tests, push, and monitor the complete matrix through the Phase 15 Linux gate.
6. Once green, update `docs/delivery-plan.md` to `Complete` and replace this blocker section with the successful run URL.

## Commit range

Phase 15 starts at `26b0292` on top of Phase 11 commit `a24cf72`. Current branch head is `7f50b44`; the branch is pushed and the worktree was clean at handoff.
