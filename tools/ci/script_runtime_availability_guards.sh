#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

metrics_file="${SCRIPT_RUNTIME_AVAILABILITY_METRICS_FILE:-artifacts/script-runtime/availability-metrics.txt}"
run_success_rate="${SCRIPT_RUNTIME_RUN_SUCCESS_RATE_30M:-}"

runtime_present=0
if rg --files src test -g 'Aevatar.AI.Script.*.csproj' | rg -q '.'; then
  runtime_present=1
fi

if [[ -z "${run_success_rate}" && -f "${metrics_file}" ]]; then
  run_success_rate="$(awk -F '=' '$1=="run_success_rate_30m"{print $2}' "${metrics_file}" | tr -d '[:space:]')"
fi

if [[ -z "${run_success_rate}" ]]; then
  if [[ "${runtime_present}" -eq 0 ]]; then
    echo "Script runtime projects not found; availability guard skipped."
    exit 0
  fi

  echo "Missing availability metric run_success_rate_30m. Provide env var or ${metrics_file}."
  exit 1
fi

if ! [[ "${run_success_rate}" =~ ^[0-9]+([.][0-9]+)?$ ]]; then
  echo "Availability metric run_success_rate_30m must be numeric. Got ${run_success_rate}."
  exit 1
fi

if ! awk "BEGIN {exit !(${run_success_rate} >= 99.5)}"; then
  echo "Availability guard failed: run_success_rate_30m=${run_success_rate} is below 99.5."
  exit 1
fi

echo "Script runtime availability guard passed: run_success_rate_30m=${run_success_rate}."
