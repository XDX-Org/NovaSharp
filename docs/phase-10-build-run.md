# Phase 10: build, run, and output

## Goal

Complete the normal edit/build/run loop inside NovaSharp with structured state and safe process ownership.

## Scope

- Build, rebuild, clean, restore, and run a selected startup project and configuration.
- Queued/running/succeeded/failed/canceled task state with duration and exit code.
- Output channels with bounded retention, copy/export, and clickable file locations.
- Structured MSBuild diagnostics published to the phase 8 diagnostic store.
- Stop/restart and stdin for owned non-terminal processes.

Terminal emulation and debugging are deferred to phases 11–13.
Project-aware target-framework and launch-profile configuration is deferred to phase 17.

## Design constraints

- Use argument arrays, explicit working directories, and allowlisted environment inheritance; never construct shell command strings from user input.
- Prefer structured MSBuild logging. Keep raw logs separately; console parsing is a fallback for third-party tools.
- Track the complete owned process tree without signaling unrelated processes.
- Define concurrency policy for restore/build/run and serialize conflicting operations.
- Redact secrets in commands and environment values displayed or logged.

## Completion criteria

- Fixture projects build and run under supported configurations using project defaults.
- Problems and build output agree on file, range, severity, project, and diagnostic code.
- Cancellation and application shutdown clean up owned descendants on every supported OS.
- Invalid SDKs, restore failures, malformed profiles, huge output, and interactive input fail recoverably.
- Numeric cancellation, cleanup, output-memory, and diagnostic-publication budgets are met.

## Next phase

Add interactive shell sessions over the proven process boundary.
