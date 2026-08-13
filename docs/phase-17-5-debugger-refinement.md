# Phase 17.5: debugger refinement

## Goal

Make managed debugging usable from the editor without requiring protocol-level or test-only setup.

## Scope

- Set, remove, enable, and disable source breakpoints from the editor gutter and keyboard commands.
- Show pending, verified, moved, disabled, and rejected breakpoint states in the editor, with rejection details.
- Send existing breakpoints when a debug session starts and synchronize changes made while it is running.
- Persist user-authored breakpoints across edits, rebuilds, restarts, and workspace restoration.
- Refine the resizable Debugger panel and expose threads, call stack, scopes, variables, watches, exceptions, and console output coherently.
- Make launch failures and adapter diagnostics visible in Output and the Debugger panel.
- Verify build, launch, break, inspect, step, continue, restart, and stop as one end-to-end workflow.

## Design constraints

- Keep user-authored breakpoint identity separate from adapter binding state.
- Map breakpoints by canonical source identity and update their lines through document edits.
- Never report a breakpoint as active until the adapter verifies it; explain pending and rejected states.
- Debugger controls and views remain keyboard-operable and screen-reader labelled.
- Adapter requests, inspection data, output, and diagnostics remain bounded and cancellable.

## Completion criteria

- Clicking the editor gutter toggles a breakpoint and immediately shows its state.
- A keyboard-only user can add or remove a breakpoint on the current line.
- Starting Debug builds and launches the selected executable, binds existing breakpoints, stops on the selected source line, and opens the Debugger panel.
- Breakpoints added, removed, enabled, or disabled during a session synchronize without restarting it.
- Breakpoints track line edits and survive workspace restoration and debugger restart.
- Invalid targets, missing adapters, adapter stderr, binding failures, and target exits are visible and actionable.
- Windows x64, Linux x64, macOS x64, and macOS arm64 packaged interaction tests cover launch, breakpoint hit, inspection, stepping, restart, and cleanup.

## Completion audit

- [ ] Add editor-gutter breakpoint interaction and visual states.
- [ ] Add keyboard commands and command-palette entries for breakpoint control.
- [ ] Connect the persisted breakpoint store to open documents and workspace restoration.
- [ ] Send stored breakpoints during launch and synchronize live changes.
- [ ] Surface adapter stderr, launch failures, binding messages, and target exit state.
- [ ] Complete Debugger panel layout, focus, empty states, accessibility, and resize verification.
- [ ] Add end-to-end managed-debugging fixtures and packaged cross-platform interaction gates.

## Status

Planned. The DAP/session foundation and inspection controls exist, but editor breakpoint creation and the complete
user workflow are not connected yet.

## Next phase

Package and qualify the complete preview product.
