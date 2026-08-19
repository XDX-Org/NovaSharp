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

## Ownership and data flow

```text
Monaco ITextModel (live text + undo; browser thread)
        |
        | ordered UTF-16 edit batches; no synchronous acknowledgement
        v
bounded per-document channel
        |
        v
DocumentReplica (single writer; sequence + immutable snapshots)
        |
        +--> async save/recovery
        +--> later Roslyn/search consumers
```

- Monaco is authoritative for interactive text and undo/redo while its model is open. `DocumentRecord` owns persistence metadata and leases; `DocumentReplica` is the ordered .NET shadow.
- Each edit batch carries document ID, base sequence, ordered non-overlapping range edits, and resulting sequence. Validate UTF-16 bounds and ordering before applying it.
- The JavaScript callback enqueues and returns. A per-document pump coalesces safe changes, keeps at most one interop send in flight, and never waits in the typing path for disk, Roslyn, Blazor rendering, or a .NET version acknowledgement. Queue overflow or a sequence gap triggers one full-snapshot resync.
- Save and Save As await a replica barrier for the requested Monaco sequence, snapshot once, then write asynchronously. Dirty state compares saved and current sequences; it is not a manually toggled Boolean.
- Reload, formatting, and other .NET-originated replacements use Monaco edit operations with origin guards and deliberate undo stops. Ordinary updates never use whole-model `setValue`.
- Monaco handles token colors and editor-local UI. NovaSharp does not render aligned source/token layers or Blazor overlays for Monaco-owned features.
- Editor buttons, Monaco actions, and shortcuts invoke registered commands; components do not duplicate command behavior.
- Store settings atomically with user/workspace scope and schema version. Redact source text and sensitive paths from logs by default.
- File watching is advisory. A dirty model wins until the user explicitly reloads or resolves a conflict.

## Async and concurrency requirements

- File reads, writes, flushes, watcher events, dialogs, and conflict checks are cancellation-aware and never synchronously block a UI thread.
- One consumer mutates each document replica, so ordering needs no coarse lock. Different documents may be processed concurrently within the global worker limit introduced in phase 1.
- Background work publishes small immutable status snapshots to Blazor. Content changes do not call `StateHasChanged`.
- Queue depth, replica lag, resync count, save-barrier latency, long UI tasks, and model memory are measurable and bounded.
- Shutdown stops producers, drains or checkpoints accepted edits to a deadline, then disposes interop/model leases in dependency order.

## Completion criteria

- Core Monaco editing and shortcuts work offline without a custom editor layer.
- Save preserves the chosen encoding and line endings and cannot corrupt the original on an interrupted write.
- Dirty state updates from edit sequences and clears only after the matching snapshot reaches disk.
- External changes offer compare/reload/keep choices and never overwrite dirty text silently.
- Save during rapid typing writes a sequence-consistent snapshot; a stale or missing edit batch causes resynchronization, not corruption.
- Monaco models, view instances, document replicas, queued work, event handlers, and interop references are disposed when the file closes.
- Tests cover IME composition, surrogate pairs, combining characters, bidi text, tabs, CRLF/LF, multi-line/multi-cursor paste, selection replacement, undo grouping, queue saturation, out-of-order callbacks, cancellation, and shutdown.
- Numeric typing, edit-replication lag, save latency, UI-thread long-task, large-file memory, and repeated-open/close budgets pass on named hardware.

## Next phase

Put files in an asynchronously loaded workspace tree while retaining the proven Monaco/document lifecycle.
