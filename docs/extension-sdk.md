# Extension SDK 1.0 preview

Extensions ship as a directory containing `extension.json`, the declared entry assembly, and its private dependencies. The manifest schema is represented by `samples/HelloExtension/extension.json`; unknown manifest versions, traversal entry points, duplicate contributions, undeclared permissions, and incompatible API versions are rejected.

Implement `NovaSharp.Extensions.INovaSharpExtension` from `NovaSharp.ExtensionSdk`. The public context exposes commands, typed declared settings, diagnostic publication, and read-only workspace metadata. It does not expose workbench internals, arbitrary UI, files, processes, network, debugging, or secrets. Those capabilities require both a manifest permission and explicit user/workspace-trust approval, and are not part of SDK 1.0.

API 1.x changes are additive. Removing or changing a public member requires a new major API version. Extensions select an API version in the manifest and are disabled with a diagnostic when the host cannot satisfy it.

Activation is isolated by the host boundary, limited to five seconds, and failure-redacted. Safe mode disables third-party activation. Disable/uninstall invokes deactivation, removes registered contributions, and releases the host without restarting the workbench.
