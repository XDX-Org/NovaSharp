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
- Scheduler diagnostics for queue depth, active workers, cancellation/coalescing, replica lag, UI long tasks, and saturation.

## Design constraints

- Persist versioned, atomic JSON outside the workspace unless data is intentionally shareable.
- Validate schema versions, ratios, node counts, paths, commands, and settings before applying restored state.
- Dirty-buffer recovery is isolated from optional layout restoration and never overwrites the original file automatically.
- Secret values use platform credential storage or remain outside ordinary settings and diagnostics.
- All major flows must work without a pointer and with visible focus.
- Persist asynchronously from immutable snapshots. Recovery checkpoints coalesce changes and run at low priority; they must not serialize Monaco content on every keystroke.
- Profile contention and thread-pool starvation as well as elapsed time. Tune bounded concurrency per workload and retain single-writer state coordinators.

## Completion criteria

- Corrupt or incompatible state cannot prevent startup; migrations, rollback, safe mode, and reset are independently tested.
- An induced crash recovers exact dirty content without silently changing disk files.
- Keyboard-only and screen-reader passes cover shell, Explorer, tabs, splits, editor, Problems, output, terminal, and debugger.
- Repeated open/close, workspace switching, reload, terminal, and debug cycles show no unbounded resource growth.
- Startup, idle memory, typing, restoration, recovery, and sustained-session budgets pass on named fixtures.
- Typing remains within budget during solution load, search, build output, terminal floods, diagnostics, and debugging; no queue, task, model, worker, or snapshot grows without a configured bound.

## Next phase

Use the stable language-provider boundary to deliver the web languages advertised by NovaSharp.
