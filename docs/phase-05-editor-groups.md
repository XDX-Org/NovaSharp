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

## Required contracts

- A process-wide document registry owns buffers by canonical URI and reference-counts views. Groups own ordered views, never documents.
- Layout nodes, groups, and views have stable IDs. A split owns exactly two children, an orientation, and a ratio clamped from `0.1` through `0.9`.
- Layout depth is limited to eight splits. A split request that exceeds the limit leaves the tree unchanged and reports why.
- Removing an empty group normalizes its parent to the surviving child. The workbench always retains at least one group.
- Moving a view transfers it atomically without releasing its document. Copying acquires another registry reference and creates independent view state.
- Left/up splits insert before the source group; right/down splits insert after it. The new group becomes focused. Center drops insert into the target tab strip.
- Pointer and command routes call the same move, copy, split, resize, normalize, and focus operations. A cancelled drag makes no model change.
- Preview ownership is per group. Moving preserves preview state; copying creates a pinned view so a later preview cannot silently replace it.
- Closing a non-final view of a dirty document never prompts. Closing the final view uses the existing aggregated dirty-document decision.
- Neighbor focus uses visual geometry, then most-recent focus as a deterministic tie-breaker. Keyboard commands exist for every split, move, copy, resize, equalize, and focus operation.

## Persistence and migration

Phase 5 replaces the flat Phase 4 session with schema version 2. It stores the layout tree, ratios, stable IDs,
group tab order, active view per group, focused group, and per-view selection and scroll state. Loading version 1
creates one group without changing tab order. Invalid nodes, duplicate IDs, non-finite ratios, excessive depth,
and missing active references fall back safely to one normalized group. Workspace-contained paths should be stored
portably; missing files remain visible using the Phase 4 recovery behavior.

## Interaction and accessibility

- Splitters expose separator roles, orientation, current value, and keyboard increments of 2%; Shift uses 10%.
- Groups and drop zones have stable accessible names and visible keyboard focus. Drop previews do not rely on color alone.
- Splitters enforce a 160-pixel minimum editor extent when the window permits it and remain operable at narrow widths.
- Pointer capture, Escape cancellation, high-DPI fractional coordinates, and window resize during a drag are covered by interaction tests.

## Delivery record

- Owner: XDX-Org maintainers.
- Target: Phase 5 editor-groups milestone.
- Dependency: verified Phase 4 document/view state and session recovery.
- Principal risks: document disposal during transfers, divergent duplicate views, malformed recursive state, and WebView drag geometry.
- Ordered implementation: registry extraction; group/layout algorithms; rendering and commands; drag/drop and splitters; schema migration; interaction verification.

## Completion criteria

- Dragging and command-driven movement produce the same layout operation.
- Two views of one document update immediately while retaining independent selections and scrolling.
- Undo remains document-wide and coherent across duplicate views.
- Closing one copied view does not dispose its shared model or prompt to save unnecessarily.
- Layout restoration is deterministic and safely falls back to one group on invalid data.
- Pointer, keyboard, high-DPI, and narrow-window interaction tests cover splitters and drop zones.

## Progress

Started on `phase-5`:

- Phase contract, invariants, migration behavior, accessibility requirements, risks, and verification scope defined.
- Shared canonical-path document registry extracted from the single-strip tab service with reference-counted lifetime.
- Stable group and split identities, directional insertion, focus, bounded ratios/depth, equalization, and empty-branch normalization implemented.
- Unit coverage added for shared ownership and foundational layout operations.

Remaining:

- Add stable view identities and atomic move/copy operations over the registry-backed groups.
- Render nested groups, splitters, commands, focus navigation, and drop-zone previews.
- Migrate Phase 4 sessions to the version 2 layout schema.
- Add model, restoration, shared-editing, accessibility, and native interaction coverage.

## Next phase

Load real .NET solutions and give every C# document a Roslyn identity.
