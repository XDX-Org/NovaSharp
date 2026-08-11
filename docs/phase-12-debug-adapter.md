# Phase 12: debug adapter foundation

## Goal

Launch or attach to managed programs through a selected, distributable debugger engine with a deterministic lifecycle.

## Scope

- A debugger-engine boundary and documented adapter/protocol implementation.
- Launch and attach configurations with capability negotiation and validation.
- Session state machine for start, configure, running, paused, terminated, failed, and disconnected states.
- Source/function breakpoints with verified, moved, rejected, and pending states.
- Source identity using normalized paths, checksums where available, and explicit source mapping.
- Continue, pause, stop, restart, and adapter-loss recovery sufficient for an end-to-end fixture.

Full stack/variable UI and stepping experience move to phase 13.

## Design constraints

- Decide engine, transport, redistribution terms, supported runtimes, attach permissions, and unsupported capability behavior before implementation.
- Validate all adapter messages and bound message size, pending requests, output, and shutdown time.
- Never match source solely by file name. Show unmapped or stale source explicitly.
- Commands declare valid session states and transition through one coordinator.
- Terminating a debug session targets only processes NovaSharp owns or the user explicitly attached to.

## Completion criteria

- Launch and attach fixtures reach breakpoints on every supported OS and runtime in the support matrix.
- Breakpoint verification and source mapping remain correct through rebuilds and edits.
- Invalid configuration, attach denial, malformed messages, adapter crash, target exit, and disconnect are recoverable.
- Protocol contract, state-machine, ownership, timeout, and cleanup tests pass.
- Numeric launch, breakpoint-bind, pause, and shutdown budgets are met.

## Next phase

Build the daily debugging experience over the proven session boundary.

## Implementation status

Implemented: pinned netcoredbg packaging; bounded DAP transport; launch/attach ownership; negotiated capabilities;
deterministic session and adapter-loss states; conditional, hit-count, log, function, and temporary run-to-cursor
breakpoints; SHA-256 source identity and explicit longest-root source mapping; bounded request and shutdown behavior.

Cross-platform completion remains evidence-gated by the macOS source-binding limitation in `known-limitations.md`.

## Verification budgets

The managed console fixture must initialize, launch, configure, and pause within 15 seconds. Stack, variable,
thread, scope, breakpoint, control, and evaluation requests each have a five-second deadline; disconnect has a
three-second deadline. DAP payloads are limited to 8 MiB, headers to 4 KiB, and pending requests to 128.
