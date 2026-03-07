#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

runtime_delay_hits="$(
  rg -n "Task\.Delay\(" \
    src/Aevatar.Foundation.Runtime.Implementations.Orleans/Grains/RuntimeActorGrain.cs \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptRuntimeGAgent*.cs \
    || true
)"

if [ -n "${runtime_delay_hits}" ]; then
  echo "${runtime_delay_hits}"
  echo "Runtime callback paths must not use Task.Delay directly. Route delay/timeout/retry through Runtime.Callbacks."
  exit 1
fi

raw_callback_metadata_hits="$(
  rg -n "RuntimeCallbackMetadataKeys\.(CallbackId|CallbackGeneration|CallbackFireIndex|CallbackFiredAtUnixTimeMs)" \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptRuntimeGAgent*.cs \
    || true
)"

if [ -n "${raw_callback_metadata_hits}" ]; then
  echo "${raw_callback_metadata_hits}"
  echo "Workflow/script callback consumers must use RuntimeCallbackEnvelopeMetadataReader.MatchesLease instead of reading raw metadata keys."
  exit 1
fi

raw_callback_id_hits="$(
  rg -n '"(delay-step:|wait-signal-timeout:|workflow-step-timeout:|workflow-step-retry-backoff:|llm-watchdog:|script-definition-query-timeout:)' \
    src/workflow/Aevatar.Workflow.Core/Modules \
    src/Aevatar.Scripting.Core/ScriptRuntimeGAgent*.cs \
    || true
)"

if [ -n "${raw_callback_id_hits}" ]; then
  echo "${raw_callback_id_hits}"
  echo "Runtime callback ids must be built through RuntimeCallbackKeyComposer."
  exit 1
fi

script_runtime_persistent_lease_hits="$(
  rg -n "timeout_generation|timeout_backend|timeout_lease|RuntimeCallbackLease" \
    src/Aevatar.Scripting.Abstractions/script_host_messages.proto \
    || true
)"

if [ -n "${script_runtime_persistent_lease_hits}" ]; then
  echo "${script_runtime_persistent_lease_hits}"
  echo "Script runtime may persist pending definition-query facts, but callback lease/backend metadata must remain activation-local."
  exit 1
fi

echo "Runtime callback guards passed."
