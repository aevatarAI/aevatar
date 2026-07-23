#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

profile_roots=(
  "src/platform/Aevatar.GAgentService.Core/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Application/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Projection/Audit/AgentProfileAuditCommittedEventTranslators.cs"
)
core_projection_roots=(
  "src/platform/Aevatar.GAgentService.Core/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Projection/Audit/AgentProfileAuditCommittedEventTranslators.cs"
)
query_read_roots=(
  "src/platform/Aevatar.GAgentService.Application/AgentProfiles"
  "src/platform/Aevatar.GAgentService.Projection/AgentProfiles"
)
application_root="src/platform/Aevatar.GAgentService.Application/AgentProfiles"
exact_adapter_file="src/Aevatar.AI.ToolProviders.Ornn/AgentProfiles/OrnnExactAgentProfileSkillResolver.cs"
ornn_client_file="src/Aevatar.AI.ToolProviders.Ornn/OrnnSkillClient.cs"
tool_file="src/Aevatar.AI.ToolProviders.AgentCatalog/AgentProfiles/AgentProfilesTool.cs"

for required_path in \
  "${profile_roots[@]}" \
  "${exact_adapter_file}" \
  "${ornn_client_file}" \
  "${tool_file}"; do
  if [[ ! -e "${required_path}" ]]; then
    echo "Agent Profile boundary guard input is missing: ${required_path}"
    exit 1
  fi
done

bag_hits="$(
  rg -n '\b(Metadata|Headers|Items|AsyncLocal)\b' \
    "${profile_roots[@]}" \
    -g '*.cs' \
    -g '*.proto' \
    -g '!AgentProfileDocumentMetadataProviders.cs' \
    || true
)"
if [[ -n "${bag_hits}" ]]; then
  echo "${bag_hits}"
  echo "Agent Profile Core/Application/Projection code must keep stable semantics typed and must not introduce Metadata, Headers, Items, or AsyncLocal state."
  exit 1
fi

static_context_hits="$(
  rg -n 'static[[:space:]][^;\n]*(CurrentAgentProfile|CurrentProfile|AgentProfileCurrent|ProfileContext)' \
    "${profile_roots[@]}" \
    -g '*.cs' \
    || true
)"
if [[ -n "${static_context_hits}" ]]; then
  echo "${static_context_hits}"
  echo "Static current Agent Profile context is forbidden. Profile authority must remain actor/read-model owned."
  exit 1
fi

fact_collection_hits="$(
  rg -n -P \
    'private\s+(?:static\s+)?(?:readonly\s+)?(?:Dictionary|ConcurrentDictionary|HashSet|Queue)<[^;\n]+>\s+[A-Za-z_][A-Za-z0-9_]*\s*(?:=|;)' \
    "${profile_roots[@]}" \
    -g '*.cs' \
    || true
)"
if [[ -n "${fact_collection_hits}" ]]; then
  echo "${fact_collection_hits}"
  echo "Private service-level collections must not hold Agent Profile or binding facts. Use actor-owned state or read models."
  exit 1
fi

core_projection_dependency_hits="$(
  rg -n \
    'Aevatar\.AI\.ToolProviders\.Ornn|Ornn(SkillClient|RemoteSkillFetcher|Search|SkillFetcher)|System\.Net\.Http|Microsoft\.AspNetCore|Http(Client|Request|Response)|IRemoteSkillFetcher|SearchSkillsAsync|GetSkillJsonAsync' \
    "${core_projection_roots[@]}" \
    -g '*.cs' \
    -g '*.proto' \
    || true
)"
if [[ -n "${core_projection_dependency_hits}" ]]; then
  echo "${core_projection_dependency_hits}"
  echo "Agent Profile Core/Projection must not depend on Ornn, HTTP, remote fetchers, or skill-search/name lookup paths."
  exit 1
fi

application_fetch_hits="$(
  rg -n -i \
    'GetSkillJsonAsync|SearchSkillsAsync|IRemoteSkillFetcher|nameOrId|idOrName|inlineSkill|\blatest\b' \
    "${application_root}" \
    -g '*.cs' \
    || true
)"
if [[ -n "${application_fetch_hits}" ]]; then
  echo "${application_fetch_hits}"
  echo "Agent Profile Application code accepts only exact skill references; name/latest/inline lookup and name-capable fetchers are forbidden."
  exit 1
fi

if ! rg -Fq '/api/v1/skills/{Uri.EscapeDataString(guid)}?version={Uri.EscapeDataString(literalVersion)}' \
  "${ornn_client_file}"; then
  echo "${ornn_client_file}"
  echo "The exact Ornn Profile detail read must include the literal ?version= endpoint form."
  exit 1
fi
if ! rg -Fq '/api/v1/skills/{Uri.EscapeDataString(guid)}/json?version={Uri.EscapeDataString(literalVersion)}' \
  "${ornn_client_file}"; then
  echo "${ornn_client_file}"
  echo "The exact Ornn Profile JSON read must include the literal ?version= endpoint form."
  exit 1
fi
if ! rg -q 'GetExactSkillDetailAsync\(' "${exact_adapter_file}" || \
   ! rg -q 'GetExactSkillJsonAsync\(' "${exact_adapter_file}"; then
  echo "${exact_adapter_file}"
  echo "The exact Ornn Profile adapter must use both exact-version detail and JSON reads."
  exit 1
fi
name_fetch_hits="$(
  rg -n 'GetSkillJsonAsync|SearchSkillsAsync|GetSkillSetAsync|IRemoteSkillFetcher' \
    "${exact_adapter_file}" \
    || true
)"
if [[ -n "${name_fetch_hits}" ]]; then
  echo "${name_fetch_hits}"
  echo "The exact Ornn Profile adapter must not call name-capable, search, set, or generic remote fetch paths."
  exit 1
fi

query_read_hits="$(
  rg -n -i \
    'ProjectionActivation|IProjectionPortActivationService|IProjectionPortReleaseService|IActorRuntime|\bIEventStore\b|event[[:space:]_-]*replay|RebuildAsync|PrimeAsync|Priming|Ensure[A-Za-z0-9_]*Projection|Attach[A-Za-z0-9_]*Projection|ActivateAsync' \
    "${query_read_roots[@]}" \
    -g '*Query*.cs' \
    -g '*Read*.cs' \
    || true
)"
if [[ -n "${query_read_hits}" ]]; then
  echo "${query_read_hits}"
  echo "Agent Profile query/read paths must read materialized models only; activation, runtime, event-store, replay, and priming APIs are forbidden."
  exit 1
fi

tool_schema="$(
  awk '
    /public string ParametersSchema[[:space:]]*=>[[:space:]]*"""/ { capture = 1 }
    capture { print }
    capture && /""";/ { exit }
  ' "${tool_file}"
)"
if [[ -z "${tool_schema}" ]]; then
  echo "${tool_file}"
  echo "The agent_profiles ParametersSchema block was not found."
  exit 1
fi
tool_schema_hits="$(
  printf '%s\n' "${tool_schema}" \
    | rg -n -i \
      '"[^\"]*(owner_?subject|subject_id|scope_id|profile_?id|system_authority|platform_id|sealed|credential|access_?token|bearer|token)[^\"]*"[[:space:]]*:' \
    || true
)"
if [[ -n "${tool_schema_hits}" ]]; then
  echo "${tool_schema_hits}"
  echo "The agent_profiles tool schema must not accept owner subjects, scope/Profile ids, system authority, sealed content, or credential arguments."
  exit 1
fi

echo "Agent Profile Phase 1 boundary guards passed."
