# 0004: Stable workbench shell and semantic visual system

## Status

Accepted.

## Decision

Use one platform-neutral workbench frame with stable global-command, editor, bottom-panel, primary-sidebar, activity,
and status regions. The native host continues to own window chrome. A persistent command bar projects registered
File, Workspace, and View commands without a visible command-palette control. The same
global commands remain available through the command palette and registered shortcuts. The primary sidebar and activity rail are docked
on the right. Showing or resizing the sidebar reduces the editor workspace, including its bottom panel, at every
viewport width; it never overlays those regions.

Every visible action invokes the shared command registry or an explicitly local view-state operation. A browser-side
keybinding dispatcher receives normalized registry descriptors so shortcuts also work when focus is outside Monaco;
it has no command catalogue of its own. Double Shift invokes the registered command-palette command. Modified-key
shortcuts are intercepted before Monaco, avoiding duplicate invocation while leaving ordinary typing entirely local.

Use CSS custom properties as the single source of shell colours and shared dimensions. Package Inter Variable 5.3.0
and Visual Studio Code Codicons 0.0.46-24 locally from exact npm lockfile entries. Package the user-supplied Fast Mono
5.002 face as a hash-pinned, OFL-noticed optional Monaco font; the existing monospace stack remains the default.
Components request Codicons through the typed `SemanticIcon` registry; missing mappings fail validation. The temporary
NovaSharp mark is a replaceable, project-owned bitmap and is not part of the semantic icon set.

The command palette exposes `Change Editor Font…`. It writes the allow-listed
`editorFont` preference through the configuration service, and the editor host applies it with Monaco's public
`updateOptions` API to the main editor and active diff view. This narrow built-in choice does not introduce a second
editor, an arbitrary CSS font setting, or phase 14's general settings UI.

The shell exposes reduced-motion, increased-contrast, and forced-colour behavior. Resize work is frame-coalesced in
the browser and commits one persisted width after a pointer gesture. Hidden tools remain mounted, retain service and
DOM state, and restore their previous focus when shown.

Workbench panels never expose horizontal scrollbars. Panel content clips, truncates, wraps, or uses keyboard navigation;
vertical scrolling remains available where the content requires it. The tab strip has no persistent overflow button.

## Consequences

- Later phases contribute commands, views, and status items without moving the stable global command region.
- Narrow layouts retain the same docked region order. Explorer has no product-defined minimum or maximum width;
  normal flex sizing keeps the regions within the available viewport.
- Monaco remains the only editor and continues to own editor-local UI and accessibility.
- Icon and font assets, their hashes/manifests, and complete licenses become bootstrap and publish inputs.
- Built-in light themes, user themes, configurable layouts, and arbitrary docking remain phase-14 work.
- Visual baselines are engine-specific but not operating-system-specific; all supported runtime identifiers run the
  same Chromium/WebKit shell fixtures.
