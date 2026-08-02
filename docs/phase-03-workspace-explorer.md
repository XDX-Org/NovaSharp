# Phase 3: workspace explorer

## Implementation status

Implementation complete; verification in progress. Folder open/close and restore, lazy virtualized expansion, configurable/default ignores, create/rename/delete/move operations, active-file reveal, targeted watcher refresh, overflow rescan, keyboard/context interaction, symlink leaves, and versioned sidebar/expansion persistence are implemented.

The Release build and 28 tests pass locally. The generated 20,000-file fixture completes its full lazy enumeration within the five-second budget on an AMD Ryzen 7 5800X3D with NVMe storage. The Phase 3 workflow builds, tests, publishes, and launches the packaged Explorer interaction smoke on all four supported targets; Linux uses Xvfb. Phase status remains in progress until that matrix passes.

## Verification budget

| Scenario | Fixture hardware | Budget |
|---|---|---:|
| Enumerate 20 directories containing 1,000 files each | AMD Ryzen 7 5800X3D, NVMe, Linux x64 | 5 seconds |
| Packaged Explorer keyboard/context/rename smoke | Four supported GitHub runner images; Linux uses Xvfb | 60 seconds |

## Goal

Open a folder and navigate its contents through a resizable Explorer panel.

## Scope

- Open/close one workspace folder and show its canonical path.
- Resizable, collapsible right sidebar with an accessible hierarchical tree and right-edge activity-bar toggle.
- Lazy directory expansion, refresh, reveal active file, and persisted expansion state.
- Create, rename, delete, and move files/folders with confirmation and error recovery.
- Open files by mouse or keyboard; distinguish folders, supported files, unknown files, and symlinks.
- Ignore configurable paths plus `.git`, `bin`, and `obj` by default without hiding explicitly opened files.
- Filesystem watcher updates that preserve selection and expansion where possible.
- Introduce versioned persistence for workspace identity, expansion, sidebar, and restore state.

Solution/project semantics and source-control decorations are deferred. The tree represents the filesystem in this phase.

## Design constraints

- A tool window stays alive when hidden, rather than losing its state. This matches the established role of Explorer-like tool windows ([Visual Studio tool windows](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/tool-window/tool-window?view=visualstudio)).
- Tree nodes use stable canonical-path IDs. Expansion loads children on demand and enumeration runs off the UI thread.
- Detect symlink cycles and do not traverse outside the workspace through links unless explicitly allowed.
- File operations update the document registry atomically so an open renamed file keeps its buffer.
- Use virtualization or incremental rendering for large directories.

## Context menus

Phase 3 introduces the app-wide context-menu host. The menu is selected from the surface under the pointer rather than using one global list:

| Surface | Actions |
|---|---|
| Explorer folder or workspace root | New file, new folder, rename, move, delete, close workspace; invalid actions are disabled |
| Explorer file | New file/folder in its parent, rename, move, delete, close workspace |
| Editable editor or text field | Undo, cut, copy, paste, delete, select all; selection and read-only state control enablement |
| Selected read-only text | Copy |
| Surface with no contextual action | Browser-options shortcut hint |

Menus must remain fully inside the app viewport, close on outside click or `Escape`, preserve the current selection, expose menu semantics to assistive technology, and have keyboard equivalents. `Shift`+right-click bypasses custom handling and opens the native browser menu. Destructive actions and name entry open centered modal dialogs rather than expanding the menu or Explorer inline.

## Manual native verification checklist

Use this checklist as a release spot-check; the same core interactions run automatically in the packaged four-platform smoke:

- Launch the published application and open a workspace folder.
- Expand/collapse and activate tree items with the keyboard.
- Rename a dirty open file and confirm its text, dirty marker, and selection remain unchanged.
- Open a custom menu at each viewport edge and dismiss it with `Escape`.
- Use `Shift`+right-click and confirm the native browser menu opens without a custom menu.
- Close and reopen NovaSharp; confirm workspace, expansion, sidebar visibility, and width restore.

## Completion criteria

- A workspace containing at least 20,000 entries remains responsive while expanding nodes.
- Keyboard users can traverse, expand, collapse, activate, rename, and invoke context actions.
- Context menus show only actions relevant to their target, remain visible at viewport edges, and support dismissal and keyboard equivalents.
- External create/rename/delete events update affected branches without rebuilding the whole tree.
- Renaming an open dirty file retains its text, dirty state, and selection.
- Invalid paths, permissions, symlink loops, and watcher overflow produce recoverable UI errors.

## Next phase

Generalize the single document into a registry and add movable document tabs.
