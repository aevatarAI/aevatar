#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if rg -n "class\s+(ScriptDefinitionGAgent|ScriptRuntimeGAgent)\s*:\s*(RoleGAgent|AIGAgentBase<)" src/Aevatar.Scripting.Core -g '*.cs'; then
  echo "ScriptDefinitionGAgent/ScriptRuntimeGAgent must not inherit RoleGAgent/AIGAgentBase."
  exit 1
fi

if ! rg -n "class\s+ScriptDefinitionGAgent\s*:\s*GAgentBase<" src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs >/dev/null; then
  echo "ScriptDefinitionGAgent must inherit GAgentBase<> directly."
  exit 1
fi

if ! rg -n "class\s+ScriptRuntimeGAgent\s*:\s*GAgentBase<" src/Aevatar.Scripting.Core/ScriptRuntimeGAgent.cs >/dev/null; then
  echo "ScriptRuntimeGAgent must inherit GAgentBase<> directly."
  exit 1
fi

echo "Script inheritance guard passed."
