# Phase 4.5: workbench shell and visual system

## Status

Complete.

## Goal

Give NovaSharp a coherent, accessible workbench shell and reusable visual language before editor groups and later
tool panels expand the interface.

## Scope

- Establish the application frame and the default placement of the global command bar, activity rail, primary sidebar,
  editor area, bottom panel, and status bar. Project global commands into the bar, palette, and shortcut registry.
- Keep Explorer in the collapsible, resizable primary sidebar introduced in phase 3 and provide a stable activity-rail
  entry for it.
- Define toolbar, menu, context-menu, command-palette, button, toggle, and overflow behavior over the shared command
  registry.
- Select, license, pin, and package one local icon set. Map semantic icon names to assets through a typed registry
  rather than embedding arbitrary glyphs in components.
- Introduce design tokens for colour roles, typography, spacing, sizing, borders, elevation, motion, focus, and disabled,
  hover, active, selected, warning, and error states.
- Define shell behavior for narrow windows, high-DPI displays, 200% zoom, reduced motion, high contrast, keyboard-only
  use, and screen readers.
- Replace provisional shell controls and glyphs from phases 1–4 with the shared components and tokens without changing
  their document or Explorer behavior.
- Add interaction and visual-regression fixtures for representative empty, Explorer, multi-tab, overflow, notification,
  and narrow-window states.

The default workbench structure is:

```text
Native window chrome
┌────────────────────────────────────────┬────────────────┬────┐
│ Global command bar                                           │
├────────────────────────────────────────┬────────────────┬────┤
│ Editor area                            │ Primary        │ A  │
│                                        │ sidebar        │ c  │
│                                        │                │ t  │
├────────────────────────────────────────┤ Explorer       │ i  │
│ Bottom panel host                      │                │ v  │
│ Problems / Output / Terminal / Debug   │                │ i  │
│                                        │                │ t  │
│                                        │                │ y  │
├────────────────────────────────────────┴────────────────┴────┤
│ Status bar                                                   │
└──────────────────────────────────────────────────────────────┘
```

The bottom panel host is a shell region only in this phase. Problems, Output, Terminal, and Debug content remain owned
by their existing phases. Arbitrary dockable panels, floating native windows, user-authored themes, and full layout
persistence remain deferred.

## Design constraints

- Shell regions have stable semantic identities and explicit ownership. Feature components contribute commands, views,
  and status items through bounded contracts; they do not reposition global chrome.
- The native title bar remains platform-owned unless a separate cross-platform host decision records and tests custom
  chrome on every supported runtime identifier.
- Monaco remains the only source editor and owns editor-local widgets. Shell controls must not duplicate completion,
  hover, signature, marker, selection, or editor accessibility UI.
- All icons and fonts are version-pinned, notice-complete, and packaged locally. Runtime CDNs, operating-system glyphs,
  emoji, and private icon APIs are not production dependencies.
- Icons are referenced by semantic names such as `file`, `folder-open`, `close`, and `warning`. Meaning is never conveyed
  by colour or an icon alone; accessible names and state remain available to assistive technology.
- Buttons and menus invoke the command registry so enablement, keybindings, menus, the palette, and accessibility expose
  the same command state.
- Design tokens are the only production source of shell colours and shared dimensions. Feature components must not
  introduce competing palettes or unexplained pixel constants.
- Pointer, keyboard, and assistive-technology paths produce the same operations. Focus order remains deterministic when
  regions hide, collapse, resize, or overflow.
- Resize and visibility events are frame-coalesced. Shell rendering publishes small immutable snapshots and must not
  synchronously read files, serialize editor text, or enter Monaco's typing path.
- Source editors and workbench panels do not expose horizontal scrollbars. Monaco retains its own long-line navigation;
  panels clip, truncate, wrap, or provide an explicit overflow control while preserving required vertical scrolling.
- Layout and interaction remain platform-neutral. Product code must not branch on host operating system, display scale,
  path convention, or shell availability.

## Deliverables

- A documented workbench information architecture and region ownership model.
- Reusable shell layout, activity, sidebar, panel, status, button, menu, and overflow components.
- A typed semantic icon registry backed by locally packaged assets and retained third-party notices.
- Shared CSS design tokens and component-state rules with no duplicate production theme path.
- Keyboard map and accessible-name rules for every shell control.
- Browser fixtures and screenshot baselines at standard, narrow, high-DPI, high-contrast, and 200% zoom configurations.
- Migration of the existing Explorer, tab strip, notifications, and file commands onto the shared shell primitives.

## Completion criteria

