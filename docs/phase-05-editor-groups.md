# Phase 5: editor groups and split views

## Status

In progress.

## Goal

Arrange documents side by side, move or copy tabs between groups, and view two locations in the same document.

## Scope

- Split the active group left/right/up/down and resize groups with keyboard-accessible splitters.
- Move a tab between groups by command or drag/drop; copy it to create another view of the same document.
- Drop-zone previews for edges, group centers, and tab insertion positions.
- Horizontal and vertical nesting with a bounded layout depth.
- Per-view cursor, selection, scroll, and editor state over a shared document model.
- Close empty groups, focus neighboring groups, and distribute group sizes evenly.
- Restore layout tree, group sizes, tabs, active views, and focus.

Floating native windows and arbitrary dockable tool panels are deferred.

## Layout model

```text
Split(horizontal, 0.58)
├── Group(tabs: A, B; active: B)
└── Split(vertical, 0.50)
    ├── Group(tabs: C)
    └── Group(tabs: A-copy)
```

Each leaf is an editor group; each branch owns orientation and size ratio. Normalize away empty branches. Duplicate views use separate Monaco editor instances attached to the same `ITextModel`, sharing text and undo history while retaining separate validated view-state records. VS Code and Visual Studio provide the interaction reference for moving, copying, and splitting tabs ([VS Code side-by-side editing](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio editor windows](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-manage-editor-windows?view=visualstudio)).

Creating, moving, resizing, or closing a view must not clone model text or synchronously query .NET. Resize notifications are frame-coalesced; editor/model leases and observers have idempotent disposal. Layout persistence runs asynchronously from immutable snapshots.

## Completion criteria

- Dragging and command-driven movement produce the same layout operation.
- Two views of one document update immediately while retaining independent selections and scrolling.
- Undo remains document-wide and coherent across duplicate views.
- Closing one copied view does not dispose its shared model or prompt to save unnecessarily.
- Layout restoration is deterministic and safely falls back to one group on invalid data.
- Pointer, keyboard, high-DPI, and narrow-window interaction tests cover splitters and drop zones.
- Split creation, rapid resize, movement, and closure stay within frame-time and memory budgets with two views editing concurrently.

## Delivered implementation

- `EditorGroupManager` owns a bounded, normalized horizontal/vertical split tree, group-local views, focus, movement,
  copying, closure, even distribution, and immutable UI snapshots. Palette and shortcut commands split in every
  direction, move or copy to the next group, focus neighboring groups, close a group, and distribute sizes.
- Recursive group panes provide accessible tablists and splitters plus tab insertion, center, and edge drop targets.
  Native drag metadata makes the four broad edge previews and center/tab targets work consistently across browser
  engines. Pointer resizing is animation-frame-coalesced and commits once; arrow keys resize through the same ratio operation.
- Every duplicate view is a separate Monaco editor attached to the existing URI-keyed `ITextModel`. Browser gates
  prove shared text and undo, independent cursor/scroll state, and model survival after a copied view closes.
- Workspace-state schema 3 persists the split tree, ratios, ordered views, active view per group, focused group, and
  validated per-view state. Malformed, duplicate, excessive, or unknown persisted state falls back to one group.
- Managed tests cover movement/copy equivalence, normalized closure, layout bounds, persistence, and invalid-state
  recovery. Chromium and WebKit fixtures cover pointer/keyboard resizing, edge/center drops, narrow windows, high DPI,
  200% zoom, and lifecycle/performance budgets.

## Qualification status

Local managed, Chromium, and WebKit gates pass, as does an explicit `win-x64` Release publish and packaged native
smoke/performance run. Phase 5 remains in progress until the same bootstrap, build/test, browser, RID-specific publish,
packaged native smoke, performance, disposal, and retained evidence pass on all six supported runtime identifiers from
the same Phase 5 commit.

The layout/model ownership contract is recorded in [ADR 0005](decisions/0005-editor-groups.md). Workspace-state schema
3 is an additive migration from schema 2; older workspaces restore their documents into one group.

## Next phase

Load real .NET solutions and give every C# document a Roslyn identity.
