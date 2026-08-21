#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
asset_root="$repo/src/NovaSharp/LanguageServers/Assets"
manifest="$repo/src/NovaSharp/LanguageServers/assets.json"
rid=
force=
while [[ $# -gt 0 ]]; do
    case "$1" in
        --force) force=--force ;;
        -*) echo "Unknown argument: $1" >&2; exit 64 ;;
        *)
            [[ -z "$rid" ]] || { echo "Runtime identifier already given as $rid" >&2; exit 64; }
            rid=$1
            ;;
    esac
    shift
done

if [[ -z "$rid" ]]; then
    case "$(uname -s)-$(uname -m)" in
        Linux-x86_64) rid=linux-x64 ;;
        Linux-aarch64|Linux-arm64) rid=linux-arm64 ;;
        Darwin-x86_64) rid=osx-x64 ;;
        Darwin-arm64) rid=osx-arm64 ;;
        *) echo "Unsupported platform: $(uname -s)-$(uname -m)" >&2; exit 2 ;;
    esac
fi

hash_file() {
    if command -v sha256sum >/dev/null; then
        sha256sum "$1" | cut -d' ' -f1
    else
        shasum -a 256 "$1" | cut -d' ' -f1
    fi
}

output="$asset_root/$rid"
manifest_hash=$(hash_file "$manifest")
stamp="$output/.source-manifest.sha256"
node_executable="$output/node/bin/node"
[[ "$rid" == win-* ]] && node_executable="$output/node/node.exe"
if [[ "$force" != "--force" && -f "$stamp" && "$(tr -d '\r\n' < "$stamp")" == "$manifest_hash" &&
      -f "$output/roslyn/Microsoft.CodeAnalysis.LanguageServer.dll" &&
      -f "$output/razor/Microsoft.VisualStudioCode.RazorExtension.dll" && -f "$node_executable" &&
      -f "$output/node_modules/vscode-html-languageservice/package.json" &&
      -f "$output/node_modules/vscode-css-languageservice/package.json" &&
      -f "$output/node_modules/typescript-language-server/package.json" && -f "$output/server.cjs" ]]; then
    echo "Language-server assets for $rid already match the pinned manifest."
    exit 0
fi

platform=$(jq -r ".roslynRazor.artifacts[\"$rid\"].platform // empty" "$manifest")
expected=$(jq -r ".roslynRazor.artifacts[\"$rid\"].sha256 // empty" "$manifest")
[[ -n "$platform" && -n "$expected" ]] || { echo "Unsupported RID: $rid" >&2; exit 2; }

work=$(mktemp -d)
# npm on macOS validates the staged root identity against the lockfile.
stage="$work/novasharp-web-language-servers"
mkdir -p "$stage"
cleanup() {
    [[ -n "${work:-}" && -d "$work" ]] && rm -rf "$work"
}
trap cleanup EXIT

version=$(jq -r .roslynRazor.version "$manifest")
vsix="$work/csharp.vsix"
gallery=https://marketplace.visualstudio.com/_apis/public/gallery
curl --compressed -fsSL \
    "$gallery/publishers/ms-dotnettools/vsextensions/csharp/$version/vspackage?targetPlatform=$platform" \
    -o "$vsix"
actual=$(hash_file "$vsix")
[[ "$actual" == "$expected" ]] || { echo "Roslyn/Razor hash mismatch: expected $expected, received $actual" >&2; exit 3; }
mkdir -p "$stage/roslyn" "$stage/razor" "$stage/licenses"
unzip -q "$vsix" 'extension/.roslyn/*' 'extension/.razorExtension/*' \
    'extension/LICENSE.txt' 'extension/ThirdPartyNotices.txt' -d "$work/vsix"
cp -R "$work/vsix/extension/.roslyn/." "$stage/roslyn/"
cp -R "$work/vsix/extension/.razorExtension/." "$stage/razor/"
cp "$work/vsix/extension/LICENSE.txt" "$stage/licenses/csharp-MIT.txt"
cp "$work/vsix/extension/ThirdPartyNotices.txt" "$stage/licenses/csharp-ThirdPartyNotices.txt"

node_version=$(jq -r .node.version "$manifest")
node_expected=$(jq -r ".node.sha256[\"$rid\"]" "$manifest")
case "$rid" in
    linux-x64) node_platform=linux-x64; archive="node-v$node_version-$node_platform.tar.xz" ;;
    linux-arm64) node_platform=linux-arm64; archive="node-v$node_version-$node_platform.tar.xz" ;;
    osx-x64) node_platform=darwin-x64; archive="node-v$node_version-$node_platform.tar.gz" ;;
    osx-arm64) node_platform=darwin-arm64; archive="node-v$node_version-$node_platform.tar.gz" ;;
    *) echo "Unsupported RID for this script: $rid" >&2; exit 2 ;;
esac
curl -fsSL "https://nodejs.org/dist/v$node_version/$archive" -o "$work/$archive"
actual=$(hash_file "$work/$archive")
[[ "$actual" == "$node_expected" ]] || { echo "Node.js hash mismatch: expected $node_expected, received $actual" >&2; exit 4; }
mkdir -p "$work/node"
tar -xf "$work/$archive" -C "$work/node"
cp -R "$work/node/node-v$node_version-$node_platform" "$stage/node"

web="$repo/src/NovaSharp/LanguageServers/Web"
cp "$web/server.cjs" "$web/package.json" "$web/package-lock.json" "$stage/"
node="$stage/node/bin/node"
npm_cli="$stage/node/lib/node_modules/npm/bin/npm-cli.js"
"$node" "$npm_cli" ci --omit=dev --ignore-scripts --no-audit --no-fund --prefix "$stage"
cp "$stage/node/LICENSE" "$stage/licenses/node-MIT.txt"
printf '%s\n' "$manifest_hash" > "$stage/.source-manifest.sha256"

case "$output/" in
    "$asset_root"/*/) ;;
    *) echo "Refusing to replace an output outside $asset_root" >&2; exit 5 ;;
esac
rm -rf "$output"
mkdir -p "$asset_root"
mv "$stage" "$output"
[[ -f "$output/.source-manifest.sha256" ]] || { echo "Asset replacement did not land at $output" >&2; exit 6; }
echo "Acquired and verified language servers for $rid in $output."
