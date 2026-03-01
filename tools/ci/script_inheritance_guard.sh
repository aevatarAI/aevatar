#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if rg -n "class\s+ScriptHostGAgent\s*:\s*(RoleGAgent|AIGAgentBase<)" src -g '*.cs'; then
  echo "ScriptHostGAgent must not inherit RoleGAgent/AIGAgentBase."
  exit 1
fi

echo "Script inheritance guard passed."
