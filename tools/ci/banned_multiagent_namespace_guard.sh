#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

hits="$(
  rg -n "Aevatar\.Foundation\.Core\.MultiAgent" \
    src test agents tools demos \
    -g '!tools/ci/banned_multiagent_namespace_guard.sh' \
    -g '!**/bin/**' \
    -g '!**/obj/**' || true
)"

if [ -n "${hits}" ]; then
  echo "${hits}"
  echo "Aevatar.Foundation.Core.MultiAgent is forbidden. Issue #643 deleted the dead Multi-Agent core GAgents."
  exit 1
fi
