# Phase 9: search, build, run, and terminal

## Goal

Find code across the workspace and execute normal edit/build/run loops inside NovaSharp.

## Scope

- Quick Open by file name and command palette backed by the shared command registry.
- Workspace text search with plain text/regex, case/word filters, include/exclude globs, cancellation, and streamed results.
- Replace preview and transactional multi-file replace.
- Build, rebuild, clean, restore, and run the selected startup project/configuration.
- Structured task state: queued/running/succeeded/failed/canceled, duration, and exit code.
- Output panel with separate channels and clickable parsed file locations.
- Integrated terminal sessions with resize, copy/paste, links, process exit, and explicit shell selection.
- Stop/restart commands and clear ownership rules for spawned process trees.

## Design constraints

- Search streams bounded batches and excludes ignored/generated/binary files by policy; it never builds one giant result list in memory.
- Prefer structured MSBuild logging for diagnostics and progress. Console parsing is a fallback for third-party tools.
- Run commands use argument arrays and explicit working directories; do not concatenate user input into shell command strings.
- Terminal emulation lives in a dedicated component behind a process-session service.
- Closing a terminal or the application asks before terminating a live foreground process.
- Build diagnostics enter the same diagnostic store as phase 8 under a distinct producer.

## Completion criteria

- Search remains responsive, cancellable, and incrementally visible on a large workspace.
- Replace preview shows exact file/range changes and detects versions changed before apply.
- Build output and Problems agree on file, line, severity, project, and diagnostic code.
- Run uses the selected target framework/profile and supports input without freezing the UI.
- Stop terminates owned child processes on Windows, Linux, and macOS without targeting unrelated processes.
- Terminal keyboard input, resizing, Unicode, ANSI color, and cleanup have cross-platform tests.

## Next phase

Add managed debugging and harden the workbench into a durable daily-use shell.
