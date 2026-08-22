# Phase 4.5: workbench shell and visual system

## Status

Planned.

## Goal

Give NovaSharp a coherent, accessible workbench shell and reusable visual language before editor groups and later
tool panels expand the interface.

## Scope

- Establish the application frame and the default placement of global commands, the activity rail, primary sidebar,
  editor area, bottom panel, and status bar.
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
┌──────────────────────────────────────────────────────────────┐
│ Global command bar                                           │
├────┬────────────────┬────────────────────────────────────────┤
│ A  │ Primary        │ Editor area                            │
│ c  │ sidebar        │                                        │
│ t  │                │                                        │
│ i  │ Explorer       ├────────────────────────────────────────┤
│ v  │                │ Bottom panel host                      │
│ i  │                │ Problems / Output / Terminal / Debug   │
│ t  │                │                                        │
│ y  │                │                                        │
├────┴────────────────┴────────────────────────────────────────┤
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
- Layout and interaction remain platform-neutral. Product code must not branch on host operating system, display scale,
  path convention, or shell availability.

## Deliverables

- A documented workbench information architecture and region ownership model.
- Reusable shell layout, command-bar, activity, sidebar, panel, status, button, menu, and overflow components.
- A typed semantic icon registry backed by locally packaged assets and retained third-party notices.
- Shared CSS design tokens and component-state rules with no duplicate production theme path.
- Keyboard map and accessible-name rules for every shell control.
- Browser fixtures and screenshot baselines at standard, narrow, high-DPI, high-contrast, and 200% zoom configurations.
- Migration of the existing Explorer, tab strip, notifications, and file commands onto the shared shell primitives.

## Completion criteria

- Explorer has one stable activity entry and primary-sidebar location; hiding and restoring it preserves width, tree
  state, selection, and focus without reconstructing the tool.
- Every visible shell action resolves to a registered command or a documented view-state operation, with consistent
  enablement across buttons, menus, keybindings, and the command palette.
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

## Known deferrals

- Phase 5 owns editor groups, split drop zones, and the editor layout tree.
- Phases 8, 10, 11, and 13 own Problems, Output, Terminal, and Debug content placed in the shell regions.
- Phase 14 owns user/workspace layout persistence, user-authored theme selection, configurable keybindings, recovery,
  and full-workbench accessibility hardening.
- Phase 16 owns extension contributions to commands, menus, icons, settings, and views.

## Next phase

Add editor groups and split views within the established editor region.
