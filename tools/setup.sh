#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)

force=
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force) force=--force ;;
        -h|--help)
            echo "Usage: ${BASH_SOURCE[0]} [--force]"
            exit 0
            ;;
        *) echo "Unknown argument: $1" >&2; exit 64 ;;
    esac
    shift
done

for command in dotnet curl jq unzip tar; do
    command -v "$command" >/dev/null || { echo "Required command is missing: $command" >&2; exit 1; }
done
sdk_version=$(dotnet --version)
[[ "$sdk_version" == 10.* ]] || { echo "The .NET 10 SDK is required; dotnet resolved to $sdk_version." >&2; exit 1; }
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) rid=linux-x64 ;;
    Linux-aarch64|Linux-arm64) rid=linux-arm64 ;;
    Darwin-x86_64) rid=osx-x64 ;;
    Darwin-arm64) rid=osx-arm64 ;;
    *) echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
esac

# The Node.js payload for Linux is .tar.xz, so tar alone is not enough. Fail here rather than after a large download.
case "$rid" in
    linux-*)
        if ! tar --help 2>/dev/null | grep -q -- --xz && ! command -v xz >/dev/null; then
            echo "XZ support is required to extract the Node.js archive for $rid. Install xz (for example xz-utils)." >&2
            exit 1
        fi
        ;;
esac

bash "$repo/tools/acquire-language-servers.sh" "$rid" ${force:+"$force"}

assets="$repo/src/NovaSharp/LanguageServers/Assets/$rid"
node="$assets/node/bin/node"
npm_cli="$assets/node/lib/node_modules/npm/bin/npm-cli.js"
cd "$repo"
"$node" "$npm_cli" ci --ignore-scripts --no-audit --no-fund
"$node" tools/build-monaco.mjs
"$node" tools/build-monaco.mjs --check
dotnet restore NovaSharp.slnx
dotnet build NovaSharp.slnx --no-restore
dotnet test NovaSharp.slnx --no-build
echo 'NovaSharp dependencies and local assets are ready.'
echo 'Run: dotnet run --project src/NovaSharp/NovaSharp.csproj --no-build'
