#!/usr/bin/env bash
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"
jq empty LANGUAGE-SERVERS.spdx.json
rid=${NOVASHARP_RELEASE_RID:-linux-x64}
dotnet restore NovaSharp.slnx -r "$rid" -p:Configuration=Release
dotnet build NovaSharp.slnx --no-restore --configuration Release --warnaserror
dotnet tests/NovaSharp.Tests/bin/Release/net10.0/NovaSharp.Tests.dll --progress off
if grep -Eq 'Microsoft.CodeAnalysis.(CSharp.)?Features' src/NovaSharp/bin/Release/net10.0/NovaSharp.deps.json; then
  echo 'Release contains legacy Roslyn Features dependencies.' >&2
  exit 1
fi
git diff --check
