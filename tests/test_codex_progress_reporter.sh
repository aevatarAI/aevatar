#!/usr/bin/env bash

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
REPORTER="${REPO_ROOT}/.claude/skills/codex-refactor-loop/scripts/codex-progress-reporter.sh"
TMP_DIR="$(mktemp -d /tmp/codex-progress-reporter-test.XXXXXXXX)"

cleanup() {
  rm -rf "${TMP_DIR}"
}
trap cleanup EXIT

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

assert_file_exists() {
  [[ -f "$1" ]] || fail "expected file to exist: $1"
}

assert_file_missing() {
  [[ ! -e "$1" ]] || fail "expected file to be removed: $1"
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

export GH_MOCK_DIR="${TMP_DIR}/gh"
export GH_LOG="${GH_MOCK_DIR}/calls.log"
mkdir -p "${GH_MOCK_DIR}/bodies"
: >"${GH_LOG}"
printf '1001\n' >"${GH_MOCK_DIR}/next-comment-id"

gh() {
  local args=("$@")
  printf '%s\n' "$*" >>"${GH_LOG}"

  if [[ "${args[0]:-}" = "pr" && "${args[1]:-}" = "view" ]]; then
    return 0
  fi

  if [[ "${args[0]:-}" =~ ^(pr|issue)$ && "${args[1]:-}" = "comment" ]]; then
    local target="${args[2]:-missing}"
    local body_file=""
    local arg
    for arg in "${args[@]}"; do
      if [[ "${arg}" = --body-file ]]; then
        continue
      fi
    done
    for ((i = 0; i < ${#args[@]}; i++)); do
      if [[ "${args[$i]}" = "--body-file" ]]; then
        body_file="${args[$((i + 1))]}"
      fi
    done
    [[ -n "${body_file}" && -f "${body_file}" ]] || return 1
    local id
    id="$(cat "${GH_MOCK_DIR}/next-comment-id")"
    printf '%s\n' "$((id + 1))" >"${GH_MOCK_DIR}/next-comment-id"
    cp "${body_file}" "${GH_MOCK_DIR}/bodies/post-${target}-${id}.md"
    echo "https://github.com/aevatarAI/aevatar/pull/${target}#issuecomment-${id}"
    return 0
  fi

  if [[ "${args[0]:-}" = "api" ]]; then
    local method=""
    local path=""
    local body_file=""
    for ((i = 1; i < ${#args[@]}; i++)); do
      case "${args[$i]}" in
        -X)
          method="${args[$((i + 1))]}"
          i=$((i + 1))
          ;;
        repos/*)
          path="${args[$i]}"
          ;;
        body=@*)
          body_file="${args[$i]#body=@}"
          ;;
      esac
    done

    if [[ "${method}" = "PATCH" ]]; then
      [[ -n "${body_file}" && -f "${body_file}" ]] || return 1
      local cid="${path##*/}"
      cp "${body_file}" "${GH_MOCK_DIR}/bodies/patch-${cid}.md"
      return 0
    fi

    if [[ "${method}" = "DELETE" ]]; then
      local cid="${path##*/}"
      touch "${GH_MOCK_DIR}/deleted-${cid}"
      return 0
    fi
  fi

  return 1
}
export -f gh

reset_gh_log() {
  : >"${GH_LOG}"
  rm -f "${GH_MOCK_DIR}"/deleted-* "${GH_MOCK_DIR}"/bodies/*.md
}

make_case_repo() {
  local name="$1"
  local dir="${TMP_DIR}/${name}"
  mkdir -p "${dir}/.refactor-loop/markers" "${dir}/.refactor-loop/logs" "${dir}/.refactor-loop/prompts"
  echo "{}" >"${dir}/.refactor-loop/codex-progress-state.json"
  printf '%s\n' "${dir}"
}

write_marker() {
  local file="$1"
  local base="$2"
  local log_path="$3"
  local state="$4"
  jq -n \
    --arg base "${base}" \
    --arg log_path "${log_path}" \
    --arg state "${state}" \
    '{base: $base, log_path: $log_path, state: $state}' \
    >"${file}"
}

run_reporter_once() {
  local repo="$1"
  CODEX_PROGRESS_REPO_ROOT="${repo}" CODEX_PROGRESS_REPORTER_RUN_ONCE=1 bash "${REPORTER}" \
    >"${repo}/stdout.log" \
    2>"${repo}/stderr.log"
}

case1_repo="$(make_case_repo case1-running-post)"
case1_base="review-pr1172-running"
case1_log="${case1_repo}/.refactor-loop/logs/${case1_base}.log"
printf 'line one\nline two\n' >"${case1_log}"
write_marker \
  "${case1_repo}/.refactor-loop/markers/${case1_base}.running.json" \
  "${case1_base}" \
  "${case1_log}" \
  "running"
reset_gh_log
run_reporter_once "${case1_repo}"
assert_contains "${GH_LOG}" "pr comment 1172 --body-file"
assert_file_exists "${GH_MOCK_DIR}/bodies/post-1172-1001.md"
assert_contains "${GH_MOCK_DIR}/bodies/post-1172-1001.md" "codex 进展 ${case1_base}"
[[ "$(jq -r --arg k "${case1_base}" '.[$k].comment_id' "${case1_repo}/.refactor-loop/codex-progress-state.json")" = "1001" ]] \
  || fail "case 1 comment id was not recorded"

case2_repo="$(make_case_repo case2-running-patch)"
case2_base="review-pr1172-patch"
case2_log="${case2_repo}/.refactor-loop/logs/${case2_base}.log"
printf 'updated line\n' >"${case2_log}"
jq -n --arg k "${case2_base}" \
  '.[$k] = {target: "1172", kind: "pr", comment_id: 2222, last_md5: "old", finished: false}' \
  >"${case2_repo}/.refactor-loop/codex-progress-state.json"
write_marker \
  "${case2_repo}/.refactor-loop/markers/${case2_base}.running.json" \
  "${case2_base}" \
  "${case2_log}" \
  "running"
reset_gh_log
run_reporter_once "${case2_repo}"
assert_contains "${GH_LOG}" "api -X PATCH repos/aevatarAI/aevatar/issues/comments/2222"
assert_file_exists "${GH_MOCK_DIR}/bodies/patch-2222.md"
assert_contains "${GH_MOCK_DIR}/bodies/patch-2222.md" "updated line"

case3_repo="$(make_case_repo case3-done-cleanup)"
case3_base="review-pr1172-done"
case3_log="${case3_repo}/.refactor-loop/logs/${case3_base}.log"
case3_marker="${case3_repo}/.refactor-loop/markers/${case3_base}.done.json"
jq -n --arg k "${case3_base}" \
  '.[$k] = {target: "1172", kind: "pr", comment_id: 3333, last_md5: "old", finished: false}' \
  >"${case3_repo}/.refactor-loop/codex-progress-state.json"
write_marker "${case3_marker}" "${case3_base}" "${case3_log}" "done"
reset_gh_log
run_reporter_once "${case3_repo}"
assert_file_exists "${GH_MOCK_DIR}/deleted-3333"
assert_file_missing "${case3_marker}"

case3b_base="review-pr1172-done-no-comment"
case3b_marker="${case3_repo}/.refactor-loop/markers/${case3b_base}.done.json"
write_marker "${case3b_marker}" "${case3b_base}" "${case3_repo}/missing.log" "done"
reset_gh_log
run_reporter_once "${case3_repo}"
assert_not_contains "${GH_LOG}" "comment 1172"
assert_file_missing "${case3b_marker}"

case4_repo="$(make_case_repo case4-zombie-skip)"
case4_base="review-pr1172-zombie"
case4_log="${case4_repo}/.refactor-loop/logs/${case4_base}.log"
case4_marker="${case4_repo}/.refactor-loop/markers/${case4_base}.running.json"
printf 'still no exit marker\n' >"${case4_log}"
write_marker "${case4_marker}" "${case4_base}" "${case4_log}" "running"
touch -t 202001010000 "${case4_log}"
reset_gh_log
run_reporter_once "${case4_repo}"
assert_file_exists "${case4_marker}"
assert_not_contains "${GH_LOG}" "comment 1172"
assert_contains "${case4_repo}/stderr.log" "stale log without EXIT"

case5_repo="$(make_case_repo case5-invalid-skip)"
case5_marker="${case5_repo}/.refactor-loop/markers/broken.running.json"
printf '{ not json\n' >"${case5_marker}"
reset_gh_log
run_reporter_once "${case5_repo}"
assert_file_exists "${case5_marker}"
assert_not_contains "${GH_LOG}" "comment"
assert_contains "${case5_repo}/stderr.log" "skip invalid marker"

case6_repo="$(make_case_repo case6-missing-log-skip)"
case6_base="review-pr1172-missing-log"
case6_marker="${case6_repo}/.refactor-loop/markers/${case6_base}.running.json"
write_marker "${case6_marker}" "${case6_base}" "${case6_repo}/.refactor-loop/logs/missing.log" "running"
reset_gh_log
run_reporter_once "${case6_repo}"
assert_file_exists "${case6_marker}"
assert_not_contains "${GH_LOG}" "comment 1172"
assert_contains "${case6_repo}/stderr.log" "log not found"

case7_repo="$(make_case_repo case7-audit-remote-ci-skip)"
case7_audit_marker="${case7_repo}/.refactor-loop/markers/audit-iter-1172.done.json"
case7_remote_marker="${case7_repo}/.refactor-loop/markers/remote-ci-1172.done.json"
write_marker "${case7_audit_marker}" "audit-iter-1172" "${case7_repo}/missing-audit.log" "done"
write_marker "${case7_remote_marker}" "remote-ci-1172" "${case7_repo}/missing-remote.log" "done"
reset_gh_log
run_reporter_once "${case7_repo}"
assert_file_missing "${case7_audit_marker}"
assert_file_missing "${case7_remote_marker}"
assert_not_contains "${GH_LOG}" "comment"

if grep -vE '^[[:space:]]*#' "${REPORTER}" | grep -Fq 'for log in "$LOG_DIR"/*.log'; then
  fail "reporter still scans logs instead of markers"
fi
if grep -Eq '^LOG_DIR=' "${REPORTER}"; then
  fail "reporter still declares unused LOG_DIR"
fi

echo "codex-progress-reporter regression tests passed."
