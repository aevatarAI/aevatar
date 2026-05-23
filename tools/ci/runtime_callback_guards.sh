#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

ci_rg_bin="${AEVATAR_CI_RG_BIN:-rg}"

ci_search() {
  local pattern="$1"
  shift

  if command -v "${ci_rg_bin}" >/dev/null 2>&1; then
    "${ci_rg_bin}" -n "${pattern}" "$@" || true
    return
  fi

  grep -RInE -- "${pattern}" "$@" 2>/dev/null || true
}

ci_filter_out() {
  local pattern="$1"

  if command -v "${ci_rg_bin}" >/dev/null 2>&1; then
    "${ci_rg_bin}" -v "${pattern}" || true
    return
  fi

  grep -Ev -- "${pattern}" || true
}

runtime_delay_hits="$(
  ci_search "Task\.Delay\(" \
    src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs \
)"

if [ -n "${runtime_delay_hits}" ]; then
  echo "${runtime_delay_hits}"
  echo "Runtime callback paths must not use Task.Delay directly. Route delay/timeout/retry through Runtime.Callbacks."
  exit 1
fi

raw_callback_metadata_hits="$(
  ci_search "RuntimeCallbackMetadataKeys\.(CallbackId|CallbackGeneration|CallbackFireIndex|CallbackFiredAtUnixTimeMs)" \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs \
)"

if [ -n "${raw_callback_metadata_hits}" ]; then
  echo "${raw_callback_metadata_hits}"
  echo "Workflow/script callback consumers must use RuntimeCallbackEnvelopeMetadataReader.MatchesLease instead of reading raw metadata keys."
  exit 1
fi

raw_callback_id_hits="$(
  ci_search '"(delay-step:|wait-signal-timeout:|workflow-step-timeout:|workflow-step-retry-backoff:|llm-watchdog:|script-definition-query-timeout:)' \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptBehaviorGAgent.cs \
)"

if [ -n "${raw_callback_id_hits}" ]; then
  echo "${raw_callback_id_hits}"
  echo "Runtime callback ids must be built through RuntimeCallbackKeyComposer."
  exit 1
fi

script_runtime_persistent_lease_hits="$(
  ci_search "timeout_generation|timeout_backend|timeout_lease|RuntimeCallbackLease" \
    src/Aevatar.Scripting.Abstractions/script_host_messages.proto \
)"

if [ -n "${script_runtime_persistent_lease_hits}" ]; then
  echo "${script_runtime_persistent_lease_hits}"
  echo "Script runtime may persist pending definition-query facts, but callback lease/backend metadata must remain activation-local."
  exit 1
fi

handwritten_callback_state_hits="$(
  ci_search "class (RuntimeCallbackSchedulerGrainState|ReminderScheduledCallbackState)|EnvelopeBytes|IPersistentState<[^>]*(Callback|callback)[^>]*State" \
    src/Aevatar.Foundation.Runtime \
    src/Aevatar.Foundation.Runtime.Implementations.Orleans \
    | ci_filter_out "^[^[:space:]]+:[0-9]+:[[:space:]]*//"
)"

if [ -n "${handwritten_callback_state_hits}" ]; then
  allowed_callback_state_hits="$(
    printf '%s\n' "${handwritten_callback_state_hits}" |
      ci_filter_out "IPersistentState<RuntimeCallbackSchedulerState>"
  )"

  if [ -n "${allowed_callback_state_hits}" ]; then
    echo "${allowed_callback_state_hits}"
    echo "Durable runtime callback scheduler state must use generated protobuf RuntimeCallbackSchedulerState and typed EventEnvelope fields, not hand-written callback state payload classes or raw envelope bytes."
    exit 1
  fi
fi

echo "Runtime callback guards passed."
