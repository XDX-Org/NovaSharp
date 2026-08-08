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
