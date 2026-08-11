# Language-server assets

NovaSharp pins and verifies all language-server inputs in
`src/NovaSharp/LanguageServers/assets.json`. Versions were resolved from stable upstream releases on 2026-08-09.

| Component | Version | Source | License |
|---|---:|---|---|
| Roslyn language server and Razor cohost | C# extension 2.140.9 | Microsoft C# Marketplace platform package | MIT |
| Razor cohost payload | 10.0.0-preview.26262.2 | Version-matched payload inside C# 2.140.9 | MIT |
| HTML language service | 5.6.2 | npm | MIT |
| CSS language service | 6.3.10 | npm | MIT |
| TypeScript Language Server | 5.3.0 | npm | Apache-2.0 |
| TypeScript | 6.0.3 | npm | Apache-2.0 |
| VS Code LSP library | 10.1.0 | npm | MIT |
| Node.js | 24.19.0 LTS | nodejs.org | MIT and bundled third-party licenses |

The Razor assembly has a preview-shaped internal build version, but is the payload shipped by Microsoft's stable
C# 2.140.9 release. It is never independently upgraded because the cohost must match its Roslyn server.

## Acquisition

Run from the repository root:

```sh
tools/acquire-language-servers.sh linux-x64
```

Supported release RIDs are `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`. The tool:

1. Downloads the exact platform package and Node archive.
2. Rejects either artifact unless its SHA-256 matches the manifest.
3. Extracts the Roslyn server, version-matched Razor extension, and notices.
4. Runs `npm ci --omit=dev --ignore-scripts` using the pinned bundled Node runtime.

Generated assets are ignored by Git. They must be acquired before publishing. `dotnet publish -r <RID>` includes only
the matching RID directory. NovaSharp never downloads language-server assets at runtime.

The web host in `LanguageServers/Web/server.cjs` only exposes the official HTML/CSS language-service packages over
LSP. JavaScript and TypeScript use the packaged TypeScript Language Server and `tsserver`. Remote HTML custom-data
fetching is not enabled.
