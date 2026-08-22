# Phase 2: editor and file lifecycle

## Goal

Make Monaco-backed opening, editing, saving, and externally changing one file safe without putting .NET or Blazor in the typing hot path.

## Scope

- `Open`, `Save`, `Save As`, reload, undo, redo, find/replace, and editor settings with conventional shortcuts.
- Monaco's C# lexical colors, line numbers, selection, multi-cursor editing, indentation, bracket matching, find/replace, word wrap, and accessibility UI.
- Track canonical URI/path, display name, encoding, line endings, Monaco/shadow sequence, saved sequence, dirty state, and last observed disk metadata.
- Ordered incremental edit replication from Monaco to a versioned .NET shadow.
- Async file reads/writes and watcher processing; write through a temporary sibling followed by atomic replacement when the platform permits.
- Detect external modification/deletion, read-only files, decoding failures, and save conflicts.
- Prompt before closing with unsaved changes.
- Introduce the command registry, typed configuration, and structured notifications/logging.

Not included: multiple documents, workspace trees, semantic C# features, custom syntax rendering, or settings UI.

## Decisions this phase depends on

[ADR 0002](decisions/0002-document-lifecycle.md) resolves the three questions the delivery plan required before the
phase started:

- **The edit journal is in memory only.** Nothing about the replica reaches disk, so a crash discards unsaved edits.
  Crash recovery belongs to [phase 14](phase-14-durable-workbench.md), which owns the persistence service any journal
  would be built on. The replication protocol is journal-shaped so that phase can persist it unchanged.
- **Encoding is a catalogue, not a default with an escape hatch.** Every encoding the running framework can round-trip
  is offered. A byte-order mark decides; otherwise the configured default is tried strictly; a strict failure opens the
  document with a byte-preserving fallback and says so. Reopening with an encoding and converting to one are separate
  commands and are never conflated.
- **Settings are versioned JSON in two scopes**, user and workspace, written through the same replace-in-one-step path
  as document saves.

## Line endings and what Monaco can represent

Monaco represents a line feed or a carriage-return pair and nothing else. A carriage-return-only document is therefore
decoded, normalized to line feeds for the editor, and converted back to carriage returns when it is written. The
conversion happens at those two boundaries only, so an offset in the replica always means what it means in Monaco.
Mixed endings are recorded as mixed, normalized to the dominant one, and surfaced in the status bar before a save
rewrites the rest of the file.

## Delivered so far

The document lifecycle: the replication pump, the versioned replica and its save barrier, encoding and line-ending
resolution, open, save, save-as, reload, reopen-with-encoding, comparison against the file on disk, external-change
detection with compare/reload/keep, and prompts before anything discards unsaved text.

The three cross-cutting foundations this phase introduces are in place:

- **The command registry.** Stable identifiers, handlers, enablement predicates, platform-neutral keybindings, and
  palette metadata. It is authoritative: the editor is handed the descriptors and binds what it is given, so a toolbar
  button, a shortcut, and a notification's action all resolve one command with one enablement rule. A keybinding the
  editor cannot resolve is returned and reported rather than dropped.
- **The typed configuration service.** Validated defaults, a user and a workspace scope merged key by key, and atomic
  versioned JSON storage through the same replace-in-one-step write documents use. A value it cannot use is ignored
  *and reported*, never silently dropped, and a file written by a newer schema is left alone.
- **Structured notification and logging.** Severity, identity so a repeated condition replaces itself rather than
  stacking, actions named as commands, a bounded log, and redaction by default — document text never reaches the log,
  and a path is reduced to its file name and a digest of its directory.

Still open in this phase, and required before it can be called complete:

- [CI](../.github/workflows/ci.yml) now runs the bootstrap, `dotnet test`, 64 browser gates, RID-specific publish, and
  the published native smoke and performance verifier on every supported runtime identifier. The new workflow has not
  yet produced one green run on every row.
