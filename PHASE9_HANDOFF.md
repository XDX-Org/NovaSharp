# Phase 9 handoff

Branch: `phase-9` (from `phase-8` at `8d7d547`)

## Completed

- Quick Open with fuzzy file-name/path ranking, duplicate-name paths, ignored-path policy, cancellation, and keyboard selection.
- Command palette backed by the shared command registry and live command enablement.
- Incremental plain-text and regex workspace search with case/word filters, include/exclude globs, deterministic ordering, bounded batches/results, previews, and navigation.
- Dirty-buffer search, binary and invalid-encoding recovery, regex timeouts, symlink boundaries, and cancellation.
- Replace preview and transactional multi-file replacement with buffer versions, disk stamps, encoding preservation, preflight conflict rejection, and rollback.
- Linux packaged Phase 9 smoke gate plus four-platform build/test/package workflow coverage.

## Verification

- Release build with warnings as errors passes locally.
- 79 unit and acceptance tests pass locally.
- Named fixture: 5,000 UTF-8 C# files on the local Linux x64 development host.
- Budgets: first result under 2 seconds, full search under 8 seconds, retained memory under 64 MiB.

The four-platform workflow and packaged Linux Phase 9 smoke must pass after push before the delivery-plan status changes to complete. The local host does not provide `xvfb-run`.

## Known limitations

- Include/exclude globs support `*`, `**`, and `?`; brace expansion and negated patterns are not supported.
- Search targets local workspace files only and does not traverse symbolic links.
