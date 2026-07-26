#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${AGENT_PROFILE_GOVERNANCE_ROOT:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
cd "${REPO_ROOT}"

required_clause='- 不得在运行时代码、prompt、类型名、字段名或 compiled branch 中硬编码具体 skill / command / template 名称；只有经过部署发布流程核验、由 Host 持有并在启动时结构校验的 server-owned profile 数据，才可列举 opaque intent 标识、不可变 Ornn `{guid, literal_version}` 引用、显式 trigger alias 以及单义 `tool_names` / `tool_set_refs`。客户端不得提交、覆盖或逐消息切换这些 profile/tool policy 数据；运行时 router 与 classifier template 只能解释 typed profile contract，不得按具体 skill 名写分支。普通 on-demand discovery 继续走通用 search / `use_skill` 协议；测试 fixture 可引用具体名称。'
if ! rg -F -x -q -- "${required_clause}" CLAUDE.md; then
  echo "CLAUDE.md must contain the exact reviewed Host-profile governance clause."
  exit 1
fi

appsettings="src/Aevatar.Mainnet.Host.Api/appsettings.json"
python3 - "${appsettings}" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    config = json.load(handle)
aevatar = config["Aevatar"]
if aevatar["SystemSkills"]["Enabled"] is not False:
    raise SystemExit("Aevatar:SystemSkills:Enabled must remain false during profile rollout.")
gate = aevatar["AgentProfiles"]["NyxIdChat"]
expected_keys = {"Enabled", "ReleaseSpecPath"}
if set(gate) != expected_keys:
    raise SystemExit("NyxID chat Agent Profile options must contain only Enabled and ReleaseSpecPath.")
enabled = gate["Enabled"]
release_spec_path = gate["ReleaseSpecPath"]
if not isinstance(enabled, bool):
    raise SystemExit("Enabled must be a boolean.")
if not isinstance(release_spec_path, str):
    raise SystemExit("ReleaseSpecPath must be a string.")
if enabled and not release_spec_path.strip():
    raise SystemExit("Enabled profile rollout requires ReleaseSpecPath.")
if not enabled and release_spec_path.strip():
    raise SystemExit("Disabled profile rollout cannot configure ReleaseSpecPath.")
PY

profile_root="src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat"
release_spec="${profile_root}/reviewed-release.json"
if [[ ! -d "${profile_root}" || -L "${profile_root}" ]]; then
  echo "The Agent Profile rollout root must be a regular non-link directory."
  exit 1
fi
if [[ ! -f "${release_spec}" || -L "${release_spec}" || ! -s "${release_spec}" ]]; then
  echo "The pin-only Agent Profile rollout release manifest is required."
  exit 1
fi
unexpected_profile_entry="$(find "${profile_root}" -mindepth 1 -maxdepth 1 ! -name "reviewed-release.json" -print -quit)"
if [[ -n "${unexpected_profile_entry}" ]]; then
  echo "The Mainnet rollout directory may contain only the pin-only reviewed-release.json manifest."
  exit 1
fi

python3 - "${release_spec}" <<'PY'
import base64
import binascii
import json
import re
import sys
import unicodedata
import uuid

ROOT_KEYS = {
    "releaseId",
    "stage",
    "profileReference",
    "activationMode",
    "cohortSalt",
    "cohortBasisPoints",
    "expectedPublishedRevision",
    "expectedPublishedSnapshotSha256",
    "expectedExactSkillClosure",
    "runtimeBounds",
}
REFERENCE_KEYS = {"ownerHandle", "profileSlug"}
EXACT_SKILL_KEYS = {
    "skillGuid",
    "literalVersion",
    "expectedName",
    "expectedPublisherId",
}
RUNTIME_BOUND_KEYS = {
    "maxPlanSteps",
    "handoffTtlSeconds",
    "classifierTimeoutMs",
    "maxSelectedSkillBytes",
}
ACTIVATION_MODES = {
    "AGENT_PROFILE_ROLLOUT_ACTIVATION_MODE_SHADOW",
    "AGENT_PROFILE_ROLLOUT_ACTIVATION_MODE_ENFORCED",
}
HUMAN_REFERENCE = re.compile(r"[a-z0-9]+(?:-[a-z0-9]+)*\Z")
LITERAL_VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\Z")
POSITIVE_INT64 = re.compile(r"[1-9][0-9]*\Z")


def strict_object(pairs):
    value = {}
    for key, item in pairs:
        if key in value:
            raise ValueError(f"Duplicate ProtoJSON field: {key}")
        value[key] = item
    return value


def require_exact_keys(value, expected, label):
    if not isinstance(value, dict) or set(value) != expected:
        raise SystemExit(f"{label} must contain exactly the typed pin-only fields.")


