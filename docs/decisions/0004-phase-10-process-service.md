# ADR 0004: owned .NET process boundary

## Decision

NovaSharp runs restore, build, clean, rebuild, and non-terminal project execution through one serialized process owner. It launches `dotnet` directly with argument arrays, an explicit project working directory, redirected streams, and a small allowlist of inherited environment variables. Cancellation kills the process and its descendants through the platform process-tree API.

Build output is retained separately in a bounded channel. Located MSBuild console diagnostics are normalized and published to the shared Problems store; raw lines remain available for third-party output that has no structured location. Commands and logs redact secret-shaped argument values and never display environment values.

## Consequences

- Restore, build, and run cannot mutate the same output tree concurrently.
- Only processes started by NovaSharp can receive input or termination signals.
- Phase 11 can reuse ownership and lifecycle rules but must add a PTY/ConPTY boundary for terminal emulation.
- Console diagnostic parsing remains a fallback until an out-of-process structured MSBuild logger can be packaged and loaded consistently on every supported SDK.
