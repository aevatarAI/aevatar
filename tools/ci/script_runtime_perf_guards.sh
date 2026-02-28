#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

metrics_file="${SCRIPT_RUNTIME_PERF_METRICS_FILE:-artifacts/script-runtime/perf-metrics.txt}"
exec_p95="${SCRIPT_RUNTIME_EXEC_START_P95_MS:-}"
first_token_p95="${SCRIPT_RUNTIME_FIRST_TOKEN_P95_MS:-}"

runtime_present=0
if rg --files src test -g 'Aevatar.AI.Script.*.csproj' | rg -q '.'; then
  runtime_present=1
fi

if [[ -z "${exec_p95}" || -z "${first_token_p95}" ]]; then
  if [[ -f "${metrics_file}" ]]; then
    exec_p95="$(awk -F '=' '$1=="exec_start_latency_p95_ms"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
    first_token_p95="$(awk -F '=' '$1=="first_token_latency_p95_ms"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
  fi
fi

if [[ -z "${exec_p95}" || -z "${first_token_p95}" ]]; then
  if [[ "${runtime_present}" -eq 0 ]]; then
    echo "Script runtime projects not found; perf guard skipped."
    exit 0
  fi

  echo "Missing perf metrics. Provide env vars or ${metrics_file}:"
  echo "  exec_start_latency_p95_ms"
  echo "  first_token_latency_p95_ms"
  exit 1
fi

if ! [[ "${exec_p95}" =~ ^[0-9]+([.][0-9]+)?$ && "${first_token_p95}" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "Perf metrics must be numeric. Got exec_start_latency_p95_ms=${exec_p95}, first_token_latency_p95_ms=${first_token_p95}."
  exit 1
fi

if ! awk "BEGIN {exit !(${exec_p95} <= 200)}"; then
  echo "Perf guard failed: exec_start_latency_p95_ms=${exec_p95} exceeds 200ms."
  exit 1
fi

if ! awk "BEGIN {exit !(${first_token_p95} <= 800)}"; then
  echo "Perf guard failed: first_token_latency_p95_ms=${first_token_p95} exceeds 800ms."
  exit 1
fi

echo "Script runtime perf guard passed: exec_start_latency_p95_ms=${exec_p95}, first_token_latency_p95_ms=${first_token_p95}."
