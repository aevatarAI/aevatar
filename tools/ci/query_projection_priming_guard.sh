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
    agents/Aevatar.GAgents.NyxidChat/NyxIdChatStreamingRunner.cs \
    || true
)"

command_path_hits="$(
  rg -n "EnsureProjectionForActorAsync|EnsureChatProjectionAsync|EnsureSubscriptionProjectionAsync|EnsureAndAttachLeaseAsync|ActivateAsync|PrimeAsync" \
    agents/Aevatar.GAgents.Scheduled/SkillRunnerCommandPort.cs \
    agents/Aevatar.GAgents.Scheduled/UserAgentCatalogCommandPort.cs \
    agents/Aevatar.GAgents.StreamingProxy/Application/Rooms/StreamingProxyRoomCommandService.cs \
    || true
)"

if [[ -n "${hits}${endpoint_lifecycle_hits}${command_path_hits}" ]]; then
  if [[ -n "${hits}" ]]; then
    echo "${hits}"
  fi
  if [[ -n "${endpoint_lifecycle_hits}" ]]; then
    echo "${endpoint_lifecycle_hits}"
    echo "Streaming endpoints and runner must use interaction services or attach-only observation ports, not projection lifecycle APIs."
  fi
  if [[ -n "${command_path_hits}" ]]; then
    echo "${command_path_hits}"
    echo "Command ports must dispatch accepted commands; projection activation belongs to committed-state hooks, observation binders, startup activators, or background materializers."
  fi
  echo "Query/read paths must not trigger projection priming, activation, or lifecycle control."
  exit 1
fi

echo "Query projection priming guard passed."
