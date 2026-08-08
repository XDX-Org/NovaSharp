# ADR 0005: Phase 11 terminal engine

Status: accepted

## Decision

Use MIT-licensed `Porta.Pty` 1.0.7 for PTY/ConPTY transport and MIT-licensed `XtermBlazor` 2.4.0, which bundles xterm.js 6.0.0, for terminal emulation and rendering. NovaSharp owns bounded raw replay, session state, and the workbench integration.

Windows uses ConPTY and Job Objects through the dependency; Linux and macOS use `forkpty`. The supported Windows baseline already exceeds ConPTY's Windows 10 1809 minimum. The dependency is pinned and updated by the NovaSharp maintainers with the other application packages.

Do not send terminal output through the phase 10 line-oriented output channel. Both services follow the same owned-process lifecycle, but terminal I/O preserves raw UTF-8 byte boundaries and terminal resize semantics.

## Security and limits

xterm.js parses terminal data inside its isolated terminal surface rather than application markup. Its security guidance applies: terminal output is untrusted and is never interpolated into NovaSharp HTML or commands. xterm.js retains at most 5,000 scrollback lines and NovaSharp retains at most 4 MiB of raw replay data.

## Consequences

The native boundary and emulator are supplied and tested upstream. NovaSharp remains responsible for dependency review, byte-exact transport, keyboard/accessibility behavior, process cleanup, and packaged interaction gates. Both packages are pinned and updated by the NovaSharp maintainers.
