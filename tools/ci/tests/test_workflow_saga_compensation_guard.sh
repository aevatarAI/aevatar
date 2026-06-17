#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../../.." && pwd)"
GUARD="${REPO_ROOT}/tools/ci/workflow_saga_compensation_guard.sh"
TMP_DIR="$(mktemp -d)"
trap 'rm -rf "${TMP_DIR}"' EXIT

assert_fails_with() {
  local expected="$1"
  shift
  local output="${TMP_DIR}/failure.out"

  set +e
  "$@" > "${output}" 2>&1
  local status=$?
  set -e

  if [[ ${status} -eq 0 ]]; then
    echo "Expected command to fail: $*"
    cat "${output}"
    exit 1
  fi

  if ! rg -q "${expected}" "${output}"; then
    echo "Expected failure output to contain: ${expected}"
    cat "${output}"
    exit 1
  fi
}

copy_fixture() {
  local name="$1"
  local root="${TMP_DIR}/${name}"

  mkdir -p \
    "${root}/tools/ci" \
    "${root}/src/workflow/Aevatar.Workflow.Core/Execution" \
    "${root}/src/workflow/Aevatar.Workflow.Core/Validation" \
    "${root}/src/workflow/Aevatar.Workflow.Core"

  cp "${GUARD}" "${root}/tools/ci/workflow_saga_compensation_guard.sh"
  cp "${REPO_ROOT}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
    "${root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs"
  cp "${REPO_ROOT}/src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs" \
    "${root}/src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs"
  cp "${REPO_ROOT}/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs" \
    "${root}/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs"

  printf '%s\n' "${root}"
}

python_replace() {
  local file="$1"
  local old="$2"
  local new="$3"

  python3 - "$file" "$old" "$new" <<'PY'
import sys
from pathlib import Path

path = Path(sys.argv[1])
old = sys.argv[2]
new = sys.argv[3]
text = path.read_text()
if old not in text:
    raise SystemExit(f"fixture text not found in {path}: {old}")
path.write_text(text.replace(old, new, 1))
PY
}

python_replace_all() {
  local file="$1"
  local old="$2"
  local new="$3"

  python3 - "$file" "$old" "$new" <<'PY'
import sys
from pathlib import Path

path = Path(sys.argv[1])
old = sys.argv[2]
new = sys.argv[3]
text = path.read_text()
if old not in text:
    raise SystemExit(f"fixture text not found in {path}: {old}")
path.write_text(text.replace(old, new))
PY
}

bash "${GUARD}"

direct_failure_root="$(copy_fixture direct-failed-completion)"
python_replace_all \
  "${direct_failure_root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
  "await TryStartCompensationOrPublishTerminalFailureAsync(" \
  "await PublishWorkflowCompletedAsync("
assert_fails_with \
  "Terminal workflow failure must enter the compensation decision path" \
  bash "${direct_failure_root}/tools/ci/workflow_saga_compensation_guard.sh"

inline_dispatch_root="$(copy_fixture inline-dispatch)"
python_replace \
  "${inline_dispatch_root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
  "return ctx.PublishAsync(new CompensationRequestEvent" \
  "return DispatchStepAsync(new StepDefinition(), string.Empty, [], new WorkflowExecutionKernelState(), ctx, ct); // return ctx.PublishAsync(new CompensationRequestEvent"
assert_fails_with \
  "Compensation request dispatch must not inline-advance" \
  bash "${inline_dispatch_root}/tools/ci/workflow_saga_compensation_guard.sh"

missing_validator_root="$(copy_fixture missing-validator)"
python_replace \
  "${missing_validator_root}/src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs" \
  "            if (!string.IsNullOrWhiteSpace(step.Compensation) && !stepIds.Contains(step.Compensation))" \
  "            if (!string.IsNullOrWhiteSpace(step.Compensation) && stepIds.Count < 0)"
assert_fails_with \
  "WorkflowValidator must reject compensation declarations" \
  bash "${missing_validator_root}/tools/ci/workflow_saga_compensation_guard.sh"

log_drop_root="$(copy_fixture log-drop)"
python_replace \
  "${log_drop_root}/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs" \
  "await PersistDomainEventAsync(new WorkflowCompensationFailedEvent" \
  "await PersistDomainEventAsync(new CompensationStepCompletedEvent"
assert_fails_with \
  "Failed compensation completion must persist WorkflowCompensationFailedEvent" \
  bash "${log_drop_root}/tools/ci/workflow_saga_compensation_guard.sh"

missing_default_timeout_root="$(copy_fixture missing-default-timeout)"
python_replace \
  "${missing_default_timeout_root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
  "DefaultCompensationTimeoutMs" \
  "DefaultSagaTimeoutMs"
assert_fails_with \
  "Compensation dispatch must define the 30s default timeout constant" \
  bash "${missing_default_timeout_root}/tools/ci/workflow_saga_compensation_guard.sh"

missing_phase_schedule_root="$(copy_fixture missing-phase-schedule)"
python_replace \
  "${missing_phase_schedule_root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
  "await EnsureCompensationPhaseDeadlineAsync(state, ctx, ct);
                await PublishCompensationRequestAsync(" \
  "await Task.CompletedTask;
                await PublishCompensationRequestAsync("
assert_fails_with \
  "Compensation phase start must schedule a durable phase deadline" \
  bash "${missing_phase_schedule_root}/tools/ci/workflow_saga_compensation_guard.sh"

missing_terminal_cancel_root="$(copy_fixture missing-terminal-cancel)"
python_replace \
  "${missing_terminal_cancel_root}/src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs" \
  "var compensationPhaseDeadlineLease = state.CompensationPhaseDeadlineLease?.Clone();" \
  "var compensationPhaseDeadlineLease = (WorkflowRuntimeCallbackLeaseState?)null;"
assert_fails_with \
  "Terminal run cleanup must capture the compensation phase deadline lease" \
  bash "${missing_terminal_cancel_root}/tools/ci/workflow_saga_compensation_guard.sh"

missing_deadline_deadletter_root="$(copy_fixture missing-deadline-deadletter)"
python_replace \
  "${missing_deadline_deadletter_root}/src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs" \
  "async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync" \
  "async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationPhaseBudgetExceededAsync"
assert_fails_with \
  "Unable to locate saga compensation methods" \
  bash "${missing_deadline_deadletter_root}/tools/ci/workflow_saga_compensation_guard.sh"

echo "workflow saga compensation guard tests passed"
