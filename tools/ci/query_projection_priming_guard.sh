#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

hits="$(
  rg -n "IScriptAuthorityReadModelActivationPort|IScriptAuthorityProjectionPrimingPort|IProjectionPortActivationService<|IProjectionPortReleaseService<|EnsureActorProjectionAsync|AttachLiveSinkAsync|ReleaseActorProjectionAsync|ActivateAsync|PrimeAsync" \
    src \
    -g '**/*Query*.cs' \
    -g '**/*ReadPort*.cs' \
    -g '!**/*PrimingPort*.cs' \
    -g '!**/*ActivationPort*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

endpoint_lifecycle_hits="$(
  rg -n "EnsureAndAttachLeaseAsync|EnsureChatProjectionAsync|EnsureSubscriptionProjectionAsync|INyxIdChatSessionProjectionPort" \
    agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs \
    agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs \
    agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs \
    || true
)"

scope_service_script_stream_hits="$(
  rg -n "IScriptRuntimeCommandPort|IScriptServiceAguiProjectionPort|EnsureRunProjectionAsync|EnsureAndAttachLeaseAsync|RunRuntimeAsync" \
    src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs \
    || true
)"

command_path_hits="$(
  rg -n "EnsureProjectionForActorAsync|EnsureChatProjectionAsync|EnsureSubscriptionProjectionAsync|EnsureAndAttachLeaseAsync|ActivateAsync|PrimeAsync" \
    agents/Aevatar.GAgents.Scheduled/UserAgentCatalogCommandPort.cs \
    agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs \
    || true
)"

chat_route_policy_endpoint_hits="$(
  rg -n "ChatRoutePolicyProjectionPort|EnsureProjectionForActorAsync|ActivateAsync|PrimeAsync" \
    src/Aevatar.Mainnet.Host.Api/ChatRouting/ChatRoutePolicyAdminEndpoints.cs \
    | rg -v "Refactor \\(iter32/cluster-034-chat-route-policy-request-path-projection-activation\\)|Old pattern:|New principle:" \
    || true
)"

identity_oauth_hits="$(
  rg -n "IProjectionReadinessPort|ExternalIdentityBindingProjectionPort|AevatarOAuthClientProjectionPort|AevatarOAuthClientRebuildCoordinator|ProjectionWaitTimeout|WaitForRebuildObservedAsync|RebuildObservation|WaitForBindingStateAsync" \
    agents/Aevatar.GAgents.Channel.Identity \
    agents/Aevatar.GAgents.Channel.Identity.Abstractions \
    test/Aevatar.GAgents.ChannelRuntime.Tests/Identity \
    test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs \
    | rg -v "Refactor \\(iter27/cluster-028-identity-oauth-endpoint\\)|Old pattern:|New principle:" \
    || true
)"

schedule_port="src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs"
schedule_preflight_body="$(
  awk '
    /public async Task<StudioMemberWorkflowAuthorizationResult> PreflightAsync\(/ {
      capture = 1
    }
    capture {
      print
      opens = gsub(/\{/, "{")
      closes = gsub(/\}/, "}")
      depth += opens - closes
      if (opens > 0) {
        opened = 1
      }
      if (opened && depth == 0) {
        exit
      }
    }
  ' "${schedule_port}"
)"

schedule_preflight_hits="$(
  printf '%s\n' "${schedule_preflight_body}" \
    | rg -n "PlanWithCatalogRefreshRetryAsync|ResolveProvisioningBearerTokenAsync|_catalogRefreshPort|\\.RefreshAsync\\(|EnsureActorProjectionAsync|EnsureProjectionForActorAsync|EnsureAndAttachLeaseAsync|AttachLiveSinkAsync|ActivateAsync|PrimeAsync|ObserveAsync|WaitFor.*ObservedAsync|PollAsync" \
    || true
)"

owner_llm_resolver_hits="$(
  rg -n "StudioOwnerLLMServiceIdentityResolver|IScheduledInvocationOwnerLLMServiceIdentityResolver" \
    src \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

owner_llm_live_authority_hits="$(
  rg -n "IUserLlmCatalogPort|GetServicesAsync|IWorkflowCallerAccessTokenProvider|Issue[A-Za-z0-9_]*Async|BearerToken" \
    src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs \
    src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs \
    || true
)"

schedule_preflight_contract_error=""
if [[ -z "${schedule_preflight_body}" ]]; then
  schedule_preflight_contract_error="Studio schedule PreflightAsync was not found in ${schedule_port}."
elif ! printf '%s\n' "${schedule_preflight_body}" | rg -q "_authorizationPlanner\\.PlanAsync\\("; then
  schedule_preflight_contract_error="Studio schedule PreflightAsync must query the authorization planner directly."
fi

if [[ -n "${hits}${endpoint_lifecycle_hits}${scope_service_script_stream_hits}${command_path_hits}${chat_route_policy_endpoint_hits}${identity_oauth_hits}${schedule_preflight_hits}${owner_llm_resolver_hits}${owner_llm_live_authority_hits}${schedule_preflight_contract_error}" ]]; then
  if [[ -n "${hits}" ]]; then
    echo "${hits}"
  fi
  if [[ -n "${endpoint_lifecycle_hits}" ]]; then
    echo "${endpoint_lifecycle_hits}"
    echo "Streaming endpoints and runner must use interaction services or attach-only observation ports, not projection lifecycle APIs."
  fi
  if [[ -n "${scope_service_script_stream_hits}" ]]; then
    echo "${scope_service_script_stream_hits}"
    echo "ScopeService scripting stream endpoints must use ScriptServiceRunCommand interaction, not inline runtime/projection orchestration."
  fi
  if [[ -n "${command_path_hits}" ]]; then
    echo "${command_path_hits}"
    echo "Command ports must dispatch accepted commands; projection activation belongs to committed-state hooks, observation binders, startup activators, or background materializers."
  fi
  if [[ -n "${chat_route_policy_endpoint_hits}" ]]; then
    echo "${chat_route_policy_endpoint_hits}"
    echo "Chat route policy endpoints/bootstrap must not activate projection lifecycle in request paths; committed-state hooks own projection activation."
  fi
  if [[ -n "${identity_oauth_hits}" ]]; then
    echo "${identity_oauth_hits}"
    echo "Identity OAuth endpoints/bootstrap must use typed CQRS dispatch and accepted/pending ACKs, not projection readiness, rebuild observation, or readmodel polling."
  fi
  if [[ -n "${schedule_preflight_hits}" ]]; then
    echo "${schedule_preflight_hits}"
    echo "Studio schedule PreflightAsync must not refresh catalogs, issue credentials, observe/poll materialization, or invoke projection lifecycle helpers."
  fi
  if [[ -n "${owner_llm_resolver_hits}" ]]; then
    echo "${owner_llm_resolver_hits}"
    echo "Scheduled owner LLM identity must come from the projected user-config document; the live identity resolver must not return."
  fi
  if [[ -n "${owner_llm_live_authority_hits}" ]]; then
    echo "${owner_llm_live_authority_hits}"
    echo "Scheduled owner LLM query/planner paths must not call live LLM catalogs or issue access tokens."
  fi
  if [[ -n "${schedule_preflight_contract_error}" ]]; then
    echo "${schedule_preflight_contract_error}"
  fi
  echo "Query/read paths must not trigger projection priming, activation, or lifecycle control."
  exit 1
fi

echo "Query projection priming guard passed."
