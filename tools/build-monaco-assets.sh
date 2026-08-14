#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"

rid=${NOVASHARP_RELEASE_RID:-linux-x64}
node_root="$root/src/NovaSharp/LanguageServers/Assets/$rid/node"
node="$node_root/bin/node"
npm_cli="$node_root/lib/node_modules/npm/bin/npm-cli.js"
if [[ -x "$node_root/node.exe" && -f "$node_root/node_modules/npm/bin/npm-cli.js" ]]; then
  node="$node_root/node.exe"
  npm_cli="$node_root/node_modules/npm/bin/npm-cli.js"
elif [[ ! -x "$node" || ! -f "$npm_cli" ]]; then
  if command -v node >/dev/null && command -v npm >/dev/null; then
    npm ci --ignore-scripts --no-audit --no-fund
    npm run build:monaco
    npm run check:monaco
    exit
  fi
  echo "Node.js and npm are required. Acquire language-server assets for $rid or install Node.js 24.19.0." >&2
  exit 1
fi

export PATH="$(dirname "$node"):$PATH"
"$node" "$npm_cli" ci --ignore-scripts --no-audit --no-fund
"$node" "$npm_cli" run build:monaco
"$node" "$npm_cli" run check:monaco
