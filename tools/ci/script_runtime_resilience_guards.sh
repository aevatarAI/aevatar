#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

metrics_file="${SCRIPT_RUNTIME_RESILIENCE_METRICS_FILE:-artifacts/script-runtime/resilience-metrics.txt}"

cancel_consistent="${SCRIPT_RUNTIME_CANCEL_CONSISTENT:-}"
timeout_consistent="${SCRIPT_RUNTIME_TIMEOUT_CONSISTENT:-}"
restart_consistent="${SCRIPT_RUNTIME_RESTART_CONSISTENT:-}"
reclaim_time_p95_ms="${SCRIPT_RUNTIME_CONTAINER_RECLAIM_TIME_P95_MS:-}"
alc_unload_success_rate="${SCRIPT_RUNTIME_ALC_UNLOAD_SUCCESS_RATE:-}"

runtime_present=0
if rg --files src test -g 'Aevatar.AI.Script.*.csproj' | rg -q '.'; then
  runtime_present=1
fi

if [[ -f "${metrics_file}" ]]; then
  [[ -z "${cancel_consistent}" ]] && cancel_consistent="$(awk -F '=' '$1=="cancel_consistent"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
  [[ -z "${timeout_consistent}" ]] && timeout_consistent="$(awk -F '=' '$1=="timeout_consistent"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
  [[ -z "${restart_consistent}" ]] && restart_consistent="$(awk -F '=' '$1=="restart_consistent"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
  [[ -z "${reclaim_time_p95_ms}" ]] && reclaim_time_p95_ms="$(awk -F '=' '$1=="container_reclaim_time_p95_ms"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
  [[ -z "${alc_unload_success_rate}" ]] && alc_unload_success_rate="$(awk -F '=' '$1=="script_alc_unload_success_rate"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
fi

if [[ -z "${cancel_consistent}" || -z "${timeout_consistent}" || -z "${restart_consistent}" || -z "${reclaim_time_p95_ms}" || -z "${alc_unload_success_rate}" ]]; then
  if [[ "${runtime_present}" -eq 0 ]]; then
    echo "Script runtime projects not found; resilience guard skipped."
    exit 0
  fi

  echo "Missing resilience metrics. Required keys:"
  echo "  cancel_consistent timeout_consistent restart_consistent"
  echo "  container_reclaim_time_p95_ms script_alc_unload_success_rate"
  exit 1
fi

for name in cancel_consistent timeout_consistent restart_consistent; do
  value="${!name}"
  if [[ "${value}" != "true" ]]; then
    echo "Resilience guard failed: ${name} must be true, got ${value}."
    exit 1
  fi
done

if ! [[ "${reclaim_time_p95_ms}" =~ ^[0-9]+([.][0-9]+)?$ && "${alc_unload_success_rate}" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "Numeric resilience metrics are invalid. container_reclaim_time_p95_ms=${reclaim_time_p95_ms}, script_alc_unload_success_rate=${alc_unload_success_rate}."
  exit 1
fi

if ! awk "BEGIN {exit !(${reclaim_time_p95_ms} < 5000)}"; then
  echo "Resilience guard failed: container_reclaim_time_p95_ms=${reclaim_time_p95_ms} exceeds 5000ms."
  exit 1
fi

if ! awk "BEGIN {exit !(${alc_unload_success_rate} >= 99.9)}"; then
  echo "Resilience guard failed: script_alc_unload_success_rate=${alc_unload_success_rate} is below 99.9."
  exit 1
fi

echo "Script runtime resilience guard passed."
