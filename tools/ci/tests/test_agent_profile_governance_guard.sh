#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/agent_profile_governance_guard.sh"

fixture="$(mktemp -d)"
trap 'rm -rf "${fixture}"' EXIT

copy_fixture() {
  rm -rf "${fixture:?}"/*
  mkdir -p \
    "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles" \
    "${fixture}/src/Aevatar.AI.Abstractions" \
    "${fixture}/test/Aevatar.AI.Tests" \
    "${fixture}/tools/Aevatar.Tools.AgentProfileRollout" \
    "${fixture}/agents/channels" \
    "${fixture}/agents/Aevatar.GAgents.Channel.Runtime"
  cp "${REPO_ROOT}/CLAUDE.md" "${fixture}/CLAUDE.md"
  cp "${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/appsettings.json" "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json"
  cp "${REPO_ROOT}/src/Aevatar.AI.Abstractions/ai_messages.proto" "${fixture}/src/Aevatar.AI.Abstractions/ai_messages.proto"
  cp -R "${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat" "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat"
  cp "${REPO_ROOT}/test/Aevatar.AI.Tests/NyxIdChatProfileRolloutEvaluationTests.cs" "${fixture}/test/Aevatar.AI.Tests/"
  cp "${REPO_ROOT}/tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs" "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/"
}

copy_fixture
AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}"

python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
gate = value["Aevatar"]["AgentProfileRollout"]["NyxIdChat"]
gate["NewBindingsEnabled"] = True
gate["CohortBasisPoints"] = 500
gate["ReviewedProfilePath"] = "missing.profile.pb.json"
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted enabled bindings without resolved profile artifacts."
  exit 1
fi

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["Aevatar"]["SystemSkills"]["Enabled"] = True
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted the dynamic system-skill overlay during immutable profile rollout."
  exit 1
fi

copy_fixture
touch "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/nyxid-chat-shadow-v1.profile.pb.json"
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted an incomplete immutable profile pair."
  exit 1
fi

copy_fixture
printf 'class Relay { MainnetAgentProfileRolloutSelector? Selector { get; } }\n' \
  > "${fixture}/agents/channels/Bad.cs"
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted direct-conversation rollout wiring in relay."
  exit 1
fi

copy_fixture
python3 - "${fixture}/src/Aevatar.AI.Abstractions/ai_messages.proto" <<'PY'
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = handle.read()
value = value.replace(
    "message ChatRequestEvent  {",
    "message ChatRequestEvent  {\n  string agent_profile = 1000;")
with open(path, "w", encoding="utf-8") as handle:
    handle.write(value)
PY
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted a client-controlled per-message profile override."
  exit 1
fi

copy_fixture
mkdir -p "${fixture}/src/Runtime"
printf 'class Runtime { const string Skill = "nyxid-service-call"; }\n' > "${fixture}/src/Runtime/Bad.cs"
if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
  echo "Guard accepted a compiled branch with a reviewed skill name."
  exit 1
fi

echo "Agent profile governance guard behavior tests passed."
