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
gate = aevatar["AgentProfileRollout"]["NyxIdChat"]
enabled = gate["NewBindingsEnabled"]
bps = gate["CohortBasisPoints"]
if enabled is False and bps != 0:
    raise SystemExit("Disabled profile rollout must have CohortBasisPoints=0.")
if enabled is True and not 1 <= bps <= 10000:
    raise SystemExit("Enabled profile rollout must have CohortBasisPoints in 1..10000.")
if not isinstance(gate["ReviewedProfilePath"], str):
    raise SystemExit("ReviewedProfilePath must be a string.")
PY

profile_root="src/Aevatar.Mainnet.Host.Api/Profiles/nyxid-chat"
release_spec="${profile_root}/reviewed-release.textproto"
if [[ ! -s "${release_spec}" ]]; then
  echo "Reviewed agent-profile release spec is required."
  exit 1
fi
if [[ "$(rg -c '^packages \{' "${release_spec}")" -ne 4 ]]; then
  echo "Reviewed agent-profile release must declare exactly four packages."
  exit 1
fi
if ! rg -q 'reviewed_publisher_id: "5d0d7b72-acff-49af-bb1b-9f30bbb7c102"' "${release_spec}"; then
  echo "Reviewed release publisher ID is missing or changed."
  exit 1
fi
if rg -ni 'latest|placeholder|todo' "${release_spec}" "${profile_root}/packages"; then
  echo "Reviewed package/release data must not contain mutable or unresolved identities."
  exit 1
fi
if rg -n '^[[:space:]]*-[[:space:]]*(nyxid_services|nyxid_proxy|nyxid_external_keys|nyxid_service_handoff|[^[:space:]]*credential[^[:space:]]*|[^[:space:]]*api_key[^[:space:]]*)[[:space:]]*$' "${profile_root}/packages"; then
  echo "Reviewed focused packages must not declare broad or credential-bearing tools."
  exit 1
fi

profile_files=(
  "${profile_root}/nyxid-chat-shadow-v2.profile.pb.json"
  "${profile_root}/nyxid-chat-enforced-v2.profile.pb.json"
)
existing_profiles=0
for profile_file in "${profile_files[@]}"; do
  [[ -f "${profile_file}" ]] && existing_profiles=$((existing_profiles + 1))
done
if [[ "${existing_profiles}" -eq 1 ]]; then
  echo "Resolved SHADOW and ENFORCED profiles must be committed as a complete pair."
  exit 1
fi
if [[ "${existing_profiles}" -eq 2 ]]; then
  if rg -ni 'latest|placeholder|todo|"guid"[[:space:]]*:[[:space:]]*""' "${profile_files[@]}"; then
    echo "Resolved profiles contain mutable, unresolved, or empty exact identities."
    exit 1
  fi
  python3 - "${profile_files[@]}" <<'PY'
import json
import sys

shadow, enforced = (json.load(open(path, encoding="utf-8")) for path in sys.argv[1:])
if shadow["activationMode"] != "AGENT_PROFILE_ACTIVATION_MODE_SHADOW":
    raise SystemExit("SHADOW artifact has the wrong activation mode.")
if enforced["activationMode"] != "AGENT_PROFILE_ACTIVATION_MODE_ENFORCED":
    raise SystemExit("ENFORCED artifact has the wrong activation mode.")
if shadow["profileVersion"] == enforced["profileVersion"]:
    raise SystemExit("SHADOW and ENFORCED must have distinct immutable profile versions.")
if len(shadow["members"]) != 4 or len(enforced["members"]) != 4:
    raise SystemExit("Resolved profiles must contain exactly four members.")
PY
fi

if python3 - "${appsettings}" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as handle:
    gate = json.load(handle)["Aevatar"]["AgentProfileRollout"]["NyxIdChat"]
raise SystemExit(0 if gate["NewBindingsEnabled"] else 1)
PY
then
  if [[ "${existing_profiles}" -ne 2 ]]; then
    echo "Enabled new bindings require both resolved immutable profile artifacts."
    exit 1
  fi
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
chat_request_contract="src/Aevatar.AI.Abstractions/ai_messages.proto"
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
if ! rg -q 'args\[0\] is not \("provision" or "evaluate"\)' tools/Aevatar.Tools.AgentProfileRollout/AgentProfileRolloutCommands.cs; then
  echo "The deployment-only rollout CLI must expose only provision and evaluate."
  exit 1
fi

echo "Agent profile governance guard passed."
