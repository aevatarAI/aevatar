#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ZERO_SHA="0000000000000000000000000000000000000000"
DIFF_MODE="worktree"
DIFF_RANGE_VALUE="${DIFF_RANGE:-}"

if [[ -n "${DIFF_RANGE_VALUE}" ]]; then
  DIFF_MODE="range"
elif [[ "${GITHUB_EVENT_NAME:-}" == "pull_request" && -n "${GITHUB_BASE_REF:-}" ]]; then
  git fetch --no-tags --depth=1 origin "${GITHUB_BASE_REF}"
  DIFF_RANGE_VALUE="origin/${GITHUB_BASE_REF}...HEAD"
  DIFF_MODE="range"
elif [[ -n "${GITHUB_EVENT_BEFORE:-}" && "${GITHUB_EVENT_BEFORE}" != "${ZERO_SHA}" && -n "${GITHUB_SHA:-}" ]]; then
  DIFF_RANGE_VALUE="${GITHUB_EVENT_BEFORE}...${GITHUB_SHA}"
  DIFF_MODE="range"
fi

if [[ "${DIFF_MODE}" == "range" ]]; then
  echo "Architecture guards diff mode: range (${DIFF_RANGE_VALUE})"
else
  echo "Architecture guards diff mode: worktree (HEAD vs working tree)"
fi

if [ -d "src/Aevatar.Host.Api" ] || [ -d "src/Aevatar.Host.Gateway" ]; then
  echo "Legacy host projects are forbidden. Use subsystem hosts only."
  exit 1
fi

if rg -n "Aevatar\.Host\.Api|Aevatar\.Host\.Gateway" aevatar.slnx; then
  echo "Solution must not include legacy host projects."
  exit 1
fi

if rg -n "docs\\\\SOLUTION_AUDIT_REPORT_" aevatar.slnx; then
  echo "Working audit documents must not be added to solution."
  exit 1
fi

if rg -n "docs[\\\\/]agents-working-space[\\\\/]" aevatar.slnx; then
  echo "Working documents under docs/agents-working-space must not be added to solution."
  exit 1
fi

if rg -n "GetAwaiter\(\)\.GetResult\(\)" src; then
  echo "Found sync-over-async usage."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/DefaultAutoPersistedStateEventFactory.cs" ]; then
  echo "DefaultAutoPersistedStateEventFactory is forbidden. EventStore must persist domain events, not snapshot-state events."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/IAutoPersistedStateEventFactory.cs" ]; then
  echo "IAutoPersistedStateEventFactory is forbidden. Use IDomainEventDeriver<TState> for semantic event derivation."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/EventSourcingAutoPersistenceOptions.cs" ]; then
  echo "EventSourcingAutoPersistenceOptions is forbidden. Stateful actors must emit explicit domain events."
  exit 1
fi

if [ -f "src/Aevatar.Foundation.Core/EventSourcing/IDomainEventDeriver.cs" ]; then
  echo "IDomainEventDeriver is forbidden on runtime path. Domain events must be produced explicitly by command handlers."
  exit 1
fi

if rg -n "ConfirmStateAsync\(" src/Aevatar.Foundation.Core/EventSourcing; then
  echo "ConfirmStateAsync is forbidden."
  exit 1
fi

if rg -n "ConfirmDerivedEventsAsync\(" src/Aevatar.Foundation.Core/EventSourcing; then
  echo "ConfirmDerivedEventsAsync is forbidden. Domain events must be raised explicitly via RaiseEvent + ConfirmEventsAsync."
  exit 1
fi

if rg -n "TryUnpack<TState>|Unpack<TState>" src/Aevatar.Foundation.Core/EventSourcing/EventSourcingBehavior.cs; then
  echo "EventSourcingBehavior must not unpack TState snapshots from persisted events."
  exit 1
fi

if rg -n "StateStore\.LoadAsync|StateStore\.SaveAsync" src/Aevatar.Foundation.Core/GAgentBase.TState.cs; then
  echo "GAgentBase<TState> must not use StateStore as the fact source. Recovery must come from EventStore replay."
  exit 1
fi

if rg -n "IEventSourcingBehavior<>\\)\\.MakeGenericType|EventSourcingBehavior<>\\)\\.MakeGenericType|GetProperty\\(\"EventSourcing\"\\)|GetProperty\\(\"StateStore\"\\)" \
  src/Aevatar.Foundation.Runtime \
  src/Aevatar.Foundation.Runtime.Implementations.Orleans
then
  echo "Runtime must not use reflection-based stateful ES binding. Use static generic construction in GAgentBase<TState>."
  exit 1
fi

if rg -n "TypeUrl\.Contains|typeUrl\.Contains\(" src demos; then
  echo "Found string-based event type matching."
  exit 1
