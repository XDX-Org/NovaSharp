# Language-server and editor assets

NovaSharp pins and verifies every editor/language input. `tools/setup.ps1` and `tools/setup.sh` acquire the correct assets for the current
platform, build Monaco, restore .NET packages, and build the solution.

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

`src/NovaSharp/LanguageServers/assets.json` records C#/Razor and Node versions plus a SHA-256 for every supported runtime identifier:

- `win-x64`
- `linux-x64`
- `linux-arm64`
- `osx-x64`
- `osx-arm64`

The acquisition scripts download to a temporary directory, verify hashes before extraction, install npm dependencies with
`npm ci --ignore-scripts`, and replace the local asset directory only after every step succeeds. A source-manifest stamp makes normal
bootstrap runs idempotent. `--force` / `-ForceAssets` ignores the stamp and reacquires everything.

Root `package-lock.json` fixes Monaco, esbuild, and transitive dependencies. The web-language `package-lock.json` independently fixes all
LSP packages. `tools/build-monaco.mjs` bundles supported Monaco ESM entry points and its worker, packages runtime license texts, and records
SHA-256 hashes for every emitted file. Its `--check` mode rebuilds into a temporary directory and compares the result with the manifest.

Roslyn workspace/features packages are ordinary `PackageReference` entries and are restored by `dotnet restore`. The separately acquired
Microsoft C# payload supplies the executable Roslyn language server and its version-matched Razor cohost.

## Runtime policy

Generated assets live under:

```text
src/NovaSharp/wwwroot/monaco/
src/NovaSharp/LanguageServers/Assets/<RID>/
```

They are ignored by Git and must not be downloaded by the running IDE. Published builds include only the selected RID's language-server
assets. Missing Monaco assets fail the build with a bootstrap instruction rather than silently using a CDN or main-thread fallback.

Update a dependency only by changing its version, lockfile/manifest hashes, notices, and verification evidence together. Never disable
integrity checks to work around an upstream change.
