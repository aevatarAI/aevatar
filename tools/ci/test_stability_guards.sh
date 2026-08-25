#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

allowlist_file="tools/ci/test_polling_allowlist.txt"

run_guard_meta_tests() {
  bash "${SCRIPT_DIR}/test_coverage_file_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_python_entrypoint_portability.sh"
  bash "${SCRIPT_DIR}/tests/test_project_reference_layer_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_architecture_guards_enforces_layer_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_projection_document_reader_list_async_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_catch_exception_observability_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_audit_trail_guards.sh"
  bash "${SCRIPT_DIR}/tests/test_lark_agent_path_contract_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_fkst_host_policy_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_gagent_registry_kind_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_query_projection_priming_guard.sh"
  bash "${SCRIPT_DIR}/tests/test_nyxid_chat_semantics_guard.sh"
  python3 -m unittest tools/ci/tests/test_nyxid_semantic_evaluation.py
}

if [[ ! -f "${allowlist_file}" ]]; then
  echo "Missing allowlist: ${allowlist_file}"
  exit 1
fi

hits="$(rg -n "Task\\.Delay\\(|WaitUntilAsync\\(" test -g '*.cs' || true)"
if [[ -z "${hits}" ]]; then
  echo "No polling waits found in tests."
  run_guard_meta_tests
  exit 0
fi

disallowed=""
while IFS= read -r hit; do
  [[ -z "${hit}" ]] && continue

  file_path="${hit%%:*}"
  if ! rg -Fx "${file_path}" "${allowlist_file}" >/dev/null; then
    disallowed="${disallowed}${hit}"$'\n'
  fi
done <<< "${hits}"

if [[ -n "${disallowed}" ]]; then
  echo "Detected polling wait usages outside allowlist:"
  printf '%s' "${disallowed}"
  echo "Add deterministic sync points (TaskCompletionSource/channel) or explicitly approve file in ${allowlist_file}."
  exit 1
fi

echo "Test stability guard passed (polling waits constrained by allowlist)."
run_guard_meta_tests