fi

if rg -n "<MassTransitVersion>9\." Directory.Packages.props; then
  echo "MassTransit v9 is forbidden in this repository. Keep MassTransitVersion on v8.x."
  exit 1
fi

mass_transit_v9_refs="$(
  rg -n "<Package(Reference|Version) Include=\"MassTransit(\.[^\"]*)?\" Version=\"9\." \
    Directory.Packages.props src test demos tools \
    -g 'Directory.Packages.props' -g '*.csproj' || true
)"

if [ -n "${mass_transit_v9_refs}" ]; then
  echo "${mass_transit_v9_refs}"
  echo "MassTransit package references must stay on v8.x. v9 references are forbidden."
  exit 1
fi

reducer_test_coverage_violations=""
reducer_test_scan_roots=(
  "src/Aevatar.AI.Projection/Reducers"
  "src/workflow/Aevatar.Workflow.Projection/Reducers"
)

for root in "${reducer_test_scan_roots[@]}"; do
  if [ ! -d "${root}" ]; then
    continue
  fi

  while IFS= read -r file; do
    while IFS= read -r class_line; do
      if [ -z "${class_line}" ]; then
        continue
      fi

      line_no="$(echo "${class_line}" | cut -d: -f1)"
      decl="$(echo "${class_line}" | cut -d: -f2-)"

      if echo "${decl}" | rg -q "\babstract\s+class\b"; then
        continue
      fi

      class_name="$(echo "${decl}" | sed -E 's/.*class[[:space:]]+([A-Za-z0-9_]+).*/\1/')"
      if [ -z "${class_name}" ]; then
        continue
      fi

      if ! rg -n "\b${class_name}\b" test -g '*Tests.cs' >/dev/null; then
        reducer_test_coverage_violations="${reducer_test_coverage_violations}${file}:${line_no}:${class_name}\n"
      fi
    done < <(rg -n "^\s*(public|internal)\s+(sealed\s+)?(abstract\s+)?class\s+[A-Za-z0-9_]*Reducer[A-Za-z0-9_]*\b" "${file}" || true)
  done < <(rg --files "${root}" -g '*Reducer*.cs')
done

if [ -n "${reducer_test_coverage_violations}" ]; then
  printf '%b' "${reducer_test_coverage_violations}"
  echo "Projection reducer coverage guard failed: each non-abstract reducer class must be referenced by tests."
  exit 1
fi

echo "Running projection route-mapping guard..."
bash tools/ci/projection_route_mapping_guard.sh

if rg -n "Aevatar\.AI\.Core\.csproj" src/workflow/Aevatar.Workflow.Core/Aevatar.Workflow.Core.csproj; then
  echo "Workflows.Core must not reference AI.Core."
  exit 1
fi

if rg -n "Aevatar\.AI\.(Abstractions|Core|LLMProviders\.MEAI|LLMProviders\.Tornado|ToolProviders\.MCP|ToolProviders\.Skills)\.csproj" \
  src/Aevatar.Bootstrap/Aevatar.Bootstrap.csproj
then
  echo "Aevatar.Bootstrap must not directly reference AI implementations. Use Bootstrap.Extensions.AI composition package."
  exit 1
fi

if rg -n "Aevatar\.AI\.Projection\.csproj" src/workflow/Aevatar.Workflow.Projection/Aevatar.Workflow.Projection.csproj; then
  echo "Workflow.Projection must not directly reference AI.Projection. Use workflow extension composition."
  exit 1
fi

if rg -n "AddAIDefaultProjectionLayer<" src/workflow/Aevatar.Workflow.Projection/DependencyInjection/ServiceCollectionExtensions.cs; then
  echo "Workflow.Projection must not directly register AI default projection layer."
  exit 1
fi

workflow_to_maker_violations="$(
  rg -n "Aevatar\.Maker\..*\.csproj" src/workflow -g '*.csproj' || true
)"

if [ -n "${workflow_to_maker_violations}" ]; then
  echo "${workflow_to_maker_violations}"
  echo "Workflow projects must not reference Maker projects."
  exit 1
fi

for legacy_maker_project in \
  src/maker/Aevatar.Maker.Application.Abstractions/Aevatar.Maker.Application.Abstractions.csproj \
  src/maker/Aevatar.Maker.Application/Aevatar.Maker.Application.csproj \
  src/maker/Aevatar.Maker.Infrastructure/Aevatar.Maker.Infrastructure.csproj \
  src/maker/Aevatar.Maker.Host.Api/Aevatar.Maker.Host.Api.csproj \
  src/maker/Aevatar.Maker.Core/Aevatar.Maker.Core.csproj
do
  if [ -f "${legacy_maker_project}" ]; then
    echo "Legacy Maker capability project must be removed: ${legacy_maker_project}"
    exit 1
  fi
