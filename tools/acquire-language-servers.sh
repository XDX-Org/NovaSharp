#!/usr/bin/env bash
set -euo pipefail

rid=${1:?usage: acquire-language-servers.sh RID [OUTPUT]}
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
output=${2:-"$repo/src/NovaSharp/LanguageServers/Assets/$rid"}
manifest="$repo/src/NovaSharp/LanguageServers/assets.json"
work=$(mktemp -d)
trap 'find "$work" -type f -delete; find "$work" -depth -type d -empty -delete' EXIT
hash_file() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

platform=$(jq -r ".roslynRazor.artifacts[\"$rid\"].platform // empty" "$manifest")
expected=$(jq -r ".roslynRazor.artifacts[\"$rid\"].sha256 // empty" "$manifest")
[[ -n "$platform" && -n "$expected" ]] || { echo "Unsupported RID: $rid" >&2; exit 2; }
version=$(jq -r .roslynRazor.version "$manifest")
vsix="$work/csharp.vsix"
curl --compressed -fsSL "https://marketplace.visualstudio.com/_apis/public/gallery/publishers/ms-dotnettools/vsextensions/csharp/$version/vspackage?targetPlatform=$platform" -o "$vsix"
actual=$(hash_file "$vsix")
[[ "$actual" == "$expected" ]] || { echo "Roslyn/Razor hash mismatch" >&2; exit 3; }
mkdir -p "$output/roslyn" "$output/razor" "$output/licenses"
unzip -q "$vsix" 'extension/.roslyn/*' 'extension/.razorExtension/*' 'extension/LICENSE.txt' 'extension/ThirdPartyNotices.txt' -d "$work/vsix"
cp -R "$work/vsix/extension/.roslyn/." "$output/roslyn/"
cp -R "$work/vsix/extension/.razorExtension/." "$output/razor/"
cp "$work/vsix/extension/LICENSE.txt" "$output/licenses/csharp-MIT.txt"
cp "$work/vsix/extension/ThirdPartyNotices.txt" "$output/licenses/csharp-ThirdPartyNotices.txt"

node_version=$(jq -r .node.version "$manifest")
node_expected=$(jq -r ".node.sha256[\"$rid\"]" "$manifest")
case "$rid" in
  linux-x64) node_platform=linux-x64; archive="node-v$node_version-$node_platform.tar.xz" ;;
  osx-x64) node_platform=darwin-x64; archive="node-v$node_version-$node_platform.tar.gz" ;;
  osx-arm64) node_platform=darwin-arm64; archive="node-v$node_version-$node_platform.tar.gz" ;;
  win-x64) node_platform=win-x64; archive="node-v$node_version-$node_platform.zip" ;;
esac
curl -fsSL "https://nodejs.org/dist/v$node_version/$archive" -o "$work/$archive"
node_actual=$(hash_file "$work/$archive")
[[ "$node_actual" == "$node_expected" ]] || { echo "Node hash mismatch" >&2; exit 4; }
mkdir -p "$output/node"
mkdir -p "$work/node"
case "$archive" in
  *.zip) unzip -q "$work/$archive" -d "$work/node" ;;
  *.tar.xz) tar -xJf "$work/$archive" -C "$work/node" ;;
  *.tar.gz) tar -xzf "$work/$archive" -C "$work/node" ;;
esac
cp -R "$work/node/node-v$node_version-$node_platform/." "$output/node/"

web="$repo/src/NovaSharp/LanguageServers/Web"
cp "$web/server.cjs" "$web/package.json" "$web/package-lock.json" "$output/"
if [[ "$rid" == win-x64 ]]; then
  (cd "$output" && "$output/node/npm.cmd" ci --omit=dev --ignore-scripts --no-audit --no-fund)
else
  (cd "$output" && PATH="$output/node/bin:$PATH" npm ci --omit=dev --ignore-scripts --no-audit --no-fund)
fi
cp "$output/node/LICENSE" "$output/licenses/node-MIT.txt"
echo "Acquired and verified language servers for $rid in $output"
