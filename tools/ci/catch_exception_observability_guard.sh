#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

python3 "${SCRIPT_DIR}/guards/catch_exception_observability_guard.py" \
  --root "${REPO_ROOT}" \
  --baseline "${SCRIPT_DIR}/guards/catch_exception_observability_baseline.tsv" \
  "${REPO_ROOT}/src" \
  "${REPO_ROOT}/agents"
