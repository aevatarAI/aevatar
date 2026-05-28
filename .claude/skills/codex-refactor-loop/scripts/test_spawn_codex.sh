#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SPAWN_CODEX="${SCRIPT_DIR}/spawn-codex.sh"
SHARED_PROMPT="${SCRIPT_DIR}/../prompts/_shared.md"
TMP_DIR="$(mktemp -d /tmp/spawn-codex-test.XXXXXXXX)"

cleanup() {
  rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

assert_contains() {
  local file="$1"
  local pattern="$2"
  if ! grep -Fq -- "${pattern}" "${file}"; then
    fail "expected ${file} to contain: ${pattern}"
  fi
}

assert_not_contains() {
  local file="$1"
  local pattern="$2"
  if grep -Fq -- "${pattern}" "${file}"; then
    fail "expected ${file} not to contain: ${pattern}"
  fi
}

extract_prompt_path() {
  local stderr_file="$1"
  sed -n 's/^SPAWN: prompt=\([^ ]*\).*/\1/p' "${stderr_file}" | tail -1
}

shared_heading_count() {
  local file="$1"
  grep -c '^# Shared hard rules$' "${file}"
}

run_dry() {
  local prompt_file="$1"
  local stdout_file="$2"
  local stderr_file="$3"
  "${SPAWN_CODEX}" \
    --cd "${TMP_DIR}" \
    --prompt "${prompt_file}" \
    --log "${TMP_DIR}/codex.log" \
    --timeout 3600 \
    --dry-run \
    >"${stdout_file}" \
    2>"${stderr_file}"
}

case1_prompt="${TMP_DIR}/role-no-shared.md"
case1_stdout="${TMP_DIR}/case1.stdout"
case1_stderr="${TMP_DIR}/case1.stderr"
cat >"${case1_prompt}" <<'PROMPT'
# Role prompt

Do scoped work.
PROMPT

run_dry "${case1_prompt}" "${case1_stdout}" "${case1_stderr}"
case1_rendered="$(extract_prompt_path "${case1_stderr}")"
[[ -n "${case1_rendered}" && -f "${case1_rendered}" ]] || fail "case 1 rendered prompt path missing"
assert_contains "${case1_rendered}" "# Shared hard rules"
assert_contains "${case1_rendered}" "---"
assert_contains "${case1_rendered}" "# Role prompt"
assert_contains "${case1_rendered}" "$(sed -n '3p' "${SHARED_PROMPT}")"
[[ "$(shared_heading_count "${case1_rendered}")" -eq 1 ]] || fail "case 1 shared heading count was not 1"

case2_prompt="${TMP_DIR}/role-with-shared.md"
case2_stdout="${TMP_DIR}/case2.stdout"
case2_stderr="${TMP_DIR}/case2.stderr"
{
  cat "${SHARED_PROMPT}"
  printf '\n---\n\n'
  printf '# Role prompt\n\nAlready rendered.\n'
} >"${case2_prompt}"

run_dry "${case2_prompt}" "${case2_stdout}" "${case2_stderr}"
case2_rendered="$(extract_prompt_path "${case2_stderr}")"
[[ "${case2_rendered}" == "${case2_prompt}" ]] || fail "case 2 should reuse already rendered prompt"
[[ "$(shared_heading_count "${case2_rendered}")" -eq 1 ]] || fail "case 2 double-prepended shared heading"

case3_prompt="${TMP_DIR}/role-dry-run.md"
case3_stdout="${TMP_DIR}/case3.stdout"
case3_stderr="${TMP_DIR}/case3.stderr"
cat >"${case3_prompt}" <<'PROMPT'
# Dry run role

No codex process should start.
PROMPT

run_dry "${case3_prompt}" "${case3_stdout}" "${case3_stderr}"
assert_contains "${case3_stderr}" "dry-run=1"
assert_contains "${case3_stdout}" "# Shared hard rules"
assert_not_contains "${case3_stderr}" "DONE:"
[[ ! -f "${TMP_DIR}/codex.log" ]] || fail "dry-run should not invoke codex or write log"

echo "spawn-codex regression tests passed."
