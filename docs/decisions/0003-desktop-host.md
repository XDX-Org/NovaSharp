# 0003: Use the upstream Photino desktop host

## Status

Accepted.

## Context

Phase 1 originally used `PhotinoXDX.Blazor` 0.1.0-preview.6. Its Windows and Linux adapters passed NovaSharp's native
smoke, but its
[macOS adapter](https://github.com/XDX-Org/PhotinoXDX/blob/237fd79047e35dc1d36218ff20438917920d4375/PhotinoEx.Core/Platform/Mac/MacPhotinoEx.cs)
constructs another `PhotinoExWindow` as its native window. That window selects the same macOS adapter, recursively, so
the Cocoa message loop never produces an interactive editor. Runs
[32571974955](https://github.com/XDX-Org/NovaSharp/actions/runs/32571974955) and
[32572423120](https://github.com/XDX-Org/NovaSharp/actions/runs/32572423120) exposed that path on both macOS runtime
identifiers after the application bundle itself passed signature verification.

A phase cannot be complete with a host known not to launch on two supported runtime identifiers. Carrying a private
replacement for the host would also make NovaSharp own a second windowing stack before its editor shell is qualified.

## Decision

NovaSharp uses the exact `Photino.Blazor` 4.0.13 package. Its dependency graph pins `Photino.NET` 4.0.16 and
`Photino.Native` 4.0.22, whose package contains native assets for every runtime identifier in NovaSharp's supported
matrix.

NovaSharp adapts the upstream dialog API behind its platform namespace and keeps native dialogs asynchronous at the
call site. No product code selects a host by operating system. The same entry point, Blazor root, packaged `app://`
origin, smoke arguments, and shutdown contract serve all six runtime identifiers.

## Consequences

- The host namespace and dialog enum names follow upstream Photino rather than the preview fork.
- Host upgrades require the six-row published native smoke; package compatibility or a single-platform launch is not
  sufficient evidence.
- The macOS CI application bundle remains qualification scaffolding. Phase 17 still owns release packaging, identity,
  signing, and notarization.
