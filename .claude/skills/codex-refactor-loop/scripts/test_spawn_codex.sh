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

run_dry_expect_exit() {
  local prompt_file="$1"
  local stdout_file="$2"
  local stderr_file="$3"
  local expected_exit="$4"
  local actual_exit

  set +e
  "${SPAWN_CODEX}" \
    --cd "${TMP_DIR}" \
    --prompt "${prompt_file}" \
    --log "${TMP_DIR}/codex.log" \
    --timeout 3600 \
    --dry-run \
    >"${stdout_file}" \
    2>"${stderr_file}"
  actual_exit=$?
  set -e

  [[ "${actual_exit}" -eq "${expected_exit}" ]] || fail "expected exit ${expected_exit}, got ${actual_exit}"
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

case4_prompt="${TMP_DIR}/role-blank-cluster.md"
case4_stdout="${TMP_DIR}/case4.stdout"
case4_stderr="${TMP_DIR}/case4.stderr"
cat >"${case4_prompt}" <<'PROMPT'
# Invalid blank cluster

cluster ''
PROMPT

run_dry_expect_exit "${case4_prompt}" "${case4_stdout}" "${case4_stderr}" 2
assert_contains "${case4_stderr}" "unresolved or blank placeholders"

case5_prompt="${TMP_DIR}/role-missing-audit-num.md"
case5_stdout="${TMP_DIR}/case5.stdout"
case5_stderr="${TMP_DIR}/case5.stderr"
cat >"${case5_prompt}" <<'PROMPT'
# Invalid missing audit number

Read /tmp/audit-iter-MISSING-NUM.md before continuing.
PROMPT

run_dry_expect_exit "${case5_prompt}" "${case5_stdout}" "${case5_stderr}" 2

case6_prompt="${TMP_DIR}/role-unresolved-envsubst.md"
case6_stdout="${TMP_DIR}/case6.stdout"
case6_stderr="${TMP_DIR}/case6.stderr"
cat >"${case6_prompt}" <<'PROMPT'
# Invalid unresolved envsubst

cluster ${CLUSTER_ID}
PROMPT

run_dry_expect_exit "${case6_prompt}" "${case6_stdout}" "${case6_stderr}" 2

case7_prompt="${TMP_DIR}/role-valid-placeholder-check.md"
case7_stdout="${TMP_DIR}/case7.stdout"
case7_stderr="${TMP_DIR}/case7.stderr"
cat >"${case7_prompt}" <<'PROMPT'
# Valid prompt

cluster cluster-1159
Read /tmp/audit-iter-42.md before continuing.
PROMPT

run_dry_expect_exit "${case7_prompt}" "${case7_stdout}" "${case7_stderr}" 0

case8_dir="${TMP_DIR}/case8-state"
case8_prompt="${TMP_DIR}/role-dry-run-marker.md"
case8_stdout="${TMP_DIR}/case8.stdout"
case8_stderr="${TMP_DIR}/case8.stderr"
case8_log="${case8_dir}/logs/dry-run-marker.log"
mkdir -p "${case8_dir}/logs"
cat >"${case8_prompt}" <<'PROMPT'
# Dry run marker

No marker should be written.
PROMPT

"${SPAWN_CODEX}" \
  --cd "${TMP_DIR}" \
  --prompt "${case8_prompt}" \
  --log "${case8_log}" \
  --timeout 3600 \
  --dry-run \
  >"${case8_stdout}" \
  2>"${case8_stderr}"
[[ ! -e "${case8_dir}/markers/dry-run-marker.running.json" ]] || fail "case 8 dry-run wrote running marker"
[[ ! -e "${case8_dir}/markers/dry-run-marker.done.json" ]] || fail "case 8 dry-run wrote done marker"

case9_dir="${TMP_DIR}/case9-state"
case9_bin="${TMP_DIR}/case9-bin"
case9_prompt="${TMP_DIR}/role-fake-codex.md"
case9_stdout="${TMP_DIR}/case9.stdout"
case9_stderr="${TMP_DIR}/case9.stderr"
case9_log="${case9_dir}/logs/fake-spawn.log"
mkdir -p "${case9_dir}/logs" "${case9_bin}"
cat >"${case9_prompt}" <<'PROMPT'
# Fake codex

Write a verdict marker.
PROMPT
cat >"${case9_bin}/codex" <<'SH'
#!/usr/bin/env bash
cat >/dev/null
echo "fake codex started"
echo "IMPLEMENT_DONE:markers-event-design:ok"
exit 0
SH
chmod +x "${case9_bin}/codex"

PATH="${case9_bin}:${PATH}" "${SPAWN_CODEX}" \
  --cd "${TMP_DIR}" \
  --prompt "${case9_prompt}" \
  --log "${case9_log}" \
  --timeout 3600 \
  >"${case9_stdout}" \
  2>"${case9_stderr}"

case9_done="${case9_dir}/markers/fake-spawn.done.json"
[[ -f "${case9_done}" ]] || fail "case 9 done marker missing"
[[ ! -e "${case9_dir}/markers/fake-spawn.running.json" ]] || fail "case 9 running marker should have been renamed"
[[ "$(jq -r '.state' "${case9_done}")" = "done" ]] || fail "case 9 state was not done"
[[ "$(jq -r '.base' "${case9_done}")" = "fake-spawn" ]] || fail "case 9 base mismatch"
[[ "$(jq -r '.log_path' "${case9_done}")" = "${case9_log}" ]] || fail "case 9 log_path mismatch"
[[ "$(jq -r '.exit_code' "${case9_done}")" = "0" ]] || fail "case 9 exit_code mismatch"
[[ "$(jq -r '.verdict' "${case9_done}")" = "IMPLEMENT_DONE:markers-event-design:ok" ]] || fail "case 9 verdict mismatch"
assert_contains "${case9_log}" "EXIT=0"
assert_contains "${case9_log}" "DONE_AT="

echo "spawn-codex regression tests passed."
