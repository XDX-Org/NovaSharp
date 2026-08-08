# Phase 11 handoff

Branch: `phase-11` (from `phase-10`)

## Completed

- Cross-platform interactive sessions through PTY/ConPTY with explicit profiles and inherited workspace working directories.
- Multiple sessions with activation, rename, restart, close, exit state, resize, UTF-8 input/output, and owned-process cleanup.
- Dedicated hideable terminal panel using xterm.js 6.0.0 for editable input, selection/copy, guarded multiline paste, wrapping, ANSI/VT rendering, links, and screen-reader support.
- Complete emulator-managed terminal responses required by interactive shells such as fish.
- Explorer, search, problems, output, and terminal panel visibility restored with the workbench session; terminal processes remain intentionally non-persistent.
- Editor-style terminal tabs support per-tab close buttons and middle-click close; an open panel automatically replaces the final closed session.
- Bounded 5,000-line/4-MiB scrollback and isolated escape handling that cannot create workbench markup or invoke commands.
- Confirmation before terminating a live session or closing the workbench with active terminals.

## Verification

- Release build with warnings as errors passes locally.
- 89 unit, integration, and acceptance tests pass locally.
- A real Unix PTY fixture verifies resize, Unicode input/output, process exit, and exit-code propagation.
- Raw PTY replay tests cover byte preservation and memory bounds; xterm.js owns escape parsing and cursor redraw behavior.

Four-platform build/test/package and packaged Linux Phase 11 interaction evidence remain required before the delivery-plan status changes to complete.
