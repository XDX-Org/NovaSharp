#!/usr/bin/env bash
set -euo pipefail

rid=${1:?usage: acquire-debug-adapter.sh RID}
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
manifest="$root/tools/debug-adapter-assets.json"
url=$(jq -er --arg rid "$rid" '.[$rid].url' "$manifest")
expected=$(jq -er --arg rid "$rid" '.[$rid].sha256' "$manifest")
destination="$root/src/NovaSharp/DebugAdapters/Assets/$rid"
archive=$(mktemp)
license=$(mktemp)
staging=$(mktemp -d)
trap 'rm -f "$archive" "$license"; rm -rf "$staging"' EXIT
curl --fail --location --retry 3 --silent --show-error "$url" --output "$archive"
actual=$(if command -v sha256sum >/dev/null; then sha256sum "$archive" | cut -d' ' -f1; else shasum -a 256 "$archive" | cut -d' ' -f1; fi)
test "$actual" = "$expected" || { echo "netcoredbg hash mismatch for $rid" >&2; exit 1; }
case "$url" in
  *.zip) unzip -q "$archive" -d "$staging" ;;
  *.tar.gz) tar -xzf "$archive" -C "$staging" ;;
  *) echo "Unsupported adapter archive" >&2; exit 1 ;;
esac
rm -rf "$destination"
mkdir -p "$destination"
cp -R "$staging"/. "$destination"/
version=$(jq -er --arg rid "$rid" '.[$rid].version' "$manifest")
curl --fail --location --retry 3 --silent --show-error "https://raw.githubusercontent.com/Samsung/netcoredbg/$version/LICENSE" --output "$license"
license_hash=$(if command -v sha256sum >/dev/null; then sha256sum "$license" | cut -d' ' -f1; else shasum -a 256 "$license" | cut -d' ' -f1; fi)
test "$license_hash" = "6cd03b0de8299b0800f22b35ae842c931ded7684a2d1ba4f1d4188bab9b09a11" || { echo "netcoredbg license hash mismatch" >&2; exit 1; }
cp "$license" "$destination/LICENSE-netcoredbg"
