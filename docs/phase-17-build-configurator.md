# Phase 17: build configurator

## Goal

Provide one project-aware place to configure build and run behavior without crowding the main toolbar.

## Scope

- Add a Build Configurator button to the top toolbar that opens a dedicated dialog or tool view.
- Select startup project, configuration, target framework, and a validated `launchSettings.json` profile.
- Configure application arguments, working directory, and non-secret environment variables.
- Discover valid frameworks, configurations, and profiles from the evaluated project instead of requiring free-text names.
- Preview the effective build/run command with secret values redacted.
- Persist workspace selections separately from project files unless the user explicitly chooses to edit project configuration.

## Design constraints

- Reuse the phase 10 process service and argument-array boundary.
- Never write project or launch-settings files without an explicit apply action and conflict-safe validation.
- Keep secrets out of workspace settings and logs; integrate with platform credential storage if secret persistence is offered.
- Explain invalid or unavailable combinations and retain the last valid configuration.
- The compact toolbar shows only the active configuration summary and the button that opens the configurator.

## Completion criteria

- Multi-target fixture projects expose only evaluated target frameworks and valid configuration combinations.
- Valid launch profiles round-trip without losing unknown properties or formatting-sensitive content.
- Missing SDKs, malformed profiles, stale project evaluation, conflicting file edits, and invalid working directories fail recoverably.
- Build and run use the exact effective settings shown by the configurator.
- Keyboard navigation, screen-reader labels, persistence, redaction, and cross-platform path behavior pass.

## Next phase

Package and qualify the complete preview product.