done

if rg -n "AddMakerCapability\(" src -g '*.cs'; then
  echo "AddMakerCapability is forbidden. Maker must be wired as workflow extension plugin."
  exit 1
fi

if rg -n "MapMakerCapabilityEndpoints|/api/maker" src -g '*.cs'; then
  echo "Maker standalone capability endpoints are forbidden."
  exit 1
fi

if ! rg -n "AddWorkflowMakerExtensions\(" src/Aevatar.Mainnet.Host.Api/Program.cs >/dev/null; then
  echo "Mainnet host must register workflow maker extensions via AddWorkflowMakerExtensions()."
  exit 1
fi

if ! rg -n "AddWorkflowCapabilityWithAIDefaults\(" src/Aevatar.Mainnet.Host.Api/Program.cs >/dev/null; then
  echo "Mainnet host must register workflow capability + AI defaults via AddWorkflowCapabilityWithAIDefaults()."
  exit 1
fi

if ! rg -n "AddWorkflowCapabilityWithAIDefaults\(" src/workflow/Aevatar.Workflow.Host.Api/Program.cs >/dev/null; then
  echo "Workflow host must register workflow capability + AI defaults via AddWorkflowCapabilityWithAIDefaults()."
  exit 1
fi

if [ -f "src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/MakerModuleFactory.cs" ]; then
  echo "Maker extension must use unified module pack model; MakerModuleFactory is forbidden."
  exit 1
fi

if rg -n "IEventModuleFactory" src/workflow/extensions/Aevatar.Workflow.Extensions.Maker -g '*.cs'; then
  echo "Maker extension must not register standalone IEventModuleFactory. Use IWorkflowModulePack."
  exit 1
fi

if ! rg -n "AddWorkflowModulePack<MakerModulePack>\(" src/workflow/extensions/Aevatar.Workflow.Extensions.Maker/ServiceCollectionExtensions.cs >/dev/null; then
  echo "Maker extension must register MakerModulePack via AddWorkflowModulePack<MakerModulePack>()."
  exit 1
fi

for host_program in \
  src/Aevatar.Mainnet.Host.Api/Program.cs \
  src/workflow/Aevatar.Workflow.Host.Api/Program.cs
do
  if ! rg -n "AddAevatarDefaultHost\(" "${host_program}" >/dev/null; then
    echo "Missing AddAevatarDefaultHost in ${host_program}"
    exit 1
  fi

  if ! rg -n "UseAevatarDefaultHost\(" "${host_program}" >/dev/null; then
    echo "Missing UseAevatarDefaultHost in ${host_program}"
    exit 1
  fi
done

if rg -n "AddCqrsCore\(" \
  src/Aevatar.Mainnet.Host.Api \
  src/workflow/Aevatar.Workflow.Host.Api \
  src/workflow/Aevatar.Workflow.Infrastructure
then
  echo "Direct AddCqrsCore wiring in hosts/infrastructure is forbidden. Use Aevatar.CQRS.Runtime.Hosting."
  exit 1
fi

if rg -n "TryGetContext\(" src; then
  echo "Projection context reverse lookup is forbidden. Use explicit projection lease/session handles."
  exit 1
fi

if rg -n "SemaphoreSlim" src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs; then
  echo "WorkflowExecutionProjectionService must not use process-local SemaphoreSlim for projection start arbitration."
  exit 1
fi

if rg -n "Dictionary<|ConcurrentDictionary<" src/Aevatar.CQRS.Projection.Core/Orchestration/ProjectionSubscriptionRegistry.cs; then
  echo "ProjectionSubscriptionRegistry must not keep in-memory dictionary state."
  exit 1
fi

if rg -n "Task\s+AttachLiveSinkAsync\(\s*string\s+actorId|Task\s+DetachLiveSinkAsync\(\s*string\s+actorId|Task\s+ReleaseActorProjectionAsync\(\s*string\s+actorId" \
  src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs
then
  echo "Workflow projection port must use lease/session handles instead of actorId context lookup."
  exit 1
fi

if ! rg -n "IWorkflowExecutionProjectionLease" \
  src/workflow/Aevatar.Workflow.Application.Abstractions/Projections/IWorkflowExecutionProjectionPort.cs >/dev/null
then
  echo "Workflow projection port must depend on IWorkflowExecutionProjectionLease."
  exit 1
fi

id_mapping_state_pattern='^\s*(private|protected|internal)\s+((readonly|static)\s+)*(ConcurrentDictionary|Dictionary|HashSet|Queue)\s*<[^>]+>\s+_[A-Za-z0-9_]*(actor|entity|run|session)[A-Za-z0-9_]*\b'
id_mapping_state_hits=""

