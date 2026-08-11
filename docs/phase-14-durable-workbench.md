# Phase 14: durable workbench

## Goal

Make the C# workbench resilient and configurable enough for sustained daily use.

## Scope

- Settings UI over the configuration service introduced in phase 2, including user/workspace scope and validation.
- Command/keybinding editor, themes, zoom, reduced motion, high contrast, and screen-reader validation.
- Persist workspace, editor groups, panels/sidebar, tabs, breakpoints, run configurations, and relevant view state.
- Crash recovery for dirty buffers and startup safe mode after repeated restoration failure.
- Storage/schema migration, reset/export diagnostics, and documented data locations.
- Full-workbench profiling and correction of lifecycle, startup, typing, and memory regressions.

## Design constraints

- Persist versioned, atomic JSON outside the workspace unless data is intentionally shareable.
- Validate schema versions, ratios, node counts, paths, commands, and settings before applying restored state.
- Dirty-buffer recovery is isolated from optional layout restoration and never overwrites the original file automatically.
- Secret values use platform credential storage or remain outside ordinary settings and diagnostics.
- All major flows must work without a pointer and with visible focus.

## Completion criteria

- Corrupt or incompatible state cannot prevent startup; migrations, rollback, safe mode, and reset are independently tested.
- An induced crash recovers exact dirty content without silently changing disk files.
- Keyboard-only and screen-reader passes cover shell, Explorer, tabs, splits, editor, Problems, output, terminal, and debugger.
- Repeated open/close, workspace switching, reload, terminal, and debug cycles show no unbounded resource growth.
- Startup, idle memory, typing, restoration, recovery, and sustained-session budgets pass on named fixtures.

## Next phase

Expose a deliberately small, stable subset of proven workbench capabilities to extensions.

## Implementation status

Implemented: atomic validated settings and layout storage; exact dirty-buffer recovery; automatic restoration safe
mode; theme, zoom, reduced-motion, high-contrast, and ligature controls; bounded persisted breakpoint/run/panel state;
redacted persistence diagnostics and independently resettable state. Full-workbench accessibility, lifecycle-growth,
and named performance-budget evidence remain required before marking the phase complete.

## Verification budgets

The named recovery fixture repeatedly atomically captures a 1 MiB dirty buffer 20 times and restores the newest
exact content within ten seconds on the CI runner, while retaining one recovery file per canonical source path.
Restoration accepts at most 256 documents and recovery buffers, 4,096 breakpoints, and 64 run configurations.