def require_canonical_text(value, label):
    if (
        not isinstance(value, str)
        or not value
        or value != value.strip()
        or any(unicodedata.category(character) == "Cc" for character in value)
    ):
        raise SystemExit(f"{label} must be canonical non-empty text.")


def utf16_ordinal_key(value):
    try:
        return value.encode("utf-16-be")
    except UnicodeEncodeError as error:
        raise SystemExit("Rollout text must contain valid Unicode scalar values.") from error


try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        release = json.load(handle, object_pairs_hook=strict_object)
except (json.JSONDecodeError, UnicodeDecodeError, ValueError) as error:
    raise SystemExit(f"Rollout release manifest must be strict ProtoJSON: {error}") from error

require_exact_keys(release, ROOT_KEYS, "Rollout release manifest")
require_canonical_text(release["releaseId"], "releaseId")
require_canonical_text(release["stage"], "stage")
require_canonical_text(release["cohortSalt"], "cohortSalt")

reference = release["profileReference"]
require_exact_keys(reference, REFERENCE_KEYS, "profileReference")
if reference != {"ownerHandle": "system", "profileSlug": "nyxid-chat"}:
    raise SystemExit("Rollout Profile reference must be exactly system/nyxid-chat.")
if release["activationMode"] not in ACTIVATION_MODES:
    raise SystemExit("Rollout activationMode must be typed SHADOW or ENFORCED.")

cohort_basis_points = release["cohortBasisPoints"]
if (
    isinstance(cohort_basis_points, bool)
    or not isinstance(cohort_basis_points, int)
    or not 1 <= cohort_basis_points <= 10_000
):
    raise SystemExit("Rollout cohortBasisPoints must be an integer in 1..10000.")
if (
    not isinstance(release["expectedPublishedRevision"], str)
    or POSITIVE_INT64.fullmatch(release["expectedPublishedRevision"]) is None
    or int(release["expectedPublishedRevision"]) > 9_223_372_036_854_775_807
):
    raise SystemExit("Rollout expectedPublishedRevision must be one canonical positive int64 string.")

try:
    snapshot_digest = base64.b64decode(
        release["expectedPublishedSnapshotSha256"],
        validate=True,
    )
except (TypeError, ValueError, binascii.Error) as error:
    raise SystemExit("Rollout snapshot digest must be canonical base64.") from error
if len(snapshot_digest) != 32:
    raise SystemExit("Rollout snapshot digest pin must contain exactly 32 bytes.")

closure = release["expectedExactSkillClosure"]
if not isinstance(closure, list) or not 1 <= len(closure) <= 32:
    raise SystemExit("Rollout exact skill closure must contain between 1 and 32 pins.")
identities = []
for index, exact_skill in enumerate(closure):
    require_exact_keys(exact_skill, EXACT_SKILL_KEYS, f"expectedExactSkillClosure[{index}]")
    guid = exact_skill["skillGuid"]
    try:
        canonical_guid = str(uuid.UUID(guid))
    except (AttributeError, ValueError) as error:
        raise SystemExit(f"Closure skill GUID at index {index} is invalid.") from error
    if canonical_guid != guid:
        raise SystemExit(f"Closure skill GUID at index {index} is not canonical lowercase D format.")
    if not isinstance(exact_skill["literalVersion"], str) or LITERAL_VERSION.fullmatch(
        exact_skill["literalVersion"]
    ) is None:
        raise SystemExit(f"Closure literal version at index {index} is invalid.")
    if not isinstance(exact_skill["expectedName"], str) or HUMAN_REFERENCE.fullmatch(
        exact_skill["expectedName"]
    ) is None or len(exact_skill["expectedName"].encode("utf-8")) > 64:
        raise SystemExit(f"Closure expected name at index {index} is invalid.")
    require_canonical_text(exact_skill["expectedPublisherId"], f"closure publisher at index {index}")
    if len(exact_skill["expectedPublisherId"].encode("utf-8")) > 256:
        raise SystemExit(f"Closure publisher at index {index} exceeds its byte bound.")
    identities.append("\0".join((
        guid,
        exact_skill["literalVersion"],
        exact_skill["expectedName"],
        exact_skill["expectedPublisherId"],
    )))
if len(set(identities)) != len(identities):
    raise SystemExit("Rollout exact skill closure pins must be unique.")
if identities != sorted(identities, key=utf16_ordinal_key):
    raise SystemExit("Rollout exact skill closure pins must use canonical order.")

runtime_bounds = release["runtimeBounds"]
require_exact_keys(runtime_bounds, RUNTIME_BOUND_KEYS, "runtimeBounds")
if runtime_bounds != {
    "maxPlanSteps": 4,
    "handoffTtlSeconds": 900,
    "classifierTimeoutMs": 600,
    "maxSelectedSkillBytes": 24_576,
}:
    raise SystemExit("NyxID chat rollout runtime bounds must be exactly 4/900/600/24576.")
