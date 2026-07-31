# Phase 10: debugging and durable workbench

## Goal

Debug .NET applications and make the complete workbench resilient, restorable, and accessible.

## Scope

- Launch/attach configurations and a debugger-engine boundary suitable for a .NET debug adapter.
- Source, function, conditional, hit-count, and log-point breakpoints with verified/unverified states.
- Continue, pause, stop, restart, step over/into/out, and run to cursor.
- Threads, call stack, scopes, variables, watches, evaluate, and debug console.
- Source mapping and a clear read-only experience for source not present in the workspace.
- Context-sensitive debug layout while retaining the user's normal layout.
- Persist workspace, editor-group tree, panel/sidebar visibility and size, tabs, breakpoints, run configurations, and user settings.
- Command/keybinding editor, themes, zoom, reduced motion, high contrast, and screen-reader validation.
- Crash recovery for dirty buffers plus startup safe mode after repeated restoration failure.

Extensions and non-.NET debugger support remain future work.

## Design constraints

- Debug state is a state machine; commands declare the states in which they are valid.
- Identify source by normalized path plus checksums when available. Never guess a match solely from file name.
- Variable expansion is lazy, cancellable, paged where supported, and bounded for hostile object graphs.
- Persist versioned, atomic JSON state outside the workspace unless data is intentionally shareable.
- Layout restoration validates ratios, node counts, paths, commands, and schema version before applying.
- All major flows must be operable without a pointer. Visual Studio's durable docking/layout behavior is an established workbench expectation ([Visual Studio layouts](https://learn.microsoft.com/en-us/visualstudio/ide/customizing-window-layouts-in-visual-studio?view=visualstudio)).

## Completion criteria

- A user can build, launch, break, inspect, evaluate, step, stop, edit, rebuild, and relaunch a fixture app.
- Breakpoints survive edits/restarts and visibly report binding failures.
- Exceptions, process exit, adapter loss, and stale source produce recoverable states.
- Corrupt persisted state cannot prevent startup; dirty-buffer recovery is independently testable.
- Keyboard-only and screen-reader passes cover shell, Explorer, tabs, splits, editor, Problems, terminal, and debugger.
- Cross-platform smoke tests and documented performance/reliability budgets gate a preview release.
