# Phase 10 handoff

Branch: `phase-10` (from `phase-9`)

## Completed

- Serialized restore, build, rebuild, clean, and run tasks with queued/running/terminal states, duration, and exit code.
- Startup project, configuration, target framework, and launch-profile selection from the workbench.
- Direct argument-array process launch with an explicit working directory, allowlisted inherited environment, secret redaction, stdin, and owned-tree stop/restart behavior.
- Bounded build/output retention, copy, export, and clickable diagnostic locations.
- MSBuild error/warning normalization into the shared Problems store with file, range, severity, project, and code.
- Recovery coverage for invalid projects, failed builds, large output, cancellation, and conflicting operations.

## Verification

- Release build with warnings as errors passes locally.
- 86 unit, integration, and acceptance tests pass locally.
- Real .NET fixture projects build, fail diagnostically, run with arguments/stdin, and clean successfully.
- Linux x64 cancellation and descendant cleanup completes under the 5-second budget; output retention is capped at 10,000 entries and 4 MiB.

Four-platform build/test/package and packaged Linux Phase 10 interaction evidence remain required before the delivery-plan status changes to complete.

## Known limitations

- Third-party/MSBuild console diagnostics use the documented text fallback; a packaged structured logger is deferred until it can be loaded consistently by every supported SDK.
- Non-terminal run input is line-oriented. Terminal emulation and byte-exact interactive input begin in phase 11.
