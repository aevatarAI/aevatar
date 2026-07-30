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

bind_identity_paths=(
  "src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs"
  "src/platform/Aevatar.GAgentService.Abstractions/ScopeBindings/ScopeBindingModels.cs"
  "src/platform/Aevatar.GAgentService.Application/Bindings/ScopeBindingCommandApplicationService.cs"
  "src/Aevatar.Studio.Application.Abstractions/Studio/Contracts/MemberContracts.cs"
  "src/Aevatar.Studio.Application/Studio/Services/StudioMemberService.cs"
  "src/Aevatar.Studio.Projection/CommandServices/ActorDispatchStudioMemberCommandService.cs"
  "src/Aevatar.Studio.Projection/CommandServices/ScopeBindingStudioMemberPlatformBindingCommandService.cs"
)

report_filtered_matches \
  "Registry/admission production code must not use GAgentType/gAgentType/gagent_type identity names" \
  "GAgentType|gAgentType|gagent_type" \
  "reserved \"gagent_type\"" \
  "${registry_identity_paths[@]}"

report_matches \
  "Scope GAgent catalog production code must not reintroduce type-keyed catalog models" \
  "HandleListGAgentTypesAsync|GAgentTypeCatalogHttpResponse" \
  "src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeGAgentEndpoints.cs"

report_matches \
  "Frontend runtime identity code must not keep type-keyed catalog aliases" \
  "RuntimeGAgentTypeDescriptor|selectedGAgentTypeName|gAgentTypes|listTypes\\(|normalizeRuntimeGAgentTypeName" \
  "${frontend_runtime_identity_paths[@]}"

report_filtered_matches \
  "Frontend runtime identity code must not send legacy GAgent identity aliases" \
  "gAgentType|gagentType|gagent_type|actorTypeName" \
  "diagnostic[A-Za-z]*TypeName|Diagnostic[A-Za-z]*TypeName|staticActorTypeName|StaticActorTypeName|StudioMemberImplementationRef\\.diagnosticActorTypeName" \
  "${frontend_runtime_identity_paths[@]}"

report_filtered_matches \
  "GAgent bind request and command paths must not accept caller actorTypeName aliases" \
  "ActorTypeName|actorTypeName|gAgentType|gagentType|gagent_type" \
  "LEGACY_ACTOR_TYPE_NAME_REJECTED|HasLegacyActorTypeName|gagent\\.actorTypeName is not accepted|string\\.Equals\\(key, \"actorTypeName\"|string\\.Equals\\(key, \"ActorTypeName\"|ActorTypeName = diagnosticClrTypeName|StaticSpec\\.ActorTypeName|staticSpec\\.ActorTypeName|Implementation\\?\\.Static\\?\\.ActorTypeName|DiagnosticClrTypeName|DiagnosticActorTypeName|diagnosticActorTypeName|StaticActorTypeName|result\\.GAgent\\?\\.DiagnosticClrTypeName" \
  "${bind_identity_paths[@]}"

set +e
studio_member_gagent_binding_alias="$(
  awk '
    /message StudioMemberGAgentBindingRequest/ { in_message = 1 }
    in_message { print FILENAME ":" FNR ":" $0 }
    in_message && /^}/ { in_message = 0 }
  ' agents/Aevatar.GAgents.StudioMember/studio_member_messages.proto | rg "string .*actor_type_name|optional .*actor_type_name"
)"
studio_member_gagent_binding_alias_status=$?
set -e
if [[ ${studio_member_gagent_binding_alias_status} -eq 0 ]]; then
  echo "Studio member GAgent binding request proto must not accept actor_type_name:"
  echo "${studio_member_gagent_binding_alias}"
  violations=$((violations + 1))
elif [[ ${studio_member_gagent_binding_alias_status} -ne 1 ]]; then
  echo "gagent_registry_kind_guard: scan failed for Studio member GAgent binding request proto" >&2
  exit "${studio_member_gagent_binding_alias_status}"
fi

report_filtered_matches \
  "Aevatar invocation tool must not expose actor_name outside reserved proto names and negative tests" \
  "actor_name" \
  "reserved \"actor_name\"|TryGetProperty\\(\"actor_name\".*BeFalse|\"actor_name\": \"RoleGAgent\"|\"actor_name\": \"LegacyRoleGAgent\"|DeletedGAgentActorNameAlias = \"actor_name\"|Contain\\(\"actor_name\"\\)" \
  "src/Aevatar.AI.ToolProviders.AevatarInvocation" \
  "test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests"

if ! rg -n "InvokeGAgent_ShouldRejectActorNameAliasEvenWhenPairedWithValidSelector" \
    test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/AevatarInvocationToolSourceTests.cs >/dev/null ||
   ! rg -n '\[InlineData\("agent_kind", "RoleGAgent"\)\]' \
    test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/AevatarInvocationToolSourceTests.cs >/dev/null ||
   ! rg -n '\[InlineData\("actor_id", "actor-1"\)\]' \
    test/Aevatar.AI.ToolProviders.AevatarInvocation.Tests/AevatarInvocationToolSourceTests.cs >/dev/null; then
  echo "Aevatar invocation actor_name rejection must cover paired agent_kind and actor_id selectors."
  violations=$((violations + 1))
fi

report_filtered_matches \
  "Binding tool must not accept gagent_type outside explicit rejection tests" \
  "gagent_type" \
  "is not accepted|RejectsGAgentTypeAlias|\"gagent_type\":\"OrdersGAgent\"|args\\.Str\\(\"gagent_type\"\\)" \
  "src/Aevatar.AI.ToolProviders.Binding" \
  "test/Aevatar.AI.ToolProviders.Binding.Tests"

report_filtered_matches \
  "Tests must not treat gagent-types as a legacy type-keyed route" \
  "GAgentTypeCatalog|gagent type|old gagent-types|old /api/scopes/gagent-types" \
  "NotContain" \
  "test" \
  "apps/aevatar-console-web/src"

if (( violations > 0 )); then
  echo "gagent_registry_kind_guard: ${violations} violation group(s) found." >&2
  exit 1
fi

echo "gagent_registry_kind_guard: ok"