- Explorer has one stable activity entry and primary-sidebar location; hiding and restoring it preserves width, tree
  state, selection, and focus without reconstructing the tool.
- Explorer and the activity rail remain docked on the right at every viewport width. Showing or resizing Explorer
  resizes the editor and bottom-panel workspace instead of covering either region.
- Explorer has no product-defined minimum or maximum width. Its file operations appear in an item-specific context
  menu: folders expose creation actions, while files expose only actions applicable to that file. Left or right clicking
  outside an open context menu dismisses it.
- Every visible shell action resolves to a registered command or a documented view-state operation, with consistent
  enablement across buttons, menus, keybindings, and the command palette.
- Top-level menus are mutually exclusive and close on either left- or right-button pointer input outside the menu.
- One pinned icon system covers all shipped shell controls. Missing icons fail validation, notices ship with the app,
  and no control relies on emoji, colour, or an icon as its only label.
- The shell remains operable without clipped or unreachable controls at the minimum supported window size and at 200%
  zoom; overflow behavior is deterministic and keyboard accessible.
- Keyboard-only and screen-reader tests cover global commands, activity navigation, sidebar collapse/resize, tabs,
  notifications, panel visibility, and status items with visible focus and correct announcements.
- Visual-regression baselines cover every supported browser engine supplied by the native hosts without maintaining
  operating-system-specific layouts.
- Toggling and resizing shell regions while editing creates no browser long task over 50 ms, does not breach existing
  Monaco typing budgets, and shows no retained growth after 100 open/hide/resize cycles on each named CI fixture.
- Bootstrap, build, tests, RID publish, packaged native smoke, accessibility, visual, disposal, and retained-evidence
  gates pass on all six supported runtime identifiers from the same commit.

## Delivered implementation

- A graphite-grey shell now provides stable global-command, editor, bottom-panel, primary-sidebar, activity, and status
  regions. The restored command bar exposes File, Workspace, and View menus plus open and save controls without
  an active-file label, palette control, or visible shortcut hint. Explorer and the activity rail are docked on the right. Explorer remains mounted while hidden, restores
  its focus, and uses a frame-coalesced pointer and keyboard resize separator.
- Tab context actions, activity navigation, shortcuts, and the command palette project the shared command registry.
  Double Shift and `Ctrl/Cmd+Shift+P` continue to open the palette. The tab strip has no ellipsis overflow control.
- Inter Variable 5.3.0 and Codicons 0.0.46-24 are exact-lockfile, locally built assets with manifests and shipped
  licenses. A typed semantic registry validates every icon mapping. The temporary generated Nova mark is retained as
  a replaceable project asset.
- Fast Mono 5.002 is an optional, hash-pinned Monaco font with embedded and packaged OFL attribution. `Change Editor
  Font…` is available in the palette, persists the allow-listed choice through settings schema version 3,
  and updates normal and comparison editors through Monaco's public API; the prior monospace stack remains default.
- Shared design tokens cover colour, typography, spacing, dimensions, borders, elevation, focus, interaction states,
  reduced motion, increased contrast, and forced colours. Narrow layouts use deterministic command overflow and a
  naturally flexed docked Explorer without creating an operating-system-specific layout.
- Monaco normal/diff views and every current workbench panel hide horizontal scrollbars; vertical tree, palette, and
  editor scrolling remains available.
- Chromium and WebKit fixtures retain standard, narrow, high-DPI, high-contrast, and 200%-zoom baselines and exercise
  Double Shift, global shortcuts, focus restoration, one-commit resize, long-task, and 100-cycle retention gates.

The information architecture, command surfaces, visual rules, responsive states, and keyboard/accessibility contract
are recorded in [workbench-shell.md](workbench-shell.md) and [ADR 0004](decisions/0004-workbench-shell.md).

## Qualification status

[Qualification run 32752027806](https://github.com/XDX-Org/NovaSharp/actions/runs/32752027806) passed bootstrap,
build/test, browser accessibility and visual gates, RID-specific publish, packaged native smoke, performance,
disposal, and retained-evidence gates on all six supported runtime identifiers from commit `306e8db`.

## Known deferrals

- Phase 5 owns editor groups, split drop zones, and the editor layout tree.
- Phases 8, 10, 11, and 13 own Problems, Output, Terminal, and Debug content placed in the shell regions.
- Phase 14 owns user/workspace layout persistence, user-authored theme selection, configurable keybindings, recovery,
  and full-workbench accessibility hardening.
- Phase 16 owns extension contributions to commands, menus, icons, settings, and views.

## Next phase

Add editor groups and split views within the established editor region.
