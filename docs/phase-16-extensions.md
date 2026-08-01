# Phase 16: extension architecture

## Goal

Support useful third-party additions without granting implicit access to all application internals.

## Scope

- Versioned manifest and SDK for commands, menus, settings, language providers, diagnostics, and read-only workspace metadata.
- Discovery, enable/disable, compatibility checks, activation events, diagnostics, and uninstall.
- Declared permissions and workspace trust for file, process, network, debugger, and secret access.
- Isolation strategy with bounded activation, calls, memory, failure handling, and restart/disable behavior.
- One maintained sample extension and compatibility tests against the public SDK.

An online marketplace, arbitrary UI embedding, extension self-update, and full internal API exposure are not preview requirements.

## Design constraints

- Decide host isolation, trust, signing, permissions, distribution, and API compatibility before publishing the SDK.
- Default to no undeclared file, process, network, debugger, or secret access.
- Extension failures cannot crash the workbench, corrupt persisted state, or block startup; safe mode disables third-party activation.
- Public contracts are additive within a declared compatibility band and separate from internal service interfaces.
- Clearly label extensions as trusted/untrusted and show requested permissions before enablement.

## Completion criteria

- The sample extension installs locally and contributes a command, setting, and diagnostic provider using only public APIs.
- Incompatible, malformed, slow, crashing, over-memory, and permission-denied extensions fail in isolation.
- Disable/uninstall removes contributions and releases resources without restarting where the host permits.
- API compatibility, permissions, activation timing, safe mode, and malicious-input tests pass.
- SDK reference, manifest schema, sample, compatibility policy, and security model are published.

## Next phase

Package and qualify the complete preview product.
