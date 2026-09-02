#!/usr/bin/env bash
#
# Workflow Run Observatory read-only + embedded-asset guard.
#
# Enforces the C2 (06-19-workflow-run-observatory) invariants from the design spec:
#
#   1. Read-only surface: the observatory endpoints are GET-only. No MapPost/MapPut/MapDelete/MapPatch
#      may appear in the endpoint file — there are no edit/run/stop controls anywhere.
#   2. Query-ports-only seam: the scope-enforcement query service must depend on query ports only
#      (IWorkflowExecution*QueryPort). It must NOT depend on IActorDispatchPort / IActorRuntime /
#      IEventStore — a read viewer never dispatches commands or side-reads write-model state.
#   3. Zero-build embedded asset: the page is an embedded HTML asset with host-time configuration
#      injection. It must NOT reference wwwroot or a build step.
#
# Pre-implementation: when the observatory files do not exist, the guard is a scaffold no-op.
#
# Usage:
#   tools/ci/workflow_observatory_readonly_guard.sh
#       Scan the production observatory files.
#   tools/ci/workflow_observatory_readonly_guard.sh --scan <dir>
#       Scan an arbitrary directory (used by guard self-tests).

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ENDPOINTS_FILE="src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowRunObservatoryEndpoints.cs"
PAGE_FILE="src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/workflow-observatory.html"
OLD_PAGE_FILE="src/workflow/Aevatar.Workflow.Infrastructure/CapabilityApi/WorkflowRunObservatoryPage.cs"
SERVICE_FILE="src/workflow/Aevatar.Workflow.Application/Observatory/WorkflowRunObservatoryQueryService.cs"
CSPROJ_FILE="src/workflow/Aevatar.Workflow.Infrastructure/Aevatar.Workflow.Infrastructure.csproj"

if [[ "${1:-}" == "--scan" ]] && [[ -n "${2:-}" ]]; then
  ENDPOINTS_FILE="$2/WorkflowRunObservatoryEndpoints.cs"
  PAGE_FILE="$2/workflow-observatory.html"
  OLD_PAGE_FILE="$2/WorkflowRunObservatoryPage.cs"
  SERVICE_FILE="$2/WorkflowRunObservatoryQueryService.cs"
  CSPROJ_FILE="$2/Aevatar.Workflow.Infrastructure.csproj"
fi

if [[ ! -f "${ENDPOINTS_FILE}" && ! -f "${SERVICE_FILE}" ]]; then
  echo "workflow_observatory_readonly_guard: skipped (observatory not yet landed)."
  exit 0
fi

violations=0

# 1. GET-only endpoints.
if [[ -f "${ENDPOINTS_FILE}" ]]; then
  mutating_hits="$(rg -n -P '\.Map(Post|Put|Delete|Patch)\s*\(' "${ENDPOINTS_FILE}" || true)"
  if [[ -n "${mutating_hits}" ]]; then
    echo "${mutating_hits}"
    echo "Workflow Run Observatory is read-only: only GET endpoints are allowed (no Map{Post,Put,Delete,Patch})."
    violations=$((violations + 1))
  fi
fi

# 2. Query-ports-only scope seam (ignore comment lines — the doc text legitimately names the
#    forbidden ports to explain why they are excluded).
if [[ -f "${SERVICE_FILE}" ]]; then
  forbidden_deps="$(rg -n -P '^(?!\s*//).*\b(IActorDispatchPort|IActorRuntime|IEventStore|ICommandDispatch|IProjectionDocumentReader|HttpContext)\b' "${SERVICE_FILE}" || true)"
  if [[ -n "${forbidden_deps}" ]]; then
    echo "${forbidden_deps}"
    echo "WorkflowRunObservatoryQueryService must depend on query ports only (no dispatch/runtime/event-store/HttpContext)."
    violations=$((violations + 1))
  fi
fi

# 3. Embedded zero-build page (no wwwroot / no build step); ignore comment lines.
if [[ -f "${OLD_PAGE_FILE}" ]]; then
  echo "${OLD_PAGE_FILE}: old C# raw-string page carrier must not exist; use workflow-observatory.html embedded asset."
  violations=$((violations + 1))
fi

if [[ -f "${PAGE_FILE}" ]]; then
  wwwroot_hits="$(rg -n -P '^(?!\s*//).*\bwwwroot\b' "${PAGE_FILE}" || true)"
  if [[ -n "${wwwroot_hits}" ]]; then
    echo "${wwwroot_hits}"
    echo "Workflow Run Observatory page must stay a zero-build embedded asset (no wwwroot)."
    violations=$((violations + 1))
  fi
  build_hits="$(rg -n -P '\b(npm|pnpm|yarn|webpack|vite|rollup|node_modules)\b' "${PAGE_FILE}" || true)"
  if [[ -n "${build_hits}" ]]; then
    echo "${build_hits}"
    echo "Workflow Run Observatory page must not depend on a frontend build step."
    violations=$((violations + 1))
  fi
  if ! rg -q -F '__BACKEND_CONSOLE_CONFIG__' "${PAGE_FILE}"; then
    echo "${PAGE_FILE}: expected __BACKEND_CONSOLE_CONFIG__ host configuration placeholder."
    violations=$((violations + 1))
  fi
else
  echo "${PAGE_FILE}: expected embedded workflow observatory HTML asset."
  violations=$((violations + 1))
fi

if [[ -f "${CSPROJ_FILE}" ]]; then
  if ! rg -q -F 'workflow-observatory.html' "${CSPROJ_FILE}"; then
    echo "${CSPROJ_FILE}: expected workflow-observatory.html to be declared as an EmbeddedResource."
    violations=$((violations + 1))
  fi
else
  echo "${CSPROJ_FILE}: expected workflow infrastructure project file for embedded asset declaration."
  violations=$((violations + 1))
fi

if [[ ${violations} -gt 0 ]]; then
  echo
  echo "workflow_observatory_readonly_guard: FAILED — ${violations} violation(s)."
  exit 1
fi

echo "workflow_observatory_readonly_guard: ok"
