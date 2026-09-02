#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

capture_rg_hits() {
  local label="$1"
  shift
  local output=""
  local status=0

  set +e
  output="$(rg "$@" 2>&1)"
  status=$?
  set -e

  case "${status}" in
    0)
      printf '%s' "${output}"
      ;;
    1)
      return 0
      ;;
    *)
      echo "query_projection_priming_guard: ${label} failed (rg exit ${status})." >&2
      if [[ -n "${output}" ]]; then
        printf '%s\n' "${output}" >&2
      fi
      return "${status}"
      ;;
  esac
}

capture_rg_input_hits() {
  local label="$1"
  local input="$2"
  shift 2
  local output=""
  local status=0

  set +e
  output="$(printf '%s\n' "${input}" | rg "$@" 2>&1)"
  status=$?
  set -e

  case "${status}" in
    0)
      printf '%s' "${output}"
      ;;
    1)
      return 0
      ;;
    *)
      echo "query_projection_priming_guard: ${label} failed (rg exit ${status})." >&2
      if [[ -n "${output}" ]]; then
        printf '%s\n' "${output}" >&2
      fi
      return "${status}"
      ;;
  esac
}

capture_root_rg_hits() {
  local label="$1"
  local root="$2"
  shift 2
  local output=""
  local status=0

  set +e
  output="$(cd -- "${root}" && rg "$@" 2>&1)"
  status=$?
  set -e

  case "${status}" in
    0)
      printf '%s' "${output}"
      ;;
    1)
      return 0
      ;;
    *)
      echo "query_projection_priming_guard: ${label} failed (rg exit ${status})." >&2
      if [[ -n "${output}" ]]; then
        printf '%s\n' "${output}" >&2
      fi
      return "${status}"
      ;;
  esac
}

require_source_anchor() {
  local root="$1"
  local relative_path="$2"
  local anchor="$3"
  local label="$4"
  local absolute_path="${root}/${relative_path}"

  if [[ ! -f "${absolute_path}" ]]; then
    echo "Missing ${label} anchor file: ${relative_path}" >&2
    exit 1
  fi
  if ! grep -Fq -- "${anchor}" "${absolute_path}"; then
    echo "Missing ${label} anchor '${anchor}' in ${relative_path}" >&2
    exit 1
  fi
}

hits="$(
  capture_rg_hits "query/read lifecycle scan" \
    -n "IScriptAuthorityReadModelActivationPort|IScriptAuthorityProjectionPrimingPort|IProjectionPortActivationService<|IProjectionPortReleaseService<|EnsureActorProjectionAsync|AttachLiveSinkAsync|ReleaseActorProjectionAsync|ActivateAsync|PrimeAsync" \
    src \
    -g '**/*Query*.cs' \
    -g '**/*ReadPort*.cs' \
    -g '!**/*PrimingPort*.cs' \
    -g '!**/*ActivationPort*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
)"

endpoint_lifecycle_hits="$(
  capture_rg_hits "streaming endpoint lifecycle scan" \
    -n "EnsureAndAttachLeaseAsync|EnsureChatProjectionAsync|EnsureSubscriptionProjectionAsync|INyxIdChatSessionProjectionPort" \
    agents/Aevatar.GAgents.StreamingProxy/StreamingProxyEndpoints.cs \
    agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.Streaming.cs \
    agents/Aevatar.GAgents.NyxidChat/ChannelConversationTurnRunner.cs \
)"

scope_service_script_stream_hits="$(
  capture_rg_hits "ScopeService script stream scan" \
    -n "IScriptRuntimeCommandPort|IScriptServiceAguiProjectionPort|EnsureRunProjectionAsync|EnsureAndAttachLeaseAsync|RunRuntimeAsync" \
    src/platform/Aevatar.GAgentService.Hosting/Endpoints/ScopeServiceEndpoints.cs \
)"

command_path_hits="$(
  capture_rg_hits "command path lifecycle scan" \
    -n "EnsureProjectionForActorAsync|EnsureChatProjectionAsync|EnsureSubscriptionProjectionAsync|EnsureAndAttachLeaseAsync|ActivateAsync|PrimeAsync" \
    agents/Aevatar.GAgents.Scheduled/UserAgentCatalogCommandPort.cs \
    agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs \
)"

