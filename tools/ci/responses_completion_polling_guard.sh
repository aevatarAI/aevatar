#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

scan_paths=(
  "src/platform/Aevatar.GAgentService.Application/Responses"
)

set +e
polling_report="$(
  rg -n "LlmSessionCompletionObserver|response_completion_not_observed|WaitForCompletionAsync|Task\.Delay\(|RecordCompletionAndReadAsync" \
    "${scan_paths[@]}" \
    -g '!**/bin/**' \
    -g '!**/obj/**'
)"
polling_status=$?
set -e

if [[ ${polling_status} -ne 0 && ${polling_status} -ne 1 ]]; then
  echo "Responses completion polling guard execution failed."
  exit "${polling_status}"
fi

if [[ -n "${polling_report}" ]]; then
  echo "Responses completion recording must not poll read models on the request path."
  echo "${polling_report}"
  exit 1
fi
