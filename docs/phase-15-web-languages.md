# Phase 15: Razor, HTML, and CSS

## Goal

Make Razor/Blazor, HTML, and CSS projects first-class editing targets rather than merely loadable projects.

## Scope

- Syntax highlighting, completion, hover, diagnostics, formatting, symbols, navigation, and rename where the selected service supports them.
- Razor host/projected-document mapping for C#, HTML, and CSS regions with versioned range translation.
- Project-aware Razor/Blazor configuration, generated document visibility, and component/tag-helper completion.
- Standalone HTML and CSS documents through the same internal provider contracts.
- Clear degraded states when a language service is unavailable or a project is still loading.
- Monaco language IDs/configuration and provider registration for each supported document/projection.

JavaScript/TypeScript language services and browser debugging are not preview requirements.

## Design constraints

- Use the protocol-based, lockfile-pinned Razor/HTML/CSS services acquired by the repository bootstrap; lifecycle, redistribution,
  versions, hashes, and licenses follow [language-server assets](language-server-assets.md).
- Host-to-projection edits, diagnostics, selections, and navigation must round-trip through one tested mapping service.
- Never apply stale projected edits to a newer host document.
- Bound language-service processes, caches, requests, and restart loops.
- Keep feature availability capability-driven rather than claiming unsupported parity across all languages.
- Use Monaco's packaged HTML/CSS services when their public capabilities satisfy the requirement; otherwise use the selected external service. Razor remains project-aware. Exactly one provider owns each language/feature combination.
- Monaco owns lexical/semantic token painting and editor-local completion/hover/signature UI. NovaSharp does not create Blazor color or popup overlays.
- Run protocol I/O, projection updates, and service restarts asynchronously with bounded per-service requests. Prioritize visible host regions, cancel stale projections, and serialize host-document mapping mutations.

## Completion criteria

- Representative Razor Pages, Blazor Web App, component library, HTML, and CSS fixtures receive documented language features.
- Mixed-language edits, directives, embedded expressions, generated files, rename, formatting, and stale mapping cases are tested.
- Service crash/restart and project reload preserve editor text and recover without application restart.
- Diagnostic and navigation ranges map to the correct host text after rapid edits.
- Numeric first-result, projection-update, service-restart, and retained-memory budgets pass.
- Concurrent C#/HTML/CSS results after rapid mixed-language edits cannot cross versions, duplicate providers, or degrade Monaco typing.

## Next phase

Expose a deliberately small, stable subset of proven workbench capabilities to extensions.
