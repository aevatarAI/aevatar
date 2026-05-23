#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Refactor (iter52/issue-905-public-projection-ensure-ports):
#   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
#   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
public_abstraction_hits="$(
  rg -n "Ensure(Actor)?ProjectionAsync[[:space:]]*\\(|EnsureProjectionForActorAsync[[:space:]]*\\(" \
    src/platform/Aevatar.GAgentService.Abstractions \
    src/platform/Aevatar.GAgentService.Governance.Abstractions \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

agent_public_projection_hits="$(
  rg -n "public[[:space:]]+(sealed[[:space:]]+)?class[[:space:]]+(ChannelBotRegistrationProjectionPort|DeviceRegistrationProjectionPort|UserAgentCatalogProjectionPort|HealthProbeProjectionPort|StreamingProxyCurrentStateProjectionPort)|public[[:space:]]+Task<[^>]+>[[:space:]]+EnsureProjectionForActorAsync[[:space:]]*\\(" \
    agents/Aevatar.GAgents.Channel.Runtime \
    agents/Aevatar.GAgents.Device \
    agents/Aevatar.GAgents.Scheduled \
    agents/Aevatar.GAgents.StatusDashboard \
    agents/Aevatar.GAgents.StreamingProxy \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

command_path_hits="$(
  rg -n "EnsureProjectionAsync[[:space:]]*\\(|EnsureActorProjectionAsync[[:space:]]*\\(|EnsureProjectionForActorAsync[[:space:]]*\\(|ProjectionScopeStartRequest" \
    src/platform/Aevatar.GAgentService.Application \
    src/platform/Aevatar.GAgentService.Infrastructure \
    src/platform/Aevatar.GAgentService.Hosting \
    src/platform/Aevatar.GAgentService.Governance.Application \
    src/platform/Aevatar.GAgentService.Governance.Infrastructure \
    agents/Aevatar.GAgents.Channel.Runtime \
    agents/Aevatar.GAgents.Device \
    agents/Aevatar.GAgents.Scheduled \
    agents/Aevatar.GAgents.StatusDashboard \
    agents/Aevatar.GAgents.StreamingProxy \
    -g '*CommandTarget*.cs' \
    -g '*CommandTargetResolver*.cs' \
    -g '*CommandService*.cs' \
    -g '*CommandPort*.cs' \
    -g '*Endpoint*.cs' \
    -g '*Endpoints*.cs' \
    -g '*Query*.cs' \
    -g '*ReadPort*.cs' \
    -g '*ObservationLifecycle.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

if [[ -n "${public_abstraction_hits}${agent_public_projection_hits}${command_path_hits}" ]]; then
  if [[ -n "${public_abstraction_hits}" ]]; then
    echo "${public_abstraction_hits}"
    echo "Public GAgentService projection abstractions must not expose actorId-based Ensure* projection activation."
  fi
  if [[ -n "${agent_public_projection_hits}" ]]; then
    echo "${agent_public_projection_hits}"
    echo "Agent projection activation must be bootstrap-internal or projection-owned; do not expose public *ProjectionPort EnsureProjectionForActorAsync surfaces."
  fi
  if [[ -n "${command_path_hits}" ]]; then
    echo "${command_path_hits}"
    echo "Command/query/request paths must not activate projection scopes or construct ProjectionScopeStartRequest."
  fi
  exit 1
fi

echo "public_projection_ensure_ports_guard: ok"