chat_route_policy_endpoint_raw_hits="$(
  capture_rg_hits "chat route policy endpoint scan" \
    -n "ChatRoutePolicyProjectionPort|EnsureProjectionForActorAsync|ActivateAsync|PrimeAsync" \
    src/Aevatar.Mainnet.Host.Api/ChatRouting/ChatRoutePolicyAdminEndpoints.cs \
)"
chat_route_policy_endpoint_hits="$(
  capture_rg_input_hits "chat route policy allowlist filter" \
    "${chat_route_policy_endpoint_raw_hits}" \
    -v "Refactor \\(iter32/cluster-034-chat-route-policy-request-path-projection-activation\\)|Old pattern:|New principle:"
)"

identity_oauth_raw_hits="$(
  capture_rg_hits "identity OAuth lifecycle scan" \
    -n "IProjectionReadinessPort|ExternalIdentityBindingProjectionPort|AevatarOAuthClientProjectionPort|AevatarOAuthClientRebuildCoordinator|ProjectionWaitTimeout|WaitForRebuildObservedAsync|RebuildObservation|WaitForBindingStateAsync" \
    agents/Aevatar.GAgents.Channel.Identity \
    agents/Aevatar.GAgents.Channel.Identity.Abstractions \
    test/Aevatar.GAgents.ChannelRuntime.Tests/Identity \
    test/Aevatar.Capabilities.Tests/MainnetHostCompositionTests.cs \
)"
identity_oauth_hits="$(
  capture_rg_input_hits "identity OAuth allowlist filter" \
    "${identity_oauth_raw_hits}" \
    -v "Refactor \\(iter27/cluster-028-identity-oauth-endpoint\\)|Old pattern:|New principle:"
)"

schedule_port="src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs"
if [[ ! -f "${schedule_port}" ]]; then
  echo "Missing Studio schedule preflight anchor file: ${schedule_port}" >&2
  exit 1
fi
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
  capture_rg_input_hits "Studio schedule preflight forbidden scan" \
    "${schedule_preflight_body}" \
    -n "PlanWithCatalogRefreshRetryAsync|ResolveProvisioningBearerTokenAsync|_catalogRefreshPort|\\.RefreshAsync\\(|EnsureActorProjectionAsync|EnsureProjectionForActorAsync|EnsureAndAttachLeaseAsync|AttachLiveSinkAsync|ActivateAsync|PrimeAsync|ObserveAsync|WaitFor.*ObservedAsync|PollAsync"
)"

owner_llm_root="${AEVATAR_QUERY_PROJECTION_OWNER_ROOT:-${REPO_ROOT}}"
if [[ ! -d "${owner_llm_root}/src" ]]; then
  echo "Missing owner LLM source root: ${owner_llm_root}/src" >&2
  exit 1
fi

owner_query_file="src/Aevatar.Studio.Projection/QueryPorts/ProjectionScheduledInvocationAuthorityQueryPorts.cs"
planner_file="src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/ScheduledInvocationAuthorizationPlanner.cs"
contracts_file="src/platform/Aevatar.GAgentService.Abstractions/Schedules/Authorization/ScheduledInvocationAuthorizationContracts.cs"
require_source_anchor \
  "${owner_llm_root}" \
  "${owner_query_file}" \
  "public sealed class ProjectionScheduledInvocationOwnerLLMQueryPort" \
  "owner LLM query"
require_source_anchor \
  "${owner_llm_root}" \
  "${planner_file}" \
  "public sealed class ScheduledInvocationAuthorizationPlanner" \
  "schedule authorization planner"
require_source_anchor \
  "${owner_llm_root}" \
  "${contracts_file}" \
  "public interface IScheduledInvocationOwnerLLMEvidenceQueryPort" \
  "owner LLM evidence contract"

owner_llm_resolver_hits="$(
  capture_root_rg_hits "owner LLM resolver scan" \
    "${owner_llm_root}" \
    -n "StudioOwnerLLMServiceIdentityResolver|IScheduledInvocationOwnerLLMServiceIdentityResolver" \
    src \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
)"

owner_llm_live_authority_hits="$(
  # The NyxID catalog version-regression repair service is an explicit admin
  # maintenance command, not scheduled owner-LLM query/planner logic.
  capture_root_rg_hits "owner LLM live authority scan" \
    "${owner_llm_root}" \
    -n "IUserLlmCatalogPort|GetServicesAsync|IWorkflowCallerAccessTokenProvider|I[A-Za-z0-9_]*AccessTokenIssuer|Issue[A-Za-z0-9_]*Async|BearerToken" \
    src/Aevatar.Studio.Projection/QueryPorts \
    src/platform/Aevatar.GAgentService.Application/Schedules/Authorization \
    -g '*.cs' \
    -g '!src/platform/Aevatar.GAgentService.Application/Schedules/Authorization/NyxIdAuthorizationCatalogVersionRegressionRepairService.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**'
)"

