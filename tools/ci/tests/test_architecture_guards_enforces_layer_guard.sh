#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
ARCHITECTURE_GUARDS="${REPO_ROOT}/tools/ci/architecture_guards.sh"

call_block="$(
  awk '
    /project_reference_layer_guard\.py/ { capture = 1 }
    capture {
      print
      if ($0 !~ /\\[[:space:]]*$/) {
        exit
      }
    }
  ' "${ARCHITECTURE_GUARDS}"
)"

if [[ -z "${call_block}" ]]; then
  echo "Expected architecture_guards.sh to invoke project_reference_layer_guard.py."
  exit 1
fi

if ! printf '%s\n' "${call_block}" | rg -q -- '--mode[[:space:]]+fail'; then
  echo "Expected project_reference_layer_guard.py invocation to use --mode fail."
  printf '%s\n' "${call_block}"
  exit 1
fi

if printf '%s\n' "${call_block}" | rg -q -- '--mode[[:space:]]+report'; then
  echo "project_reference_layer_guard.py invocation must not use --mode report."
  printf '%s\n' "${call_block}"
  exit 1
fi

echo "architecture_guards layer guard enforcement test passed"
