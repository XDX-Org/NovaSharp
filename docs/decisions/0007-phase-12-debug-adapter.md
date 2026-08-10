# ADR 0007: managed debug adapter

## Decision

Use the MIT-licensed Samsung `netcoredbg` Debug Adapter Protocol implementation. Pin adapter archives and SHA-256 hashes per supported runtime, acquire them during CI, and package them beside the application. NovaSharp owns the bounded DAP transport, session state, source identity, cancellation, and process lifetime.

Upstream 3.2 does not publish Intel macOS, so Intel macOS uses pinned 3.1.3-1062 while the other targets use pinned 3.2.0-1092. Both are protocol-compatible within NovaSharp's negotiated capability boundary. Every archive is hash-verified before packaging.

Launch targets only processes started for the selected project. Attach requires an explicit process selection and never grants NovaSharp ownership of that process. Stop and disconnect therefore use distinct ownership rules.

## Limits

DAP messages are limited to 8 MiB, headers to 4 KiB, and pending requests to 128. Interactive requests use explicit timeouts. Paused-state data is keyed by a monotonically increasing pause epoch and discarded after resume.

## Consequences

Debug functionality is capability-driven. Unsupported adapter requests stay disabled and never silently fall back to shell commands or in-process debugging APIs.