PY

rollout_commands="tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs"
rollout_project="tools/Aevatar.Tools.AgentProfileRollout/Aevatar.Tools.AgentProfileRollout.csproj"
rollout_contract="tools/Aevatar.Tools.AgentProfileRollout/protos/agent_profile_rollout_tool.proto"
if rg -n \
  'System\.Diagnostics|ProcessStartInfo|Process\.Start|ResolveProtocPath|GetEnvironmentVariable\("PROTOC"\)' \
  "${rollout_commands}"; then
  echo "The rollout tool must not discover or execute protoc at runtime."
  exit 1
fi
if rg -ni \
  'protoc|IncludeAgentProfileRolloutProtoc|Protobuf_PackagedToolsPath|Protobuf_Tools(Os|Cpu)' \
  "${rollout_project}"; then
  echo "The rollout tool project must not export a packaged protoc executable."
  exit 1
fi
if rg -n \
  'ReviewedAgentProfileRelease|ReviewedSkillPackage|AgentProfileSnapshot|PublishSkillAsync|CreateSkillsetAsync|ReadExactSkillsetAsync|MaterializeProfile|BuildPackageArchive|ShadowProfileFileName|EnforcedProfileFileName' \
  "${rollout_commands}" "${rollout_contract}"; then
  echo "The rollout tool must not restore the legacy Host-owned Profile content or publishing path."
  exit 1
fi

bootstrap_hosting_root="src/platform/Aevatar.GAgentService.Hosting/AgentProfiles"
if rg -n \
  'Task\.Delay\s*\(|PeriodicTimer|RetryInterval|System\.Threading\.Timer|new\s+Timer\s*\(' \
  "${bootstrap_hosting_root}" -g '*.cs'; then
  echo "Agent Profile bootstrap must be driven by committed-materialization signals, not timed polling."
  exit 1
fi

runtime_profile_contract="src/Aevatar.AI.Abstractions/ai_messages.proto"
runtime_profile_core="src/Aevatar.AI.Core"
if rg -n \
  '\b(AgentProfileSnapshot|AgentProfileSkillMember|ExactRemoteSkillsetRef)\b' \
  "${runtime_profile_contract}" "${runtime_profile_core}" -g '*.cs' -g '*.proto'; then
  echo "The retired Agent Profile snapshot authority surface must not exist in AI runtime contracts or Core."
  exit 1
fi

if rg -n 'nyxid-service-(discovery|connect|call|maintenance)' src agents \
  -g '*.cs' \
  -g '!src/Aevatar.Mainnet.Host.Api/Profiles/**'; then
  echo "Runtime compiled branches must not hardcode reviewed skill names."
  exit 1
fi
if rg -n 'AgentProfileRollout|MainnetAgentProfileRolloutSelector' agents/channels agents/Aevatar.GAgents.Channel.Runtime 2>/dev/null; then
  echo "Relay and channel runtime must not consume the direct-conversation profile rollout gate."
  exit 1
fi

chat_request_contract="${runtime_profile_contract}"
python3 - "${chat_request_contract}" <<'PY'
import re
import sys

with open(sys.argv[1], encoding="utf-8") as handle:
    source = handle.read()
match = re.search(r"\bmessage\s+ChatRequestEvent\s*\{", source)
if match is None:
    raise SystemExit("ChatRequestEvent contract is required for profile governance.")
depth = 1
index = match.end()
while index < len(source) and depth:
    if source[index] == "{":
        depth += 1
    elif source[index] == "}":
        depth -= 1
    index += 1
body = source[match.end():index - 1]
for line in body.splitlines():
    declaration = re.match(r"\s*(?:optional\s+|repeated\s+)?[\w.<> ,]+\s+(\w+)\s*=\s*\d+\s*;", line)
    if declaration and re.search(r"profile|activation_mode|tool_policy", declaration.group(1), re.IGNORECASE):
        raise SystemExit("ChatRequestEvent must not expose per-message profile or tool-policy overrides.")
PY
if ! rg -q 'HaveCount\(64\)' test/Aevatar.AI.Tests/NyxIdChatProfileRolloutEvaluationTests.cs; then
  echo "The executable agent-profile evaluation matrix must assert exactly 64 cases."
  exit 1
fi
if ! rg -q 'args\[0\] is not \("provision" or "evaluate"\)' "${rollout_commands}"; then
  echo "The deployment-only rollout CLI must expose only provision and evaluate."
  exit 1
fi

echo "Agent profile governance guard passed."
