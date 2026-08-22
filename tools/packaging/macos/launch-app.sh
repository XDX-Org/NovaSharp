#!/usr/bin/env bash
set -euo pipefail

: "${NOVASHARP_APP_BUNDLE:?NOVASHARP_APP_BUNDLE must name the application bundle}"
arguments=("$@")
result_path=''
for ((index = 0; index < ${#arguments[@]}; index++)); do
  if [[ "${arguments[index]}" == '--phase-smoke-result' ]]; then
    result_path="${arguments[index + 1]:-}"
    break
  fi
done

: "${result_path:?--phase-smoke-result must name the result file}"
bundle_directory="$(cd "$(dirname "$NOVASHARP_APP_BUNDLE")" && pwd)/$(basename "$NOVASHARP_APP_BUNDLE")"
application_path="$bundle_directory/Contents/MacOS/NovaSharp"

/usr/bin/open -n "$bundle_directory" --args "${arguments[@]}"
for ((attempt = 0; attempt < 400; attempt++)); do
  if [[ -s "$result_path" ]]; then
    for ((exit_attempt = 0; exit_attempt < 50; exit_attempt++)); do
      if ! pgrep -f "$application_path" >/dev/null; then
        exit 0
      fi
      sleep 0.1
    done

    echo "NovaSharp wrote its smoke result but did not exit." >&2
    pkill -f "$application_path" || true
    exit 1
  fi
  sleep 0.1
done

echo "NovaSharp did not write its smoke result within 40 seconds." >&2
pkill -f "$application_path" || true
exit 1
