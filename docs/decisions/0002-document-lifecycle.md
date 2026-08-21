# 0002: Document lifecycle — replication boundary, text encoding, and settings storage

## Status

Accepted.

## Context

[ADR 0001](0001-monaco-editor.md) fixes Monaco as the only editor and states that .NET keeps a versioned shadow of each
open document. It does not say where that shadow's durability ends, how bytes on disk become the text Monaco is given,
or where the settings that govern either of those live. The [delivery plan](../delivery-plan.md#open-decisions) lists
all three as decisions that must be resolved before phase 2 starts, because each one constrains the file, recovery, and
configuration surface of every later phase.

## Decision

### 1. The edit journal is in memory only, and phase 2 says so out loud

`DocumentReplica` — the ordered .NET shadow — lives in memory for as long as its document is open, and nothing about it
is written to disk. A process that dies with unsaved edits loses those edits.

The only guards phase 2 ships are the ones that need no durable store: dirty state derived from edit sequences, a prompt
before closing a dirty document, and a save path that cannot corrupt the file it is replacing.

This is a recorded gap, not an oversight. Crash recovery is owned by
[phase 14](../phase-14-durable-workbench.md), which also owns the persistence service, the versioned schemas, and the
corruption fallback that any durable journal would have to be built on. Writing a journal in phase 2 would mean
inventing that machinery twice, and inventing the smaller of the two first.

The replication protocol is nevertheless journal-shaped from the start: every batch carries a document ID, a base
sequence, ordered non-overlapping range edits, and a resulting sequence. Phase 14 can persist that stream without
renegotiating it.

**User-visible behavior of the gap:** closing a dirty document prompts, so no edit is lost to an ordinary close. A
crash, a forced quit, or a power loss discards every unsaved edit with no way to recover it. NovaSharp must not present
a recovery promise it cannot keep, so no "restoring your session" affordance ships before phase 14.

### 2. Text encoding is a catalogue, not a default with an escape hatch

NovaSharp reads and writes whatever encoding the platform can name. `Encoding.GetEncodings()`, extended with
`CodePagesEncodingProvider`, is the catalogue; NovaSharp does not curate a hard-coded list, because the set that the
running framework can actually round-trip is the only honest answer to what NovaSharp supports.

Opening a file resolves its encoding in this order:

1. **A byte-order mark decides.** UTF-8, UTF-16 LE/BE, and UTF-32 LE/BE marks are recognized and the mark is recorded as
   part of the document's encoding, so a save reproduces it exactly.
2. **Otherwise the configured default encoding is tried strictly.** Strict means a decoder that throws on any byte
   sequence it cannot represent, so a mis-detection is an event rather than a screen of replacement characters.
3. **A strict failure is reported, not papered over.** The document opens with the configured fallback encoding — one
   that can round-trip every byte, defaulting to ISO-8859-1 — and NovaSharp says which encoding it used and offers to
   reopen with another. It never substitutes U+FFFD, because a buffer containing replacement characters cannot be saved
   back over its own file without destroying data.

Each document carries its resolved encoding as persistence metadata. Saving re-encodes with that encoding and reproduces
its byte-order mark. Changing a document's encoding is an explicit user action with two forms, which are not the same
operation and are never conflated: **reopen with encoding** re-reads the bytes on disk and discards nothing but the
current decode, and **save with encoding** re-encodes the text that is in the editor. Reopening is refused while the
document is dirty; converting is refused, with the offending characters named, when the target encoding cannot represent
the current text.

An encoding that cannot round-trip the document's current text is marked as lossy wherever it is offered, so the warning
appears before the choice rather than after it.

Line endings are resolved and preserved on the same principle: the dominant ending in the file becomes the document's
ending, mixed endings are recorded as mixed and left alone, and a file with no line break at all takes the configured
default. Saving writes the document's ending. Monaco is told which one it is so its own newline insertion matches.

### 3. Settings are JSON files, versioned, scoped, and written atomically

Settings are stored as UTF-8 JSON with a `schemaVersion` field, in two scopes:

| Scope | Location | Purpose |
|---|---|---|
| User | The platform's per-user configuration directory, resolved through the platform seam, under `NovaSharp/settings.json` | Applies to every workspace |
| Workspace | `.novasharp/settings.json` beside the opened workspace root | Applies to one workspace and travels with it in source control |

Workspace values override user values key by key; a missing file is an empty scope, not an error. Every write goes
through the same temporary-sibling-then-replace path that document saves use, so an interrupted write cannot leave a
truncated settings file. A file that fails to parse is reported, backed up beside itself, and treated as empty rather
than silently rewritten.

JSON is chosen over a binary or registry-shaped store because the workspace scope is a source-controlled file that
humans diff and merge, and because a format the platform seam does not have to abstract is one fewer place for an
operating-system branch to appear.

## Consequences

- The catalogue is built by registering `System.Text.CodePagesEncodingProvider`, which `net10.0` already supplies: the
  `System.Text.Encoding.CodePages` package is part of the shared framework and referencing it explicitly is rejected as
  redundant. Registering the provider is not optional, though. Without it the catalogue would be Unicode plus
  ISO-8859-1, and which encodings a user could open a file with would differ per operating system — exactly what the
  platform parity rule forbids.
- Phase 2 must surface encoding and line endings in the workbench, because a document property that changes what a save
  writes cannot be invisible.
- The dirty-state contract is arithmetic on sequences, never a manually toggled flag, so a save that races typing either
  writes a consistent snapshot or is superseded.
- Phase 14 inherits a replication stream it can persist as-is, and inherits the obligation to do so.
- Any later phase that needs durable state uses the phase-3 persistence service rather than adding a second storage
  shape. Settings are the first schema versioned under that rule, from their introduction.
