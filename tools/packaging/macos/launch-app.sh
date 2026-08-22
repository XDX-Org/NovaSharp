#!/usr/bin/env bash
set -euo pipefail

: "${NOVASHARP_APP_BUNDLE:?NOVASHARP_APP_BUNDLE must name the application bundle}"
exec /usr/bin/open -W -n "$NOVASHARP_APP_BUNDLE" --args "$@"
