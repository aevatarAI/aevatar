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

if rg -n "TypeUrl\.Contains|typeUrl\.Contains\(" src demos; then
  echo "Found string-based event type matching."
  exit 1
fi

if rg -n "Aevatar\.AI\.Core\.csproj" src/workflow/Aevatar.Workflow.Core/Aevatar.Workflow.Core.csproj; then
  echo "Workflows.Core must not reference AI.Core."
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

maker_to_workflow_host_violations="$(
  rg -n "Aevatar\.Workflow\.Host\.Api\.csproj" src/maker -g '*.csproj' || true
)"

if [ -n "${maker_to_workflow_host_violations}" ]; then
  echo "${maker_to_workflow_host_violations}"
  echo "Maker projects must not reference Workflow.Host.Api directly."
  exit 1
fi

for host_program in \
  src/Aevatar.Mainnet.Host.Api/Program.cs \
  src/workflow/Aevatar.Workflow.Host.Api/Program.cs \
  src/maker/Aevatar.Maker.Host.Api/Program.cs
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
  src/maker/Aevatar.Maker.Host.Api \
  src/workflow/Aevatar.Workflow.Infrastructure \
  src/maker/Aevatar.Maker.Infrastructure
then
  echo "Direct AddCqrsCore wiring in hosts/infrastructure is forbidden. Use Aevatar.CQRS.Runtime.Hosting."
  exit 1
fi

if rg -n "TryGetContext\(" src; then
  echo "Projection context reverse lookup is forbidden. Use explicit projection lease/session handles."
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

if [[ "${DIFF_MODE}" == "range" ]]; then
  changed_cs_source_cmd=(git diff --name-only "${DIFF_RANGE_VALUE}" -- '*.cs')
else
  changed_cs_source_cmd=(git diff --name-only HEAD -- '*.cs')
fi

mutable_state_pattern='^\+.*\b(private|protected|internal)\s+((readonly|static)\s+)*(ConcurrentDictionary|Dictionary|HashSet|Queue)\s*<'
mutable_state_hits=""

while IFS= read -r file; do
  if [ -z "${file}" ]; then
    continue
  fi

  case "${file}" in
    src/Aevatar.CQRS.Projection.Core/*|src/workflow/Aevatar.Workflow.Projection/*|src/workflow/Aevatar.Workflow.Application/*|src/workflow/Aevatar.Workflow.Core/*|src/maker/Aevatar.Maker.Core/*|src/Aevatar.Foundation.Core/*)
      ;;
    *)
      continue
      ;;
  esac

  case "${file}" in
    *InMemory*|*inmemory*)
      continue
      ;;
  esac

  if [[ "${DIFF_MODE}" == "range" ]]; then
    hits="$(git diff --unified=0 "${DIFF_RANGE_VALUE}" -- "${file}" | rg -n "${mutable_state_pattern}" || true)"
  else
    hits="$(git diff --unified=0 HEAD -- "${file}" | rg -n "${mutable_state_pattern}" || true)"
  fi

  if [ -n "${hits}" ]; then
    mutable_state_hits="${mutable_state_hits}
${file}
${hits}
"
  fi
done < <("${changed_cs_source_cmd[@]}")

if [ -n "${mutable_state_hits}" ]; then
  echo "${mutable_state_hits}"
  echo "Mutable in-memory collection fields in middle layer are forbidden. Use actor state or distributed state abstractions."
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

echo "Architecture guards passed."
