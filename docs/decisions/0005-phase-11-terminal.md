# ADR 0005: Phase 11 terminal engine

Status: accepted

## Decision

Use MIT-licensed `Porta.Pty` 1.0.7 for PTY/ConPTY transport and keep terminal parsing, bounded scrollback, session state, and workbench rendering inside NovaSharp.

Windows uses ConPTY and Job Objects through the dependency; Linux and macOS use `forkpty`. The supported Windows baseline already exceeds ConPTY's Windows 10 1809 minimum. The dependency is pinned and updated by the NovaSharp maintainers with the other application packages.

Do not send terminal output through the phase 10 line-oriented output channel. Both services follow the same owned-process lifecycle, but terminal I/O preserves raw UTF-8 byte boundaries and terminal resize semantics.

## Security and limits

Terminal text is rendered as encoded component content, never markup. Unsupported control sequences are discarded. OSC 8 links are limited to absolute HTTP(S) URLs. Buffers retain at most 5,000 lines or 4 MiB, whichever is reached first.

## Consequences

The native boundary is supplied and tested upstream on all supported operating systems. NovaSharp remains responsible for parser tests, dependency review, keyboard/accessibility behavior, process cleanup, and packaged interaction gates.
