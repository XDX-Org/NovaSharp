# Phase 2: editor and file lifecycle

## Goal

Replace the prototype text area with a production editor and make opening, editing, saving, and externally changing one file safe.

## Scope

- Replace the prototype text area with a pure C#/Blazor source editor based on DnSpyXDX's virtualized presentation pipeline.
- C# syntax colorization, line numbers, selection, find/replace, undo/redo, indentation, bracket matching, and configurable word wrap.
- `Open`, `Save`, `Save As`, and reload commands with conventional shortcuts.
- Track canonical path, display name, encoding, line endings, version, dirty state, and last observed disk metadata.
- Write through a temporary sibling file followed by replacement when the platform permits.
- Detect external modification, deletion, read-only files, decoding failures, and save conflicts.
- Prompt before closing the application with unsaved changes.

Not included: multiple documents, workspace trees, semantic C# features, or settings UI.

## Presentation design

```text
EditorDocument (mutable text, version, undo, line index)
        |
        v
EditorPresentationSnapshot (immutable visible-line input)
        |
        v
Virtualize<EditorLine> -> EditorLineView token fragments
        ^
        |
native input/selection layer -> versioned edit operations
```

- Port the useful boundaries from DnSpyXDX's `SourceDocumentModel`, `SourceView`, `SourceLineView`, `SourcePresentationCache`, and `SourceViewStateStore`; do not copy decompiler-specific symbol or debugger behavior.
- Use `Virtualize<TItem>` with fixed-height rows, stable line keys, bounded overscan, cancellable providers, and a precomputed horizontal canvas width.
- Keep a stateful C# tokenizer with checkpoints. Invalidate from the first changed line until lexical state converges with the previous snapshot.
- A browser-native input layer handles caret, selection, clipboard, keyboard composition, and IME. Interop reports edit/selection/scroll operations; it does not tokenize, render source markup, resolve symbols, or own document policy.
- C# owns text, file I/O, versions, undo history, find results, classifications, and view-state policy.
- A monotonically increasing document version accompanies editor changes and async responses.
- File watching is advisory. A dirty buffer wins until the user explicitly reloads or resolves a conflict.
- Start with fixed-height, non-wrapped lines. Enable wrapping only after input, selection, hit testing, and variable-height virtualization pass their own acceptance tests.

## Completion criteria

- Core editing and keyboard shortcuts work without network access.
- Save preserves chosen line endings and does not corrupt the original on an interrupted write.
- Dirty state appears immediately and clears only after the saved version reaches disk.
- External changes offer compare/reload/keep choices; they never overwrite dirty text silently.
- Document models, presentation snapshots, cache leases, event handlers, and interop references are disposed when the file closes.
- Rendering a large file keeps DOM row count near the visible range plus overscan.
- Editing tests cover IME composition, surrogate pairs, combining characters, tabs, CRLF/LF, multi-line paste, selection replacement, and undo grouping.
- Unit tests cover path normalization, dirty transitions, encoding, line endings, and save conflicts.

## Next phase

Put files in a workspace tree while retaining the proven document lifecycle.
