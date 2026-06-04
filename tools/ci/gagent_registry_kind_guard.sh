#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

violations=0

report_matches() {
  local label="$1"
  local pattern="$2"
  shift 2
  local output

  set +e
  output="$(rg -n "${pattern}" "$@" 2>/dev/null)"
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "${label}:"
    echo "${output}"
    violations=$((violations + 1))
  elif [[ ${status} -ne 1 ]]; then
    echo "gagent_registry_kind_guard: scan failed for ${label}" >&2
    exit "${status}"
  fi
}

report_filtered_matches() {
  local label="$1"
  local pattern="$2"
  local allow_pattern="$3"
  shift 3
  local output

  set +e
  output="$(rg -n "${pattern}" "$@" 2>/dev/null | rg -v "${allow_pattern}")"
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "${label}:"
    echo "${output}"
    violations=$((violations + 1))
  elif [[ ${status} -ne 1 ]]; then
    echo "gagent_registry_kind_guard: filtered scan failed for ${label}" >&2
    exit "${status}"
  fi
}

registry_identity_paths=(
  "src/platform/Aevatar.GAgentService.Abstractions/ScopeGAgents"
  "src/platform/Aevatar.GAgentService.Application/ScopeGAgents"
  "src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs"
  "src/Aevatar.Studio.Infrastructure/ActorBacked/ActorBackedGAgentRegistryPorts.cs"
  "agents/Aevatar.GAgents.Registry"
)

frontend_runtime_identity_paths=(
  "apps/aevatar-console-web/src/shared/api/runtimeGAgentApi.ts"
  "apps/aevatar-console-web/src/shared/models/runtime/gagents.ts"
  "apps/aevatar-console-web/src/shared/navigation/runtimeRoutes.ts"
  "apps/aevatar-console-web/src/pages/gagents/index.tsx"
  "apps/aevatar-console-web/src/pages/studio/index.tsx"
  "apps/aevatar-console-web/src/pages/studio/components/StudioBuildPanels.tsx"
  "apps/aevatar-console-web/src/pages/studio/components/bind/StudioMemberBindPanel.tsx"
  "apps/aevatar-console-web/src/shared/studio/api.ts"
  "apps/aevatar-console-web/src/shared/studio/models.ts"
)

report_filtered_matches \
  "Registry/admission production code must not use GAgentType/gAgentType/gagent_type identity names" \
  "GAgentType|gAgentType|gagent_type" \
  "reserved \"gagent_type\"" \
  "${registry_identity_paths[@]}"

report_matches \
  "Scope GAgent route production code must not expose gagent-types" \
  "gagent-types|/gagent-types|HandleListGAgentTypesAsync|GAgentTypeCatalogHttpResponse" \
  "src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs"

report_matches \
  "Frontend runtime identity code must not keep type-keyed catalog aliases" \
  "RuntimeGAgentTypeDescriptor|selectedGAgentTypeName|gAgentTypes|listTypes\\(|gagent-types|/gagent-types|normalizeRuntimeGAgentTypeName" \
  "${frontend_runtime_identity_paths[@]}"

report_filtered_matches \
  "Frontend runtime identity code must not send legacy GAgent identity aliases" \
  "gAgentType|gagentType|gagent_type|actorTypeName" \
  "diagnostic|Diagnostic|StudioMemberImplementationRef|actorTypeName\\?|readonly actorTypeName|string;|\"actorTypeName\"|gAgent.actorTypeName|actorTypeName:" \
  "${frontend_runtime_identity_paths[@]}"

report_filtered_matches \
  "Aevatar invocation tool must not expose actor_name outside reserved proto names and negative tests" \
  "actor_name" \
  "reserved \"actor_name\"|TryGetProperty\\(\"actor_name\".*BeFalse|\"actor_name\": \"RoleGAgent\"" \
  "src/Aevatar.AI.ToolProviders.AevatarInvocation" \
  "test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests"

report_filtered_matches \
  "Binding tool must not accept gagent_type outside explicit rejection tests" \
  "gagent_type" \
  "is not accepted|RejectsGAgentTypeAlias|\"gagent_type\":\"OrdersGAgent\"|args\\.Str\\(\"gagent_type\"\\)" \
  "src/Aevatar.AI.ToolProviders.Binding" \
  "test/Aevatar.AI.ToolProviders.Binding.Tests"

report_filtered_matches \
  "Positive tests must not use the old gagent-types route" \
  "gagent-types|/gagent-types" \
  "NotContain|Should\\(\\)\\.Be\\(StatusCodes\\.Status404NotFound\\)|returns 404|old route" \
  "test" \
  "apps/aevatar-console-web/src"

if (( violations > 0 )); then
  echo "gagent_registry_kind_guard: ${violations} violation group(s) found." >&2
  exit 1
fi

echo "gagent_registry_kind_guard: ok"
