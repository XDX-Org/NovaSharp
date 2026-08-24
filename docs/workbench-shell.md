# Workbench shell contract

## Region ownership

| Region | Semantic identity | Owner | Phase 4.5 behavior |
|---|---|---|---|
| Global command bar | `global-command` | Workbench shell | File, Workspace, and View command projections plus a compact overflow menu |
| Activity rail | `activity` | Workbench shell | Rightmost region with one stable Explorer entry; future views contribute entries through the shell boundary |
| Primary sidebar | `primary-sidebar` | Active workbench view | Docked right of the editor; Explorer stays mounted while hidden and retains width, tree state, selection, and focus |
| Editor area | `editor` | Document/editor system | Bounded split groups whose Monaco views share URI-keyed document models |
| Bottom panel | `bottom-panel` | Workbench shell | Collapsed host only; Problems, Output, Terminal, and Debug retain their governing phases |
| Status bar | `status` | Workbench shell | Ordered document/status items with text-equivalent accessible names |

Feature components contribute commands, views, and status items. They do not reposition these regions or inspect the
host operating system. Native window chrome remains owned by Photino.

## Command surfaces

- File, Workspace, and View menus project the shared registry. Opening one closes the previous menu, and either mouse
  button outside closes the open menu. No persistent palette control or shortcut hint is shown.
- The command palette lists the same descriptors and enablement state. `Ctrl/Cmd+Shift+P` and two consecutive Shift
  taps open it; Escape closes it and focus enters its search field when opened. Results remain vertically scrollable
  when the command list exceeds the palette height.
- Normalized registry shortcuts are dispatched throughout the workbench, including trees, dialogs, and status items.
  The dispatcher intercepts only modified registered bindings and never enters the unmodified typing path.
- Activity, tab-context, notification, and status buttons invoke registered command identifiers.
- Pointer tab context actions have palette and keyboard-command equivalents.
- Explorer item context menus are available by right click, the Context Menu key, and Shift+F10. Folder menus include
  New file and New folder; file menus omit creation actions. Move, Rename, and Delete are omitted for the workspace root.
  A left or right click outside an open Explorer or tab context menu closes it.
- `Change Editor Font…` is reachable from the palette. It offers the default monospace stack and packaged
  Fast Mono, then persists the allow-listed identifier in user settings.
- Editor-group commands are available in View and the palette. `Ctrl/Cmd+Alt+Right` splits right and
  `Ctrl/Cmd+Alt+Down` splits down; the remaining split, focus, move/copy, close-group, and distribute operations are
  palette commands. Holding Ctrl or Alt while dropping copies a view instead of moving it.

## Visual system

The graphite palette uses darker region surfaces, thin neutral dividers, and a restrained violet accent. Production
shell colour and shared sizing values come from the custom properties in `wwwroot/app.css`; components do not own a
second palette. Inter Variable is the packaged UI font. Fast Mono 5.002 is an optional source-editor font; the existing
platform-neutral monospace stack remains the default. Both are local assets with complete OFL notices. Codicons are
requested only through the typed semantic icon registry, and icons never carry meaning without an accessible name or
adjacent text.

Horizontal scrollbars are not part of the workbench visual system. Panels use clipping, truncation, wrapping, or
keyboard navigation; vertically scrollable content keeps its vertical scrollbar. The tab strip has no persistent
ellipsis control. Monaco hides its horizontal
scrollbar while retaining editor-owned long-line navigation and reveal behavior.

The generated Nova mark is temporary. Its source brief is retained in `assets/brand/README.md`, so replacing it does
not affect the icon registry or component contracts.

## Responsive and accessibility behavior

| State | Contract |
|---|---|
| Standard (`>= 900` CSS px) | Command menus, actions, editor, sidebar, activity, panel, and status remain visible |
| Compact (`720–899` CSS px) | Command menus collapse into the application menu; region order and shortcuts are retained |
| Narrow (`< 720` CSS px) | Explorer remains docked; the command bar and Explorer flex within the available viewport |
| Very narrow (`< 520` CSS px) | Explorer context actions retain labelled menu items; no action is clipped or unreachable |
| 200% zoom | The 640×480 host is tested as a 320×240 CSS-pixel viewport at device scale 2 |
| High DPI | Local raster/font/icon assets and focus indicators are tested at device scale 2 |
| High contrast | Forced system colours replace palette roles; active and selected states retain outlines |
| Reduced motion | Non-essential animation and smooth scrolling are disabled |

Focus order is command bar, tabs/editor, visible panel, visible sidebar, activity rail, then status bar. Hiding Explorer
remembers focus within its retained DOM; restoring Explorer returns focus to that element when it still exists. The
resize separator supports pointer drag plus Left and Right keys and exposes its value to assistive technology. The
separator has no product-defined minimum or maximum; Left and Right adjust its persisted pixel width.
Editor splitters expose orientation and a 10–90 percent value. Arrow keys adjust them by five percent; pointer drag is
animation-frame-coalesced and commits once on release. Edge, center, and tab insertion targets expose the same split,
move, and copy operations as the command registry.
Showing or resizing Explorer changes the width of the shared editor workspace, so the editor and bottom
panel move together and neither is covered.

## Fixtures and evidence

`tests/editor-host/editor-host.test.mjs` verifies that Fast Mono loads from the application origin, applies through
Monaco, and rejects arbitrary font identifiers. `tests/editor-host/workbench-shell.test.mjs` exercises Chromium and
WebKit at standard, narrow, high-DPI, forced-colour,
and 200%-zoom configurations. It compares engine baselines, checks reachable controls, Double Shift, global registry
bindings, frame-coalesced resize with one managed commit, focus restoration, 50 ms long-task limits, and 100-cycle
heap retention where the engine exposes it. Baselines are engine-specific and shared by every supported platform.
