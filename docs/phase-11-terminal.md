# Phase 11: integrated terminal

## Goal

Provide reliable interactive terminal sessions without coupling terminal emulation to workbench UI state.

## Scope

- Explicit shell/profile selection, multiple sessions, rename, restart, and working-directory inheritance.
- PTY/ConPTY-backed input/output, resize, Unicode, ANSI color, links, selection, copy/paste, and search.
- Session exit state and confirmation before closing a live foreground process.
- A dedicated terminal panel whose sessions survive hide/show but not application restart unless explicitly supported later.

## Design constraints

- Choose and document the terminal emulator, native PTY strategy, licensing, and update ownership before implementation.
- Bound scrollback by configurable lines and bytes; process output must not exhaust UI memory.
- Keep terminal escape handling isolated from application markup and commands.
- Route lifecycle through the phase 10 process service and preserve exact byte/encoding semantics.

## Completion criteria

- Interactive shells work on every supported OS with resize, Unicode, colors, links, signals, and process exit.
- Malicious escape sequences cannot inject workbench markup or invoke commands.
- Closing, stopping, shell failure, PTY failure, and application exit clean up owned processes predictably.
- Keyboard, screen-reader labeling, paste confirmation policy, scrollback bounds, and latency budgets are tested.

## Next phase

Establish the managed debugger protocol and lifecycle independently of its full UI.
