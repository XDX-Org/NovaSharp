# Phase 3: workspace explorer

## Implementation status

Implementation complete; verification in progress. Folder open/close and restore, lazy virtualized expansion, configurable/default ignores, create/rename/delete/move operations, active-file reveal, targeted watcher refresh, overflow rescan, keyboard/context interaction, symlink leaves, and versioned sidebar/expansion persistence are implemented.

The Release build and 27 tests pass locally. The generated 20,000-file fixture completes its full lazy enumeration within the five-second budget on an AMD Ryzen 7 5800X3D with NVMe storage. The Phase 3 workflow builds, tests, and publishes all four supported targets and runs the packaged Explorer interaction smoke under Linux/Xvfb. Phase status remains in progress until that matrix passes and Windows/macOS native-host checks are recorded.

## Verification budget

| Scenario | Fixture hardware | Budget |
|---|---|---:|
| Enumerate 20 directories containing 1,000 files each | AMD Ryzen 7 5800X3D, NVMe, Linux x64 | 5 seconds |
| Packaged Explorer keyboard/context/rename smoke | GitHub `ubuntu-24.04`, Xvfb | 60 seconds |

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
| Surface with no contextual action | No custom menu |

Menus must remain fully inside the app viewport, close on outside click or `Escape`, preserve the current selection, expose menu semantics to assistive technology, and have keyboard equivalents. Destructive actions and name entry open centered modal dialogs rather than expanding the menu or Explorer inline.

## Completion criteria

- A workspace containing at least 20,000 entries remains responsive while expanding nodes.
- Keyboard users can traverse, expand, collapse, activate, rename, and invoke context actions.
- Context menus show only actions relevant to their target, remain visible at viewport edges, and support dismissal and keyboard equivalents.
- External create/rename/delete events update affected branches without rebuilding the whole tree.
- Renaming an open dirty file retains its text, dirty state, and selection.
- Invalid paths, permissions, symlink loops, and watcher overflow produce recoverable UI errors.

## Next phase

Generalize the single document into a registry and add movable document tabs.
