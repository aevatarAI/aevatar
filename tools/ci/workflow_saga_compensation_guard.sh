#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ci_rg_bin="${AEVATAR_CI_RG_BIN:-rg}"

ci_file() {
  local path="$1"

  if [ ! -f "${path}" ]; then
    echo "Missing required saga compensation contract file: ${path}"
    exit 1
  fi
}

ci_extract_method() {
  local file="$1"
  local signature="$2"

  awk -v signature="${signature}" '
    index($0, signature) > 0 { capture = 1 }
    capture { print }
    capture && /^[[:space:]]{4}\}/ { exit }
  ' "${file}"
}

ci_extract_case() {
  local text="$1"
  local case_label="$2"

  printf '%s\n' "${text}" | awk -v case_label="${case_label}" '
    index($0, case_label) > 0 { capture = 1 }
    capture { print }
    capture && capture_count > 0 && /^[[:space:]]*case / { exit }
    capture && /^[[:space:]]*default:/ { exit }
    capture { capture_count++ }
  '
}

ci_require_pattern() {
  local text="$1"
  local pattern="$2"
  local failure="$3"

  if ! printf '%s\n' "${text}" | "${ci_rg_bin}" -q --multiline "${pattern}"; then
    echo "${failure}"
    exit 1
  fi
}

ci_forbid_pattern() {
  local text="$1"
  local pattern="$2"
  local failure="$3"

  if printf '%s\n' "${text}" | "${ci_rg_bin}" -q --multiline "${pattern}"; then
    echo "${failure}"
    exit 1
  fi
}

ci_require_file_pattern() {
  local file="$1"
  local pattern="$2"
  local failure="$3"

  if ! "${ci_rg_bin}" -q --multiline "${pattern}" "${file}"; then
    echo "${failure}"
    exit 1
  fi
}

if ! command -v "${ci_rg_bin}" >/dev/null 2>&1; then
  echo "workflow saga compensation guard requires rg-compatible search binary."
  exit 1
fi

kernel_file="src/workflow/Aevatar.Workflow.Core/Execution/WorkflowExecutionKernel.cs"
run_agent_file="src/workflow/Aevatar.Workflow.Core/WorkflowRunGAgent.cs"
validator_file="src/workflow/Aevatar.Workflow.Core/Validation/WorkflowValidator.cs"

ci_file "${kernel_file}"
ci_file "${run_agent_file}"
ci_file "${validator_file}"

step_completed_method="$(ci_extract_method "${kernel_file}" "private async Task HandleStepCompletedAsync(")"
compensation_transition_method="$(ci_extract_method "${kernel_file}" "private async Task<bool> HandleCompensationTransitionAsync(")"
try_start_method="$(ci_extract_method "${kernel_file}" "private async Task TryStartCompensationOrPublishTerminalFailureAsync(")"
publish_compensation_method="$(ci_extract_method "${kernel_file}" "private static Task PublishCompensationRequestAsync(")"
record_completion_method="$(ci_extract_method "${run_agent_file}" "async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationStepCompletionAsync(")"

if [ -z "${step_completed_method}" ] || [ -z "${compensation_transition_method}" ] || [ -z "${try_start_method}" ] || [ -z "${publish_compensation_method}" ] || [ -z "${record_completion_method}" ]; then
  echo "Unable to locate saga compensation methods; guard fails closed."
  exit 1
fi

transition_dead_letter_case="$(ci_extract_case "${compensation_transition_method}" "WorkflowCompensationTransitionStatus.CompensationDeadLettered")"
try_start_dead_letter_case="$(ci_extract_case "${try_start_method}" "WorkflowCompensationTransitionStatus.CompensationDeadLettered")"

if [ -z "${transition_dead_letter_case}" ] || [ -z "${try_start_dead_letter_case}" ]; then
  echo "Unable to locate saga compensation dead-letter cases; guard fails closed."
  exit 1
fi

ci_require_pattern \
  "${step_completed_method}" \
  "TryStartCompensationOrPublishTerminalFailureAsync[[:space:]]*\\(" \
  "Terminal workflow failure must enter the compensation decision path before publishing failed WorkflowCompletedEvent."

ci_require_pattern \
  "${try_start_method}" \
  "TryStartCompensationAsync[[:space:]]*\\(" \
  "Terminal workflow failure must ask the state host to start compensation."

ci_require_pattern \
  "${try_start_method}" \
  "(?s)WorkflowCompensationTransitionStatus\\.(Started|AlreadyCompensating|AdvancedAndRequestedNext)[[:space:]]*:.*PublishCompensationRequestAsync[[:space:]]*\\(" \
  "Compensable terminal failure must publish a compensation request instead of completing failed directly."

ci_require_pattern \
  "${try_start_method}" \
  "(?s)WorkflowCompensationTransitionStatus\\.NoCompensableLedger[[:space:]]*:.*PublishWorkflowCompletedAsync[[:space:]]*\\([[:space:]]*ctx,[[:space:]]*terminalFailure" \
  "Failed WorkflowCompletedEvent may only be published directly when no compensable ledger exists."

ci_require_pattern \
  "${transition_dead_letter_case}" \
  "PublishWorkflowCompletedAsync[[:space:]]*\\(" \
  "Dead-lettered compensation transition must publish failed WorkflowCompletedEvent to notify callers."

ci_require_pattern \
  "${try_start_dead_letter_case}" \
  "PublishWorkflowCompletedAsync[[:space:]]*\\(" \
  "Dead-lettered compensation start result must publish failed WorkflowCompletedEvent to notify callers."

ci_require_pattern \
  "${publish_compensation_method}" \
  "(?s)ctx\\.PublishAsync[[:space:]]*\\([[:space:]]*new CompensationRequestEvent.*TopologyAudience\\.Self" \
  "Compensation request dispatch must use self-continuation."

ci_forbid_pattern \
  "${publish_compensation_method}" \
  "DispatchStepAsync|HandleCompensationRequestAsync|HandleStepCompletedAsync|Task\\.Run" \
  "Compensation request dispatch must not inline-advance or use callback-thread execution."

ci_require_file_pattern \
  "${validator_file}" \
  "step\\.Compensation.*!stepIds\\.Contains\\(step\\.Compensation\\)" \
  "WorkflowValidator must reject compensation declarations that do not resolve to an existing step id."

ci_require_pattern \
  "${record_completion_method}" \
  "(?s)!completion\\.Success.*PersistDomainEventAsync[[:space:]]*\\([[:space:]]*new WorkflowCompensationFailedEvent" \
  "Failed compensation completion must persist WorkflowCompensationFailedEvent."

ci_require_pattern \
  "${record_completion_method}" \
  "WorkflowCompensationTransitionStatus\\.CompensationDeadLettered" \
  "Failed compensation completion must return the dead-letter transition instead of logging and dropping."

echo "Workflow saga compensation guards passed."