nyxid_root="${AEVATAR_QUERY_PROJECTION_NYXID_ROOT:-${REPO_ROOT}}"
if [[ ! -d "${nyxid_root}/src" || ! -d "${nyxid_root}/agents" ]]; then
  echo "Missing NyxIdChat query source roots under: ${nyxid_root}" >&2
  exit 1
fi

nyxid_state_endpoint_file="agents/Aevatar.GAgents.NyxidChat/NyxIdChatEndpoints.State.cs"
nyxid_state_query_file="src/Aevatar.Studio.Infrastructure/ActorBacked/ProjectionNyxIdChatConversationStateQueryPort.cs"
nyxid_state_contract_file="src/Aevatar.Studio.Application.Abstractions/Studio/Abstractions/INyxIdChatConversationStateQueryPort.cs"
require_source_anchor \
  "${nyxid_root}" \
  "${nyxid_state_endpoint_file}" \
  "HandleGetStateAsync" \
  "NyxIdChat state endpoint"
require_source_anchor \
  "${nyxid_root}" \
  "${nyxid_state_query_file}" \
  "ProjectionNyxIdChatConversationStateQueryPort" \
  "NyxIdChat state query"
require_source_anchor \
  "${nyxid_root}" \
  "${nyxid_state_contract_file}" \
  "INyxIdChatConversationStateQueryPort" \
  "NyxIdChat state query contract"

nyxid_state_query_authority_hits="$(
  capture_root_rg_hits "NyxIdChat state query authority scan" \
    "${nyxid_root}" \
    -n "IActorRuntime|IEventStore" \
    "${nyxid_state_endpoint_file}" \
    "${nyxid_state_query_file}" \
    "${nyxid_state_contract_file}"
)"

nyxid_state_query_lifecycle_hits="$(
  capture_root_rg_hits "NyxIdChat state query lifecycle scan" \
    "${nyxid_root}" \
    -n "Ensure[A-Za-z0-9_]*ProjectionAsync|EnsureAndAttachLeaseAsync|AttachLiveSinkAsync|ReleaseActorProjectionAsync|ActivateAsync|PrimeAsync|ReplayAsync|ReplayEventsAsync|RebuildAsync|BackfillAsync" \
    "${nyxid_state_endpoint_file}" \
    "${nyxid_state_query_file}" \
    "${nyxid_state_contract_file}"
)"

schedule_preflight_contract_error=""
schedule_preflight_planner_hits="$(
  capture_rg_input_hits "Studio schedule preflight planner anchor scan" \
    "${schedule_preflight_body}" \
    -n "_authorizationPlanner\\.PlanAsync\\("
)"
if [[ -z "${schedule_preflight_body}" ]]; then
  schedule_preflight_contract_error="Studio schedule PreflightAsync was not found in ${schedule_port}."
elif [[ -z "${schedule_preflight_planner_hits}" ]]; then
  schedule_preflight_contract_error="Studio schedule PreflightAsync must query the authorization planner directly."
fi

if [[ -n "${hits}${endpoint_lifecycle_hits}${scope_service_script_stream_hits}${command_path_hits}${chat_route_policy_endpoint_hits}${identity_oauth_hits}${schedule_preflight_hits}${owner_llm_resolver_hits}${owner_llm_live_authority_hits}${nyxid_state_query_authority_hits}${nyxid_state_query_lifecycle_hits}${schedule_preflight_contract_error}" ]]; then
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
  if [[ -n "${nyxid_state_query_authority_hits}" ]]; then
    echo "${nyxid_state_query_authority_hits}"
    echo "NyxIdChat state endpoint/query contracts must read the projection document only; IActorRuntime and IEventStore are forbidden."
  fi
  if [[ -n "${nyxid_state_query_lifecycle_hits}" ]]; then
    echo "${nyxid_state_query_lifecycle_hits}"
    echo "NyxIdChat state query calls must not attach, prime, activate, replay, rebuild, or backfill projection state."
  fi
  if [[ -n "${schedule_preflight_contract_error}" ]]; then
    echo "${schedule_preflight_contract_error}"
  fi
  echo "Query/read paths must not trigger projection priming, activation, or lifecycle control."
  exit 1
fi

echo "Query projection priming guard passed."
