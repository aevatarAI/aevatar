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
    agents/Aevatar.GAgents.Scheduled/SkillRunnerCommandPort.cs \
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

if [[ -n "${hits}${endpoint_lifecycle_hits}${scope_service_script_stream_hits}${command_path_hits}${chat_route_policy_endpoint_hits}${identity_oauth_hits}" ]]; then
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
  echo "Query/read paths must not trigger projection priming, activation, or lifecycle control."
  exit 1
fi

echo "Query projection priming guard passed."
