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
    "${fixture}/src/Aevatar.AI.Core/AgentProfiles" \
    "${fixture}/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles" \
    "${fixture}/test/Aevatar.AI.Tests" \
    "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/protos" \
    "${fixture}/agents/channels" \
    "${fixture}/agents/Aevatar.GAgents.Channel.Runtime"
  cp "${REPO_ROOT}/CLAUDE.md" "${fixture}/CLAUDE.md"
  cp "${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/appsettings.json" \
    "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json"
  cp "${REPO_ROOT}/src/Aevatar.AI.Abstractions/ai_messages.proto" \
    "${fixture}/src/Aevatar.AI.Abstractions/ai_messages.proto"
  cp "${REPO_ROOT}"/src/Aevatar.AI.Core/AgentProfiles/*Codec.cs \
    "${fixture}/src/Aevatar.AI.Core/AgentProfiles/"
  cp "${REPO_ROOT}"/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/SystemAgentProfileBootstrap*.cs \
    "${fixture}/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/"
  cp -R "${REPO_ROOT}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat" \
    "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat"
  cp "${REPO_ROOT}/test/Aevatar.AI.Tests/NyxIdChatProfileRolloutEvaluationTests.cs" \
    "${fixture}/test/Aevatar.AI.Tests/"
  cp "${REPO_ROOT}/tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs" \
    "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/"
  cp "${REPO_ROOT}/tools/Aevatar.Tools.AgentProfileRollout/Aevatar.Tools.AgentProfileRollout.csproj" \
    "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/"
  cp "${REPO_ROOT}/tools/Aevatar.Tools.AgentProfileRollout/protos/agent_profile_rollout_tool.proto" \
    "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/protos/"
}

expect_guard_failure() {
  local message="$1"
  if AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
    echo "${message}"
    exit 1
  fi
}

expect_guard_success() {
  local message="$1"
  if ! AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}" >/dev/null 2>&1; then
    echo "${message}"
    exit 1
  fi
}

copy_fixture
AGENT_PROFILE_GOVERNANCE_ROOT="${fixture}" bash "${GUARD}"

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["Aevatar"]["AgentProfiles"]["NyxIdChat"]["Enabled"] = True
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted enabled bindings without a release-spec path."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["Aevatar"]["AgentProfiles"]["NyxIdChat"]["ReleaseSpecPath"] = \
    "Profiles/nyxid-chat/reviewed-release.json"
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted a release-spec path while rollout is disabled."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/appsettings.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["Aevatar"]["AgentProfiles"]["NyxIdChat"]["Profile"] = {
    "instructions": "host-owned content",
}
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted Host-owned Profile content in rollout options."

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
expect_guard_failure "Guard accepted the dynamic system-skill overlay during Profile rollout."

copy_fixture
rm "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json"
expect_guard_failure "Guard accepted a missing rollout release manifest."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["instructions"] = "forbidden Host-owned Profile content"
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted an unknown Profile-content field in the rollout manifest."

copy_fixture
printf '{"releaseId":' \
  > "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json"
expect_guard_failure "Guard accepted malformed rollout ProtoJSON."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
del value["expectedPublishedRevision"]
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted a rollout manifest without its published revision pin."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["expectedPublishedRevision"] = str(2**63)
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted an expectedPublishedRevision above Int64.MaxValue."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["expectedExactSkillClosure"][0]["expectedName"] = "a" * 65
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted an expected Ornn name above the 64-byte domain bound."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["expectedExactSkillClosure"][0]["expectedPublisherId"] = "p" * 257
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted a publisher ID above the 256-byte domain bound."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
value["expectedExactSkillClosure"][0]["expectedPublisherId"] = "publisher-\u0085-id"
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle)
PY
expect_guard_failure "Guard accepted a Unicode control character rejected by .NET char.IsControl."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
pin = value["expectedExactSkillClosure"][0]
value["expectedExactSkillClosure"] = [
    {**pin, "expectedPublisherId": "publisher-\ue000"},
    {**pin, "expectedPublisherId": "publisher-\U00010000"},
]
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle, ensure_ascii=False)
PY
expect_guard_failure "Guard used Python code-point order instead of .NET UTF-16 ordinal order."

copy_fixture
python3 - "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" <<'PY'
import json
import sys
path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    value = json.load(handle)
pin = value["expectedExactSkillClosure"][0]
value["expectedExactSkillClosure"] = [
    {**pin, "expectedPublisherId": "publisher-\U00010000"},
    {**pin, "expectedPublisherId": "publisher-\ue000"},
]
with open(path, "w", encoding="utf-8") as handle:
    json.dump(value, handle, ensure_ascii=False)
PY
expect_guard_success "Guard rejected canonical .NET UTF-16 ordinal closure order."

copy_fixture
mv "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json" \
  "${fixture}/release-target.json"
ln -s "${fixture}/release-target.json" \
  "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/reviewed-release.json"
expect_guard_failure "Guard accepted a symlinked rollout manifest."

copy_fixture
mv "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat" \
  "${fixture}/profile-root-target"
ln -s "${fixture}/profile-root-target" \
  "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat"
expect_guard_failure "Guard accepted a symlinked rollout profile root."

copy_fixture
ln -s "reviewed-release.json" \
  "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/legacy.profile.pb.json"
expect_guard_failure "Guard accepted an extra symlink beside the rollout manifest."

copy_fixture
mkdir -p "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/packages/bad"
printf 'Host-owned skill body\n' \
  > "${fixture}/src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat/packages/bad/SKILL.md"
expect_guard_failure "Guard accepted a package body beside the pin-only rollout manifest."

copy_fixture
printf 'Process.Start("protoc");\n' \
  >> "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs"
expect_guard_failure "Guard accepted runtime protoc process execution in the rollout tool."

copy_fixture
printf '<Protobuf_PackagedToolsPath>bad</Protobuf_PackagedToolsPath>\n' \
  >> "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/Aevatar.Tools.AgentProfileRollout.csproj"
expect_guard_failure "Guard accepted a packaged-protoc export property in the rollout tool."

copy_fixture
printf '<None Include="/tmp/protoc" CopyToOutputDirectory="Always" />\n' \
  >> "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/Aevatar.Tools.AgentProfileRollout.csproj"
expect_guard_failure "Guard accepted a generic protoc copy item in the rollout project."

copy_fixture
printf '\nmessage ReviewedAgentProfileRelease { string instructions = 1; }\n' \
  >> "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/protos/agent_profile_rollout_tool.proto"
expect_guard_failure "Guard accepted the legacy content-rich rollout release message."

copy_fixture
printf '\nTask PublishSkillAsync(byte[] package) => throw null;\n' \
  >> "${fixture}/tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs"
expect_guard_failure "Guard accepted a package publishing API in the rollout command."

copy_fixture
printf 'class Relay { MainnetAgentProfileRolloutSelector? Selector { get; } }\n' \
  > "${fixture}/agents/channels/Bad.cs"
expect_guard_failure "Guard accepted direct-conversation rollout wiring in relay."

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
expect_guard_failure "Guard accepted a client-controlled per-message Profile override."

copy_fixture
mkdir -p "${fixture}/src/Runtime"
printf 'class Runtime { const string Skill = "nyxid-service-call"; }\n' \
  > "${fixture}/src/Runtime/Bad.cs"
expect_guard_failure "Guard accepted a compiled branch with a reviewed skill name."

copy_fixture
printf 'sealed class BootstrapTimestampSource(TimeProvider clock) { DateTimeOffset UtcNow => clock.GetUtcNow(); }\n' \
  > "${fixture}/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/BootstrapTimestampSource.cs"
expect_guard_success "Guard rejected legitimate clock-only Agent Profile bootstrap code."

copy_fixture
printf 'class PollingBootstrap { Task WaitAsync() => Task.Delay(TimeSpan.FromSeconds(30)); }\n' \
  > "${fixture}/src/platform/Aevatar.GAgentService.Hosting/AgentProfiles/PollingBootstrap.cs"
expect_guard_failure "Guard accepted time-based Agent Profile bootstrap polling."

copy_fixture
printf 'class RetiredRuntimeAuthority { AgentProfileSnapshot? Snapshot { get; } }\n' \
  > "${fixture}/src/Aevatar.AI.Core/AgentProfiles/RetiredRuntimeAuthority.cs"
expect_guard_failure "Guard accepted the retired AgentProfileSnapshot runtime symbol."

echo "Agent profile governance guard behavior tests passed."
