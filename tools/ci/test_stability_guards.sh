#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
cd "${REPO_ROOT}"

bash .claude/skills/codex-refactor-loop/scripts/test_spawn_codex.sh

allowlist_file="tools/ci/test_polling_allowlist.txt"

run_python_tests() {
  if [ -d "tests" ] && command -v python3 >/dev/null 2>&1; then
    if command -v pytest >/dev/null 2>&1 || python3 -m pytest --version >/dev/null 2>&1; then
      echo "==> running python tests"
      python3 -m pytest tests/ -v --tb=short || exit 1
    else
      echo "WARN: pytest not installed; skipping tests/ Python suite (install: pip install pytest)"
    fi
  fi
}

if [[ ! -f "${allowlist_file}" ]]; then
  echo "Missing allowlist: ${allowlist_file}"
  exit 1
fi

hits="$(rg -n "Task\\.Delay\\(|WaitUntilAsync\\(" test -g '*.cs' || true)"
if [[ -z "${hits}" ]]; then
  echo "No polling waits found in tests."
  run_python_tests
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
run_python_tests