id_mapping_scan_roots=(
  "src/Aevatar.CQRS.Projection.Core"
  "src/Aevatar.Foundation.Projection"
  "src/Aevatar.AI.Projection"
  "src/workflow/Aevatar.Workflow.Projection"
  "src/workflow/Aevatar.Workflow.Application"
)

scan_args=()
for root in "${id_mapping_scan_roots[@]}"; do
  if [ -d "${root}" ]; then
    scan_args+=("${root}")
  fi
done

if [ "${#scan_args[@]}" -gt 0 ]; then
  while IFS= read -r file; do
    if [ -z "${file}" ]; then
      continue
    fi

    case "${file}" in
      *InMemory*|*inmemory*|*Tests.cs|*.g.cs)
        continue
        ;;
    esac

    hits="$(rg -n -i "${id_mapping_state_pattern}" "${file}" || true)"
    if [ -n "${hits}" ]; then
      id_mapping_state_hits="${id_mapping_state_hits}
${file}
${hits}
"
    fi
  done < <(rg --files "${scan_args[@]}" -g '*.cs')
fi

if [ -n "${id_mapping_state_hits}" ]; then
  echo "${id_mapping_state_hits}"
  echo "Full-scan violation: middle-layer actor/entity/run/session ID-mapping in-memory dictionary state is forbidden. Use actorized orchestration, lease/session handles, or distributed state abstractions."
  exit 1
fi

runtime_relay_hits="$(
  rg -n "ListBySourceAsync\(|StreamForwardingRules\.TryBuildForwardedEnvelope\(" \
    src/Aevatar.Foundation.Runtime/Actor \
    src/Aevatar.Foundation.Runtime.Implementations.Orleans/Actors \
    -g '*.cs' || true
)"

if [ -n "${runtime_relay_hits}" ]; then
  echo "${runtime_relay_hits}"
  echo "Runtime actor layer must not execute relay graph traversal. Relay execution must stay in stream/message-queue infrastructure."
  exit 1
fi

implementation_ref_violations="$(
  rg -n "Aevatar\.CQRS\.Runtime\.Implementations\.(Wolverine|MassTransit)" src -g '*.csproj' \
  | rg -v "^src/Aevatar.CQRS.Runtime.Hosting/" \
  | rg -v "^src/Aevatar.CQRS.Runtime.Implementations.Wolverine/" \
  | rg -v "^src/Aevatar.CQRS.Runtime.Implementations.MassTransit/" \
  || true
)"

if [ -n "${implementation_ref_violations}" ]; then
  echo "${implementation_ref_violations}"
  echo "Only Runtime.Hosting may reference Runtime.Implementations.* directly."
  exit 1
fi

command_side_readmodel_violations="$(
  rg -n "IProjectionReadModelStore<|ReadModelStore" \
    src/workflow/Aevatar.Workflow.Application \
    src/workflow/Aevatar.Workflow.Host.Api \
    src/Aevatar.Mainnet.Host.Api \
    -g '*.cs' || true
)"

if [ -n "${command_side_readmodel_violations}" ]; then
  echo "${command_side_readmodel_violations}"
  echo "Command-side services/endpoints must not depend on projection read-model stores directly."
  exit 1
fi

check_orchestration_class_guard() {
  local file_path="$1"
  local max_non_empty_lines="$2"
  local max_private_dependencies="$3"

  if [ ! -f "${file_path}" ]; then
    return 0
  fi

  local non_empty_lines
  non_empty_lines="$(awk 'NF && $1 !~ /^\/\// {count++} END {print count + 0}' "${file_path}")"
  if [ "${non_empty_lines}" -gt "${max_non_empty_lines}" ]; then
    echo "${file_path}: ${non_empty_lines} non-empty lines exceeds limit ${max_non_empty_lines}."
    echo "Orchestration classes must stay thin. Extract strategy/resource details into dedicated components."
    exit 1
  fi

  local dependency_count
  dependency_count="$(rg -n "^\s*private readonly " "${file_path}" | wc -l | tr -d '[:space:]')"
  if [ "${dependency_count}" -gt "${max_private_dependencies}" ]; then
    echo "${file_path}: ${dependency_count} private dependencies exceeds limit ${max_private_dependencies}."
    echo "Orchestration classes must not accumulate too many direct collaborators."
    exit 1
  fi
}

check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Application/Runs/WorkflowChatRunApplicationService.cs" \
  60 \
  4
check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Application/Runs/WorkflowRunExecutionEngine.cs" \
  120 \
  6
check_orchestration_class_guard \
  "src/workflow/Aevatar.Workflow.Projection/Orchestration/WorkflowExecutionProjectionService.cs" \
  190 \
  10

echo "Architecture guards passed."
