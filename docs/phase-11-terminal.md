# Phase 11: integrated terminal

## Goal

Provide reliable interactive terminal sessions without coupling terminal emulation to workbench UI state.

## Scope

- Explicit shell/profile selection, multiple sessions, rename, restart, and working-directory inheritance.
- Pseudoterminal-backed input/output, resize, Unicode, ANSI color, links, selection, copy/paste, and search, through one abstraction over each platform's native pseudoterminal API.
- Session exit state and confirmation before closing a live foreground process.
- A dedicated terminal panel whose sessions survive hide/show but not application restart unless explicitly supported later.

## Design constraints

- Choose and document the terminal emulator, the pseudoterminal strategy for every supported platform, licensing, and update ownership before implementation. A strategy that covers only some platforms is not a decision.
- Bound scrollback by configurable lines and bytes; process output must not exhaust UI memory.
- Keep terminal escape handling isolated from application markup and commands.
- Route lifecycle through the phase 10 process service and preserve exact byte/encoding semantics.
- Read and write pseudoterminal streams asynchronously. Parse escape sequences on a dedicated bounded session worker, batch render updates, and apply scrollback backpressure without delaying Monaco or Blazor input.
- Each session is single-writer for emulator state; sessions may run concurrently within process/memory limits. Cancellation and disposal unblock pending reads and await owned pumps with deadlines.

## Completion criteria

- Interactive shells work on every supported OS with resize, Unicode, colors, links, signals, and process exit.
- Malicious escape sequences cannot inject workbench markup or invoke commands.
- Closing, stopping, shell failure, pseudoterminal failure, and application exit clean up owned processes predictably on every supported platform.
- Keyboard, screen-reader labeling, paste confirmation policy, scrollback bounds, and latency budgets are tested.
- Output floods across multiple sessions remain within worker, queue, frame-time, and memory budgets and cannot starve editor foreground work.

## Next phase

Establish the managed debugger protocol and lifecycle independently of its full UI.
