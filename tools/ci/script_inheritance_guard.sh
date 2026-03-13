#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

if rg -n "class\s+(ScriptDefinitionGAgent|ScriptBehaviorGAgent)\s*:\s*(RoleGAgent|AIGAgentBase<)" src/Aevatar.Scripting.Core -g '*.cs'; then
  echo "ScriptDefinitionGAgent/ScriptBehaviorGAgent must not inherit RoleGAgent/AIGAgentBase."
  exit 1
fi

if ! rg -n "class\s+ScriptDefinitionGAgent\s*:\s*GAgentBase<" src/Aevatar.Scripting.Core/ScriptDefinitionGAgent.cs >/dev/null; then
  echo "ScriptDefinitionGAgent must inherit GAgentBase<> directly."
  exit 1
fi

if ! rg -n "class\s+ScriptBehaviorGAgent\s*:\s*GAgentBase<" src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs >/dev/null; then
  echo "ScriptBehaviorGAgent must inherit GAgentBase<> directly."
  exit 1
fi

echo "Script inheritance guard passed."
