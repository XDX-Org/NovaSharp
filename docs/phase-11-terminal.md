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

## Implementation

- `Porta.Pty` 1.0.7 owns the cross-platform transport: ConPTY plus a Windows Job Object on Windows 10 1809+, and `forkpty` on Linux/macOS.
- NovaSharp owns session lifecycle and a small, headless ANSI/VT presentation buffer. Escape sequences never enter Razor markup; rendered text is encoded by Blazor, and OSC 8 links accept only absolute HTTP(S) targets.
- Each terminal has an independent profile, name, working directory, PTY, and bounded buffer. Sessions survive panel hide/show and are intentionally not persisted.
- The emulator answers primary and secondary device-attribute queries used by shells such as fish, and visually wraps long rendered lines to the panel width.
- Scrollback defaults to 5,000 lines and 4 MiB. Input and resize handling target one animation frame (16.7 ms) before PTY/system scheduling.
- Phase 10 and terminal processes share the same ownership rule—NovaSharp starts, stops, and disposes only processes it owns—while PTY byte streams remain separate from the line-oriented build service.

## Completion criteria

- Interactive shells work on every supported OS with resize, Unicode, colors, links, signals, and process exit.
- Malicious escape sequences cannot inject workbench markup or invoke commands.
- Closing, stopping, shell failure, PTY failure, and application exit clean up owned processes predictably.
- Keyboard, screen-reader labeling, paste confirmation policy, scrollback bounds, and latency budgets are tested.

## Next phase

Establish the managed debugger protocol and lifecycle independently of its full UI.
