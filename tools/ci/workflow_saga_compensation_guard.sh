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
dispatch_step_method="$(ci_extract_method "${kernel_file}" "private async Task DispatchStepAsync(")"
schedule_step_timeout_method="$(ci_extract_method "${kernel_file}" "private async Task<RuntimeCallbackLease?> ScheduleStepTimeoutLeaseAsync(")"
compensation_request_method="$(ci_extract_method "${kernel_file}" "private async Task HandleCompensationRequestAsync(")"
schedule_phase_deadline_method="$(ci_extract_method "${kernel_file}" "private async Task ScheduleCompensationPhaseDeadlineAsync(")"
cleanup_run_method="$(ci_extract_method "${kernel_file}" "private async Task CleanupRunAsync(")"
record_completion_method="$(ci_extract_method "${run_agent_file}" "async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationStepCompletionAsync(")"
record_deadline_method="$(ci_extract_method "${run_agent_file}" "async Task<WorkflowCompensationTransitionResult> IWorkflowExecutionStateHost.RecordCompensationPhaseDeadlineExceededAsync(")"

if [ -z "${step_completed_method}" ] ||
   [ -z "${compensation_transition_method}" ] ||
   [ -z "${try_start_method}" ] ||
   [ -z "${publish_compensation_method}" ] ||
   [ -z "${dispatch_step_method}" ] ||
   [ -z "${schedule_step_timeout_method}" ] ||
   [ -z "${compensation_request_method}" ] ||
   [ -z "${schedule_phase_deadline_method}" ] ||
   [ -z "${cleanup_run_method}" ] ||
   [ -z "${record_completion_method}" ] ||
   [ -z "${record_deadline_method}" ]; then
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
  "${kernel_file}" \
  "DefaultCompensationTimeoutMs[[:space:]]*=[[:space:]]*30_000" \
  "Compensation dispatch must define the 30s default timeout constant."

ci_require_file_pattern \
  "${kernel_file}" \
  "CompensationPhaseDeadlineMs[[:space:]]*=[[:space:]]*300_000" \
  "Compensation phase must define the 5 minute durable deadline constant."

ci_require_pattern \
  "${dispatch_step_method}" \
  "ResolveStepTimeoutMs[[:space:]]*\\([[:space:]]*step,[[:space:]]*dispatchKind[[:space:]]*\\)" \
  "Step dispatch must resolve timeout from dispatch kind before scheduling."

ci_require_pattern \
  "${schedule_step_timeout_method}" \
  "Math\\.Clamp[[:space:]]*\\([[:space:]]*effectiveTimeoutMs,[[:space:]]*100,[[:space:]]*600_000[[:space:]]*\\)" \
  "Step timeout lease scheduling must clamp the effective timeout."

ci_require_file_pattern \
  "${kernel_file}" \
  "(?s)ResolveStepTimeoutMs[[:space:]]*\\([^)]*WorkflowStepDispatchKind[[:space:]]+dispatchKind[^)]*\\).*WorkflowStepDispatchKind\\.Compensation.*DefaultCompensationTimeoutMs" \
  "Compensation dispatch without TimeoutMs must apply DefaultCompensationTimeoutMs."

ci_require_pattern \
  "${compensation_request_method}" \
  "EnsureCompensationPhaseDeadlineAsync[[:space:]]*\\(" \
  "Compensation request handling must re-arm the phase deadline when needed."

ci_require_pattern \
  "${try_start_method}" \
  "EnsureCompensationPhaseDeadlineAsync[[:space:]]*\\(" \
  "Compensation phase start must schedule a durable phase deadline."

ci_require_pattern \
  "${compensation_transition_method}" \
  "EnsureCompensationPhaseDeadlineAsync[[:space:]]*\\(" \
  "Compensation continuation must preserve the durable phase deadline."

ci_require_pattern \
  "${schedule_phase_deadline_method}" \
  "(?s)ScheduleSelfDurableTimeoutAsync[[:space:]]*\\(.*CompensationPhaseDeadlineMs.*new WorkflowCompensationPhaseDeadlineFiredEvent" \
  "Compensation phase deadline must be a durable self-timeout event."

ci_require_pattern \
  "${cleanup_run_method}" \
  "var[[:space:]]+compensationPhaseDeadlineLease[[:space:]]*=[[:space:]]*state\\.CompensationPhaseDeadlineLease\\?\\.Clone\\(\\)" \
  "Terminal run cleanup must capture the compensation phase deadline lease."

ci_require_pattern \
  "${cleanup_run_method}" \
  "(?s)CompensationPhaseDeadlineCallbackId[[:space:]]*=[[:space:]]*string\\.Empty.*CompensationPhaseDeadlineLease[[:space:]]*=[[:space:]]*null.*TryCancelAsync[[:space:]]*\\([^)]*compensationPhaseDeadlineLease" \
  "Terminal run cleanup must clear and cancel the compensation phase deadline lease."

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

ci_require_pattern \
  "${record_deadline_method}" \
  "(?s)PersistDomainEventAsync[[:space:]]*\\([[:space:]]*new WorkflowCompensationFailedEvent.*CalculateRemainingUncompensated" \
  "Compensation phase deadline must persist WorkflowCompensationFailedEvent with remaining count from run state."

ci_require_pattern \
  "${record_deadline_method}" \
  "WorkflowCompensationTransitionStatus\\.CompensationDeadLettered" \
  "Compensation phase deadline must return the dead-letter transition."

echo "Workflow saga compensation guards passed."
