# Phase 5: editor groups and split views

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

Each leaf is an editor group; each branch owns orientation and size ratio. Normalize away empty branches. Duplicate views share one `EditorDocument` and presentation cache entry but retain separate `EditorViewState` records, following DnSpyXDX's separation of document keys from tab/view state. VS Code and Visual Studio provide the interaction reference for moving, copying, and splitting tabs ([VS Code side-by-side editing](https://code.visualstudio.com/docs/editing/userinterface), [Visual Studio editor windows](https://learn.microsoft.com/en-us/visualstudio/ide/how-to-manage-editor-windows?view=visualstudio)).

## Completion criteria

- Dragging and command-driven movement produce the same layout operation.
- Two views of one document update immediately while retaining independent selections and scrolling.
- Undo remains document-wide and coherent across duplicate views.
- Closing one copied view does not dispose its shared model or prompt to save unnecessarily.
- Layout restoration is deterministic and safely falls back to one group on invalid data.
- Pointer, keyboard, high-DPI, and narrow-window interaction tests cover splitters and drop zones.

## Next phase

Load real .NET solutions and give every C# document a Roslyn identity.
