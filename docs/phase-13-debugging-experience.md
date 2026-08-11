# Phase 13: debugging experience

## Goal

Inspect and control managed execution through a complete, keyboard-operable debugging workflow.

## Scope

- Step over/into/out and run to cursor.
- Threads, call stack, scopes, variables, watches, evaluate, and debug console.
- Conditional, hit-count, and log-point breakpoints.
- Exceptions and configurable break behavior.
- Read-only mapped/decompiled-source presentation when source is outside the workspace.
- Context-sensitive debug layout that retains the normal layout.

## Design constraints

- Variable expansion is lazy, cancellable, paged where supported, and bounded for hostile object graphs.
- Key stack frames, scopes, and evaluations to paused session state; discard responses after resume.
- Clearly label side-effecting evaluation and never evaluate properties implicitly when the engine cannot do so safely.
- Preserve user-authored breakpoints separately from adapter binding state.
- Keep debug console input distinct from terminal and application stdin.

## Completion criteria

- A user can build, launch, break, inspect, evaluate, step, stop, edit, rebuild, and relaunch fixture applications.
- Multi-thread, async stack, exception, optimized-code, missing-source, large-collection, and adapter-loss cases are tested.
- Breakpoints survive edits and restarts and visibly explain binding failures.
- Keyboard and screen-reader flows cover every debug command and view.
- Step, stack, expansion, and evaluation latency/memory budgets are met.

## Next phase

Harden persistence, configuration, recovery, accessibility, and performance across the full C# workbench.

## Implementation status

Implemented: continue, pause, step over/into/out, run to cursor, threads, bounded stacks/scopes/lazy variables,
pause-epoch stale-response rejection, watch/repl evaluation contexts, exception filters, stop reasons, and
keyboard-operable debugger controls. Optimized/async/multithread interaction and screen-reader release evidence
remain required before marking the phase complete.

## Verification budgets

On the managed console fixture, evaluation and stack refresh must each complete within five seconds. Inspection
retains at most 256 frames and 10,000 variables per reference, returns at most 1,000 variables per page, and drops
all paused-state data immediately on resume. Thread lists are capped at 1,024 entries.
