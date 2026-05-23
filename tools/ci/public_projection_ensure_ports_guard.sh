#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

# Refactor (iter52/issue-905-public-projection-ensure-ports):
#   Old pattern: Public application/agent projection ports exposed actorId-based EnsureProjection/EnsureActorProjection as general callable surface.
#   New principle: Projection activation is owned by projection bootstrap/lease/session contracts (bootstrap-internal); public application/query ports only support Attach*/Release*/Query* on existing leases.
ensure_declaration_hits="$(
  rg -n "^[[:space:]]*(public[[:space:]]+)?(async[[:space:]]+)?Task(<[^;{]+>)?[[:space:]]+Ensure(Run|Actor)?Projection(ForActor)?Async[[:space:]]*\\(" \
    src \
    agents \
    -g '*.cs' \
    -g '!**/bin/**' \
    -g '!**/obj/**' \
    || true
)"

# Explicit projection-owned/bootstrap allowlist:
# - Projection Core activation contracts and start-request model.
# - Committed-state activation plan providers, which are the authoritative
#   post-commit activation bridge.
# - Narrow internal durable materialization ports whose public contract is not
#   an application/query/agent surface.
allowed_projection_internal_regex='(^src/Aevatar.CQRS\.Projection\.Core(\.Abstractions)?/|ProjectionActivationPlanProvider\.cs:|CommittedStateProjectionActivationPlanProvider\.cs:|/Orchestration/(ScriptAuthorityProjectionPort|ScriptExecutionReadModelPort|ScriptEvolutionReadModelPort|WorkflowBindingProjectionPort|WorkflowExecutionMaterializationPort)\.cs:)'

public_abstraction_hits="$(
  if [[ -n "${ensure_declaration_hits}" ]]; then
    printf '%s\n' "${ensure_declaration_hits}" \
      | rg '/(Abstractions|Application\.Abstractions)/' \
      | rg -v "${allowed_projection_internal_regex}" \
      || true
  fi
)"

public_projection_surface_hits="$(
  if [[ -n "${ensure_declaration_hits}" ]]; then
    printf '%s\n' "${ensure_declaration_hits}" \
      | rg '(ProjectionContracts|ProjectionPort|CurrentStateProjectionPort|MaterializationPort)\.cs:' \
      | rg -v "${allowed_projection_internal_regex}" \
      || true
  fi
)"

command_path_hits="$(
  rg -n "EnsureProjectionAsync[[:space:]]*\\(|EnsureActorProjectionAsync[[:space:]]*\\(|EnsureProjectionForActorAsync[[:space:]]*\\(|ProjectionScopeStartRequest" \
    src/platform/Aevatar.GAgentService.Application \
    src/platform/Aevatar.GAgentService.Infrastructure \
    src/platform/Aevatar.GAgentService.Hosting \
    src/platform/Aevatar.GAgentService.Governance.Application \
    src/platform/Aevatar.GAgentService.Governance.Infrastructure \
    src/Aevatar.Scripting.Application \
    src/Aevatar.Scripting.Infrastructure \
    src/Aevatar.Studio.Application \
    src/Aevatar.Studio.Infrastructure \
    src/Aevatar.Studio.Projection/CommandServices \
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

if [[ -n "${public_abstraction_hits}${public_projection_surface_hits}${command_path_hits}" ]]; then
  if [[ -n "${public_abstraction_hits}" ]]; then
    echo "${public_abstraction_hits}"
    echo "Public projection abstractions must not expose actorId-based Ensure* projection activation."
  fi
  if [[ -n "${public_projection_surface_hits}" ]]; then
    echo "${public_projection_surface_hits}"
    echo "Public projection ports must not expose actorId-based Ensure* activation; use attach-existing public APIs or committed-state/bootstrap-owned activation."
  fi
  if [[ -n "${command_path_hits}" ]]; then
    echo "${command_path_hits}"
    echo "Command/query/request paths must not activate projection scopes or construct ProjectionScopeStartRequest."
  fi
  exit 1
fi

echo "public_projection_ensure_ports_guard: ok"
