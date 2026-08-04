#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="${AGENT_PROFILE_GOVERNANCE_ROOT:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
cd "${REPO_ROOT}"

required_clause='- 不得在运行时代码、prompt、类型名、字段名或 compiled branch 中硬编码具体 skill / command / template 名称；只有经过服务端 validate/publish sealing 流程核验、由 AgentProfileGAgent 持有的 committed published state，才可列举 opaque intent 标识、不可变 Ornn `{guid, literal_version}` 引用、显式 trigger alias 以及单义 `tool_names` / `tool_set_refs`。经授权 owner 通过受控 draft -> validate -> publish 流程提交 Profile 内容属于发布流程输入，不属于 runtime/client override；请求与 ChatRequestEvent 不得逐消息携带或切换 profile/tool policy，客户端不得覆盖 server-sealed snapshot；运行时 router 与 classifier template 只能解释 typed profile contract，不得按具体 skill 名写分支。普通 on-demand discovery 继续走通用 search / `use_skill` 协议；测试 fixture 可引用具体名称。'
if ! rg -F -x -q -- "${required_clause}" CLAUDE.md; then
  echo "CLAUDE.md must contain the exact committed Profile governance clause."
  exit 1
fi

authority_actor="src/platform/Aevatar.GAgentService.Core/AgentProfiles/AgentProfileGAgent.cs"
protected_reader="src/platform/Aevatar.GAgentService.Projection/Queries/AgentProfileQueryReaders.cs"
profile_contract="src/platform/Aevatar.GAgentService.Abstractions/Protos/agent_profiles.proto"
if [[ ! -s "${authority_actor}" ]] || ! rg -q 'class AgentProfileGAgent' "${authority_actor}"; then
  echo "AgentProfileGAgent committed-state authority is required."
  exit 1
fi
if [[ ! -s "${protected_reader}" ]] || ! rg -q 'IAgentProfileExecutionQueryPort' "${protected_reader}"; then
  echo "The protected Agent Profile execution read-model reader is required."
  exit 1
fi
if [[ ! -s "${profile_contract}" ]] ||
   ! rg -q 'message AgentProfilePublishedSnapshot' "${profile_contract}"; then
  echo "The typed Agent Profile published snapshot contract is required."
  exit 1
fi

legacy_patterns='AgentProfileRollout|ReviewedProfilePath|MainnetAgentProfileRolloutSelector|MainnetNyxIdChatAgentProfileSnapshotSource|NyxIdChatAgentProfileOptions|AddReviewedRouteToolSet|Aevatar\.Tools\.AgentProfileRollout'
legacy_targets=()
for target in src agents test tools aevatar.slnx; do
  [[ -e "${target}" ]] && legacy_targets+=("${target}")
done
if ((${#legacy_targets[@]} > 0)) && rg -n "${legacy_patterns}" "${legacy_targets[@]}" \
  -g '!**/bin/**' -g '!**/obj/**' \
  -g '!agent_profile_governance_guard.sh' \
  -g '!test_agent_profile_governance_guard.sh'; then
  echo "Legacy file/config/CLI Agent Profile authority must not return."
  exit 1
fi

if rg -n 'class[[:space:]]+[A-Za-z0-9_]*AgentProfile[A-Za-z0-9_]*Registry|AgentProfile[^;=]*(Dictionary|ConcurrentDictionary|HashSet)|(?:Dictionary|ConcurrentDictionary|HashSet)<[^>]*AgentProfile' \
  src agents -g '*.cs' -g '!**/bin/**' -g '!**/obj/**'; then
  echo "Process-local Agent Profile registries are forbidden."
  exit 1
fi

host_composition="src/Aevatar.Mainnet.Host.Api/Hosting/MainnetHostBuilderExtensions.cs"
if [[ ! -s "${host_composition}" ]] ||
   ! rg -q 'AgentProfilePolicies\.NyxIdChatRouteToolSet' "${host_composition}"; then
  echo "The nyxid.chat Profile route tool set must be registered statically by Host."
  exit 1
fi

if rg -n 'nyxid-service-(discovery|connect|call|maintenance)' src agents \
  -g '*.cs'; then
  echo "Runtime compiled branches must not hardcode reviewed skill names."
  exit 1
fi
if rg -n 'AgentProfileRollout|IAgentProfileExecutionQueryPort|AgentProfileExecutionQueryReader' \
  agents/channels agents/Aevatar.GAgents.Channel.Runtime 2>/dev/null; then
  echo "Relay and channel runtime must not resolve the direct-conversation Agent Profile."
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
    raise SystemExit("ChatRequestEvent contract is required for Profile governance.")
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
        raise SystemExit("ChatRequestEvent must not expose per-message Profile or tool-policy overrides.")
PY

echo "Agent profile governance guard passed."
