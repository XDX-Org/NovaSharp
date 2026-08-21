# Language-server and editor assets

NovaSharp pins and verifies every editor and language input. The bootstrap entry point for your shell — `tools/setup.sh` or
`tools/setup.ps1`, which are equivalent — acquires the correct assets for the current runtime identifier, builds Monaco, restores .NET
packages, and builds the solution. Neither entry point is the reference implementation; a change to one is incomplete until the other
matches.

## Pinned components

| Component | Version | Source | Purpose |
|---|---:|---|---|
| Monaco Editor | 0.56.0 | npm lockfile | Editor, C# / HTML / CSS language definitions, and editor worker |
| esbuild | 0.28.2 | npm lockfile | Build-time ESM bundler |
| Roslyn packages | 5.6.0 | NuGet | Workspaces, MSBuild integration, and C# features |
| C# extension payload | 2.140.9 | Microsoft Visual Studio Marketplace | Roslyn C# language server and matching Razor cohost |
| Razor cohost | 10.0.0-preview.26262.2 | Inside the pinned C# payload | Project-aware Razor/Blazor language features |
| HTML language service | 5.6.2 | npm lockfile | HTML completion, hover, formatting, symbols, folding, and selection ranges |
| CSS language service | 6.3.10 | npm lockfile | CSS completion, hover, diagnostics, formatting, symbols, folding, and selections |
| TypeScript Language Server | 5.3.0 | npm lockfile | JavaScript/TypeScript protocol host acquired for future use |
| TypeScript | 6.0.3 | npm lockfile | JavaScript/TypeScript language engine acquired for future use |
| VS Code LSP library | 10.1.0 | npm lockfile | HTML/CSS protocol host |
| Node.js | 24.19.0 | nodejs.org archive | Pinned build and language-server runtime |

JavaScript/TypeScript features remain outside the preview scope even though their pinned server is acquired with the shared web-language
payload. Acquisition does not imply feature completion.

## Acquisition and verification

`src/NovaSharp/LanguageServers/assets.json` records C#/Razor and Node versions plus a SHA-256 per runtime identifier. Rows are listed
alphabetically; the ordering carries no priority.

| Runtime identifier | Roslyn/Razor payload | Node.js archive | Pinned |
|---|---|---|---|
| `linux-arm64` | `linux-arm64` | `.tar.xz` | Yes |
| `linux-x64` | `linux-x64` | `.tar.xz` | Yes |
| `osx-arm64` | `darwin-arm64` | `.tar.gz` | Yes |
| `osx-x64` | `darwin-x64` | `.tar.gz` | Yes |
| `win-arm64` | — | — | No; see the [supported platform matrix](delivery-plan.md#supported-platform-matrix) |
| `win-x64` | `win32-x64` | `.zip` | Yes |

Adding a runtime identifier means adding its payload hash, its Node hash, its extraction path, and its notices in one change. Extraction
formats differ per platform, so a platform whose archive format is not handled by both bootstrap entry points is not pinned.

Both acquisition entry points download to a temporary directory, verify hashes before extraction, install npm dependencies with
`npm ci --ignore-scripts`, and replace the local asset directory only after every step succeeds. A source-manifest stamp makes normal
bootstrap runs idempotent. Passing the force flag for your shell ignores the stamp and reacquires everything.

Each entry point must verify its platform's bootstrap prerequisites before the first download, so a missing archive tool fails
immediately rather than after a large transfer. The required tools differ per platform because the upstream archive formats differ; each
platform's list is in the root [README](../README.md#supported-platforms).

Root `package-lock.json` fixes Monaco, esbuild, and transitive dependencies. The web-language `package-lock.json` independently fixes all
LSP packages. `tools/build-monaco.mjs` bundles supported Monaco ESM entry points and its worker, packages runtime license texts, and records
SHA-256 hashes for every emitted file. Its `--check` mode rebuilds into a temporary directory and compares the result with the manifest.

Roslyn workspace/features packages are ordinary `PackageReference` entries and are restored by `dotnet restore`. The separately acquired
Microsoft C# payload supplies the executable Roslyn language server and its version-matched Razor cohost. NovaSharp therefore currently
carries both an in-process and an out-of-process route to the same semantic engine. That is unresolved, not a design: see open decision 3
in the [delivery plan](delivery-plan.md#open-decisions). Whichever route is chosen, the other's dependencies come out.

## Runtime policy

Generated assets live under:

```text
src/NovaSharp/wwwroot/monaco/
src/NovaSharp/LanguageServers/Assets/<RID>/
```

They are ignored by Git and must not be downloaded by the running IDE. Missing Monaco assets fail the build with a bootstrap instruction
rather than silently using a CDN or main-thread fallback.

Language-server assets are runtime-identifier specific, so publishing requires an explicit runtime identifier:

```bash
dotnet publish src/NovaSharp/NovaSharp.csproj -r <runtime-identifier> -c Release
```

A publish without one produces an application with no Roslyn, Razor, Node, or web-language payload. That configuration is a packaging
error, not a supported build, and the build must fail rather than emit it.

The development-time asset root recorded in the project file exists for the inner loop only. It must not appear in a Release build: a
shipped assembly may not carry a build-machine directory path.

Update a dependency only by changing its version, lockfile/manifest hashes, notices, and verification evidence together, for every
runtime identifier at once. Never disable integrity checks to work around an upstream change, and never pin a newer payload for one
platform than another.

`assets.json` records `resolvedUtc`, the date the current set was reviewed. Re-review the pinned set at least once per phase, and always
before a release qualification, so pins age deliberately rather than by neglect.
