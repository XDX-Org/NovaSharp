# Phase 18: preview release

## Goal

Ship an installable, supportable preview whose platform claims are backed by reproducible release evidence.

## Scope

- Reproducible CI builds and platform-native packages for the supported matrix.
- Application identity, semantic versioning, release channels, atomic update/rollback, and persisted-state migration policy.
- Code signing/notarization, checksums, provenance, dependency/license inventory, and vulnerability response.
- Clean install, upgrade, downgrade/rollback, repair where applicable, and uninstall behavior.
- Opt-in crash reporting/telemetry policy, privacy documentation, support route, known limitations, and release notes.

## Design constraints

- Release artifacts come only from protected CI inputs and are traceable to source and dependency locks.
- Updates verify signatures before changing installed files and retain a recoverable previous version.
- Uninstall distinguishes application files from user settings, workspaces, and recovery data.
- No release gate depends only on a manual claim; retain logs or test results for each platform.
- Preview status and unsupported workflows remain visible in product and documentation.

## Completion criteria

- Signed packages install, launch, update, roll back, and uninstall on clean supported-OS images.
- The complete edit/build/run/debug web-project smoke journey passes on every supported platform.
- Crash recovery, safe mode, schema migration, offline startup, revoked/invalid update signature, and disk-full cases pass.
- No release-blocking security, license, accessibility, data-loss, or process-leak issue remains open.
- Performance/reliability budgets, SBOM/provenance, support policy, privacy notice, known limitations, and release notes are published.

## Next phase

Use preview evidence and user feedback to prioritize post-preview work; do not imply stable status until a separate stable-release gate is defined.
