#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
CI_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

pinned_entrypoints="$(
  rg -n '(^|[[:space:]])python3\.[0-9]+([[:space:]]|$)' "${CI_DIR}" -g '*.sh' || true
)"

if [[ -n "${pinned_entrypoints}" ]]; then
  echo "CI Python entrypoints must use portable python3 instead of a pinned minor version."
  echo "${pinned_entrypoints}"
  exit 1
fi

echo "Python entrypoint portability test passed"
