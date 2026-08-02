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
    "${fixture}/src/Aevatar.AI.Abstractions" \
    "${fixture}/src/Aevatar.Mainnet.Host.Api/Hosting" \
    "${fixture}/src/platform/Aevatar.GAgentService.Abstractions/Protos" \
    "${fixture}/src/platform/Aevatar.GAgentService.Core/AgentProfiles" \
    "${fixture}/src/platform/Aevatar.GAgentService.Projection/Queries" \
    "${fixture}/agents/channels" \
    "${fixture}/agents/Aevatar.GAgents.Channel.Runtime"
  cp "${REPO_ROOT}/CLAUDE.md" "${fixture}/CLAUDE.md"
  cp "${REPO_ROOT}/src/Aevatar.AI.Abstractions/ai_messages.proto" \
    "${fixture}/src/Aevatar.AI.Abstractions/ai_messages.proto"
  cp "${REPO_ROOT}/src/platform/Aevatar.GAgentService.Abstractions/Protos/agent_profiles.proto" \
    "${fixture}/src/platform/Aevatar.GAgentService.Abstractions/Protos/agent_profiles.proto"
  cp "${REPO_ROOT}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs" \
    "${fixture}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs"
  cp "${REPO_ROOT}/src/platform/Aevatar.GAgentService.Projection/Queries/AgentProfileQueryReaders.cs" \
    "${fixture}/src/platform/Aevatar.GAgentService.Projection/Queries/AgentProfileQueryReaders.cs"
  printf '%s\n' \
    'static class HostComposition { const string ToolSet = AgentProfilePolicies.NyxIdChatRouteToolSet; }' \
    > "${fixture}/src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"
}

expect_rejected() {
  local description="$1"
  if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
    echo "Guard accepted ${description}."
    exit 1
  fi
}

copy_fixture
AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}"

copy_fixture
mkdir -p "${fixture}/src/Aevatar.Mainnet.Host.Api"
printf '%s\n' '{"Aevatar":{"AgentProfileRollout":{"NyxIdChat":{"ReviewedProfilePath":"legacy.json"}}}}' \
  > "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json"
expect_rejected "the legacy AgentProfileRollout configuration"

copy_fixture
mkdir -p "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles"
printf '%s\n' \
  'class MainnetAgentProfileRolloutSelector { byte[] Load(string path) => File.ReadAllBytes(path); }' \
  > "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/Legacy.cs"
expect_rejected "the legacy runtime profile file authority"

copy_fixture
mkdir -p "${fixture}/src/Runtime"
printf '%s\n' \
  'class AgentProfileRegistry { readonly Dictionary<string, object> profiles = new(); }' \
  > "${fixture}/src/Runtime/ProfileRegistry.cs"
expect_rejected "a process-local Profile registry"

copy_fixture
printf '%s\n' \
  'static class HostComposition { void AddReviewedRouteToolSet(object options) {} }' \
  > "${fixture}/src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"
expect_rejected "dynamic Profile tool-set registration"

copy_fixture
printf '%s\n' \
  'class ChannelRuntime { IAgentProfileExecutionQueryPort? Profiles { get; } }' \
  > "${fixture}/agents/channels/Bad.cs"
expect_rejected "Agent Profile execution resolution in relay/channel runtime"

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
expect_rejected "a client-controlled per-message Profile override"

copy_fixture
mkdir -p "${fixture}/src/Runtime"
printf '%s\n' 'class Runtime { const string Skill = "nyxid-service-call"; }' \
  > "${fixture}/src/Runtime/Bad.cs"
expect_rejected "a compiled branch with a reviewed skill name"

copy_fixture
rm "${fixture}/src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs"
expect_rejected "a repository without the Profile authority actor"

copy_fixture
rm "${fixture}/src/platform/Aevatar.GAgentService.Projection/Queries/AgentProfileQueryReaders.cs"
expect_rejected "a repository without the protected execution read model reader"

echo "Agent profile governance guard behavior tests passed."