- The named local `win-x64` Release fixture passes. [Qualification run 32568881427](https://github.com/XDX-Org/NovaSharp/actions/runs/32568881427)
  exercised all six rows and exposed CI-fixture defects in the Linux WebKit sandbox setup and browser workload plus
  `win-x64` startup misses; those gates are being corrected before qualification is repeated.

## Performance budgets

Set here because the delivery plan requires the startup, typing, and large-file budgets to exist before this phase is
implemented. Each is a per-platform figure: a result on one runtime identifier is not a result for the others, and the
fixture hardware must be named in the record alongside the number.

| Budget | Limit | Fixture |
|---|---|---|
| Cold start to an interactive editor | 2,500 ms | `src/NovaSharp/Workbench.cs`, opened from a cold page cache |
| Warm start to an interactive editor | 1,200 ms | The same file, second launch |
| Idle resident memory, one small file open | 400 MB | `src/NovaSharp/Workbench.cs` |
| Keystroke to paint, while a background workload runs | p95 16 ms, p99 33 ms | 60 s of sustained typing in a 2,000-line file |
| Longest UI-thread task during that run | 50 ms | The same run |
| Edit-replication lag, Monaco sequence to replica | p95 50 ms, p99 150 ms | The same run |
| Replication queue depth during that run | 25% of capacity | The same run |
| Save barrier, 1 MB document, typing throughout | p95 120 ms | A generated 1 MB C# file |
| Save to disk, 1 MB document | p95 250 ms | The same file |
| Resident memory added by a 10 MB file | 5x the file size | A generated 10 MB C# file |
| Resident memory after 100 open/close cycles | Baseline + 10%, zero live models | Alternating between two files |

## Completion criteria

- **Met.** Core Monaco editing and shortcuts work offline without a custom editor layer. Save and save-as are bound to
  the platform-neutral modifier through Monaco actions; find, replace, undo, redo, and word wrap remain Monaco's own.
- **Met.** Save preserves the chosen encoding and line endings and cannot corrupt the original on an interrupted write:
  the bytes go to a temporary sibling and the original is replaced in one step, or not at all.
- **Met.** Dirty state updates from edit sequences and clears only after the matching snapshot reaches disk. It is
  computed from Monaco's alternative version identifier, so undoing back to the saved text clears it.
- **Met.** External changes offer compare, reload, or keep, and never overwrite dirty text silently. The comparison is
  a Monaco diff of the file against the editor's live model, so it shows unsaved text and stays editable. A clean
  document follows the file when the user has asked it to; a dirty one keeps what they typed until they answer.
- **Met.** Save during rapid typing writes a sequence-consistent snapshot; a stale or missing edit batch causes
  resynchronization, not corruption. Both paths are covered by tests.
- **Met.** Monaco models, view instances, document replicas, queued work, event handlers, and interop references are
  disposed when the file closes.
- **Met.** Tests cover IME composition, surrogate pairs, combining characters, bidi text, tabs, CRLF/LF/CR,
  multi-line and multi-cursor edits, selection replacement, undo grouping, queue saturation, out-of-order and stale
  batches, cancellation, and shutdown, alongside the registry's enablement and failure handling, settings merging and
  validation, and redaction. 217 assertions run under `dotnet test`; 64 browser gates run in `tests/editor-host`.
- **Met.** The command registry, the typed configuration service, and structured notification and logging are
  introduced by this phase and are in place, with the workbench driven through them rather than around them.
- **Met locally; per-platform qualification pending.** The named local `win-x64` fixture records cold/warm startup at
  2,190/978 ms, 74 MB idle working set, a 19 MB working-set increase for a 10 MB file, paint at p95 2.7/p99 11.9 ms,
  browser replication at p95 3.8/p99 13.1 ms, managed replication at p95 0.01 ms, 1/256 managed and 6/256 browser
  queue depth, a 0.2 ms 1 MB save barrier, a 17.6 ms 1 MB save, and 17→18 MB heap after 100 lifecycle cycles.
- **Not met.** The new qualification workflow has not yet been green on every supported runtime identifier.

## Next phase

Put files in an asynchronously loaded workspace tree while retaining the proven Monaco/document lifecycle.
