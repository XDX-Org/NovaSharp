# Phase 9: workspace search and replace

## Goal

Find and safely change code across a large workspace without blocking editing.

## Scope

- Quick Open by file name and command palette backed by the shared command registry.
- Plain-text and regex search with case/word filters, include/exclude globs, cancellation, and streamed results.
- Bounded result groups, match previews, keyboard navigation, and opening results in the current editor group.
- Replace preview and transactional multi-file replacement across open buffers and unopened files.

Build, run, output, and terminals move to phases 10 and 11.

## Design constraints

- Search bounded batches off the UI thread and exclude ignored/generated/binary files by policy.
- Search dirty buffers instead of stale disk content for open documents.
- Version every result. Revalidate affected documents and disk metadata immediately before replacement.
- Apply replacement through the phase 8 workspace-edit transaction so cancellation or failure changes nothing.
- Treat regex timeouts, unreadable files, invalid encodings, and symlink boundaries as recoverable per-file results.

## Completion criteria

- Results are incremental, ordered deterministically, cancellable, and bounded on the large-workspace fixture.
- Quick Open remains responsive with duplicate file names and ignored paths.
- Replace preview shows exact ranges and rejects changed inputs without partial writes.
- Unicode, mixed line endings, binary detection, symlinks, permissions, regex timeout, and dirty-buffer cases are tested.
- Numeric throughput, first-result latency, and retained-memory budgets are met on named fixture hardware.

## Next phase

Use the command, diagnostic, and process boundaries to build and run projects.

## Verification budgets

The named performance fixture contains 5,000 UTF-8 C# files on the local Linux x64 development host:

- first search result under 2 seconds;
- complete search under 8 seconds;
- retained search memory under 64 MiB;
- at most 10,000 retained results, streamed in batches of 64 by default.

See `PHASE9_HANDOFF.md` for current verification evidence and known limitations.
