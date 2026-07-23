#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
TOOL="$ROOT/tools/schedules/query_member_automation_audit.sh"
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-audit-query-tests.XXXXXX")"
trap 'rm -rf "$TMP_DIR"' EXIT

fail() {
  printf 'FAIL: %s\n' "$1" >&2
  exit 1
}

test -x "$TOOL" || fail "missing executable audit query tool"

export AEVATAR_AUDIT_SCOPE_ID="scope-alpha"
export AEVATAR_AUDIT_TEAM_ID="team-alpha"
export AEVATAR_AUDIT_MEMBER_ID="m-alpha"
export AEVATAR_AUDIT_SCHEDULE_ID="sch-alpha"

cat > "$TMP_DIR/create.log" <<'LOG'
2026-07-24T01:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:00.000100000Z       Accepted Studio member automation create for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-create-alpha, and verified binding binding-alpha.
LOG

cat > "$TMP_DIR/revocation.log" <<'LOG'
2026-07-24T02:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6202]
2026-07-24T02:00:00.000100000Z       Completed Studio member automation revocation for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-delete-alpha, NyxID status Completed, Vault status Completed, state version 42, observed at 2026-07-24T02:00:00.0000000+00:00.
LOG

export AEVATAR_AUDIT_OPERATION_ID="op-create-alpha"
AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/create.log" "$TOOL" create \
  > "$TMP_DIR/create.json"
jq -e '
  type == "array"
  and length == 1
  and (.[0] | keys | sort) == ([
    "bindingId", "memberId", "operationId",
    "scheduleId", "scopeId", "teamId"
  ] | sort)
  and .[0] == {
    scopeId: "scope-alpha",
    teamId: "team-alpha",
    memberId: "m-alpha",
    scheduleId: "sch-alpha",
    operationId: "op-create-alpha",
    bindingId: "binding-alpha"
  }
' "$TMP_DIR/create.json" >/dev/null

export AEVATAR_AUDIT_OPERATION_ID="op-delete-alpha"
AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/revocation.log" "$TOOL" revocation \
  > "$TMP_DIR/revocation.json"
jq -e '
  type == "array"
  and length == 1
  and (.[0] | keys | sort) == ([
    "memberId", "nyxIdRevocationStatus", "observedAtUtc", "operationId",
    "scheduleId", "scopeId", "stateVersion", "teamId", "vaultRevocationStatus"
  ] | sort)
  and .[0].scopeId == "scope-alpha"
  and .[0].teamId == "team-alpha"
  and .[0].memberId == "m-alpha"
  and .[0].scheduleId == "sch-alpha"
  and .[0].operationId == "op-delete-alpha"
  and .[0].nyxIdRevocationStatus == "Completed"
  and .[0].vaultRevocationStatus == "Completed"
  and .[0].stateVersion == 42
  and .[0].observedAtUtc == "2026-07-24T02:00:00.0000000+00:00"
' "$TMP_DIR/revocation.json" >/dev/null

cat > "$TMP_DIR/invalid-observed-at.log" <<'LOG'
2026-07-24T02:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6202]
2026-07-24T02:00:00.000100000Z       Completed Studio member automation revocation for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-delete-alpha, NyxID status Completed, Vault status Completed, state version 42, observed at bearer-sensitive.
LOG
if AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/invalid-observed-at.log" \
    "$TOOL" revocation \
    > "$TMP_DIR/invalid-observed-at.out" \
    2> "$TMP_DIR/invalid-observed-at.err"; then
  fail "invalid observedAtUtc unexpectedly succeeded"
fi
test ! -s "$TMP_DIR/invalid-observed-at.out" \
  || fail "invalid observedAtUtc failure emitted output"
if rg -F 'bearer-sensitive' "$TMP_DIR/invalid-observed-at.err"; then
  fail "invalid observedAtUtc leaked to stderr"
fi

cat > "$TMP_DIR/filter.log" <<'LOG'
2026-07-24T00:59:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T00:59:00.000100000Z       Accepted Studio member automation create for scope scope-other, team team-other, member m-other, schedule sch-other, operation op-other, and verified binding binding-other.
2026-07-24T01:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:00.000100000Z       Accepted Studio member automation create for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-create-alpha, and verified binding binding-alpha.
LOG
export AEVATAR_AUDIT_OPERATION_ID="op-create-alpha"
AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/filter.log" "$TOOL" create \
  > "$TMP_DIR/filter.json"
jq -e 'length == 1 and .[0].bindingId == "binding-alpha"' \
  "$TMP_DIR/filter.json" >/dev/null
if rg -q 'scope-other|binding-other' "$TMP_DIR/filter.json"; then
  fail "mismatched audit record reached output"
fi

cat > "$TMP_DIR/mismatch.log" <<'LOG'
2026-07-24T00:59:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T00:59:00.000100000Z       Accepted Studio member automation create for scope scope-other, team team-other, member m-other, schedule sch-other, operation op-other, and verified binding binding-other.
LOG
if AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/mismatch.log" "$TOOL" create \
    > "$TMP_DIR/mismatch.out" 2> "$TMP_DIR/mismatch.err"; then
  fail "mismatched-only input unexpectedly succeeded"
fi
test ! -s "$TMP_DIR/mismatch.out" || fail "mismatched failure emitted output"

cat > "$TMP_DIR/malformed.log" <<'LOG'
2026-07-24T01:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:00.000100000Z       malformed bearer-sensitive raw-secret-sensitive
LOG
if AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/malformed.log" "$TOOL" create \
    > "$TMP_DIR/malformed.out" 2> "$TMP_DIR/malformed.err"; then
  fail "malformed event unexpectedly succeeded"
fi
test ! -s "$TMP_DIR/malformed.out" || fail "malformed failure emitted output"

cat "$TMP_DIR/create.log" "$TMP_DIR/create.log" > "$TMP_DIR/duplicate.log"
if AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/duplicate.log" "$TOOL" create \
    > "$TMP_DIR/duplicate.out" 2> "$TMP_DIR/duplicate.err"; then
  fail "duplicate event unexpectedly succeeded"
fi
test ! -s "$TMP_DIR/duplicate.out" || fail "duplicate failure emitted output"

cat > "$TMP_DIR/conflict.log" <<'LOG'
2026-07-24T01:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:00.000100000Z       Accepted Studio member automation create for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-create-alpha, and verified binding binding-alpha.
2026-07-24T01:00:01.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:01.000100000Z       Accepted Studio member automation create for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-create-alpha, and verified binding binding-conflict.
LOG
if AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/conflict.log" "$TOOL" create \
    > "$TMP_DIR/conflict.out" 2> "$TMP_DIR/conflict.err"; then
  fail "conflicting event unexpectedly succeeded"
fi
test ! -s "$TMP_DIR/conflict.out" || fail "conflicting failure emitted output"

cat > "$TMP_DIR/secret-unrelated.log" <<'LOG'
2026-07-24T00:58:00.000000000Z warn: Unrelated.Category[9999]
2026-07-24T00:58:00.000100000Z       Authorization: Bearer bearer-sensitive raw Agent Key raw-secret-sensitive ciphertext-sensitive
2026-07-24T01:00:00.000000000Z info: Aevatar.Studio.MemberAutomation[6201]
2026-07-24T01:00:00.000100000Z       Accepted Studio member automation create for scope scope-alpha, team team-alpha, member m-alpha, schedule sch-alpha, operation op-create-alpha, and verified binding binding-alpha.
LOG
AEVATAR_AUDIT_LOG_INPUT="$TMP_DIR/secret-unrelated.log" "$TOOL" create \
  > "$TMP_DIR/secret-unrelated.json"
if rg -qi 'bearer-sensitive|raw-secret-sensitive|ciphertext-sensitive|authorization' \
    "$TMP_DIR/secret-unrelated.json"; then
  fail "secret-like unrelated log content reached output"
fi

mkdir -p "$TMP_DIR/bin"
cat > "$TMP_DIR/bin/kubectl" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" > "$KUBECTL_ARGS_FILE"
cat "$KUBECTL_FIXTURE"
SH
chmod +x "$TMP_DIR/bin/kubectl"
export KUBECTL_ARGS_FILE="$TMP_DIR/kubectl.args"
export KUBECTL_FIXTURE="$TMP_DIR/create.log"
PATH="$TMP_DIR/bin:$PATH" "$TOOL" create > "$TMP_DIR/kubectl.json"
jq -e 'length == 1 and .[0].bindingId == "binding-alpha"' \
  "$TMP_DIR/kubectl.json" >/dev/null
rg -F -- '-n aismart-app-mainnet logs -l app=aevatar-console-backend' \
  "$TMP_DIR/kubectl.args" >/dev/null
rg -F -- '--tail=-1 --timestamps --all-containers=true --since=30m' \
  "$TMP_DIR/kubectl.args" >/dev/null

CANARY_DOC="$ROOT/docs/operations/2026-07-23-scheduled-agent-key-production-canary.md"
rg -F 'AUDIT_QUERY_TOOL="$REPO_ROOT/tools/schedules/query_member_automation_audit.sh"' \
  "$CANARY_DOC" >/dev/null || fail "runbook does not bind the repository audit query tool"
rg -F '"$AUDIT_QUERY_TOOL" create' "$CANARY_DOC" >/dev/null \
  || fail "runbook does not invoke create audit mode"
rg -F '"$AUDIT_QUERY_TOOL" revocation' "$CANARY_DOC" >/dev/null \
  || fail "runbook does not invoke revocation audit mode"
if rg -q 'APPROVED_(CREATE|REVOCATION)_AUDIT_QUERY' "$CANARY_DOC"; then
  fail "runbook still depends on unspecified audit executables"
fi

FAILURE_CONTEXT_FILE="$TMP_DIR/failure-context.sh"
awk '
  /^set_failure_context\(\) \{/ {copy = 1}
  copy {print}
  copy && /^}/ {exit}
' "$CANARY_DOC" > "$FAILURE_CONTEXT_FILE"
test -s "$FAILURE_CONTEXT_FILE" || fail "missing failure context function"
bash -c '
  set -euo pipefail
  SCOPE_ID="scope-runtime"
  TEAM_ID="team-runtime"
  MEMBER_ID="m-runtime"
  SCHEDULE_ID="sch-runtime"
  RUN_ID="run-runtime"
  source "$1"

  set_failure_context "create" "op-create" "http_202" "create_failed" "11"
  test "$FAILURE_PHASE" = "create"
  test "$FAILURE_OPERATION_ID" = "op-create"
  test "$FAILURE_STATUS" = "http_202"
  test "$FAILURE_CODE" = "create_failed"
  test "$FAILURE_STATE_VERSION" = "11"
  test "$FAILURE_SCOPE_ID" = "scope-runtime"
  test "$FAILURE_TEAM_ID" = "team-runtime"
  test "$FAILURE_MEMBER_ID" = "m-runtime"
  test "$FAILURE_SCHEDULE_ID" = "sch-runtime"
  test "$FAILURE_RUN_ID" = "run-runtime"

  set_failure_context "run" "op-run" "running" "run_failed" "12"
  test "$FAILURE_PHASE/$FAILURE_OPERATION_ID" = "run/op-run"
  set_failure_context "delete" "op-delete" "http_202" "delete_failed" "13"
  test "$FAILURE_PHASE/$FAILURE_OPERATION_ID" = "delete/op-delete"
  set_failure_context "revocation" "op-delete" "pending" "revocation_failed" "14"
  test "$FAILURE_PHASE/$FAILURE_OPERATION_ID" = "revocation/op-delete"
  set_failure_context "cleanup" "" "retiring" "cleanup_failed" "15"
  test "$FAILURE_PHASE" = "cleanup"
  test -z "$FAILURE_OPERATION_ID"
' _ "$FAILURE_CONTEXT_FILE"

rg -F "trap 'handle_canary_exit \"\$?\"' EXIT" "$CANARY_DOC" >/dev/null \
  || fail "runbook does not install the status-preserving EXIT trap"
if rg -F 'trap - ERR' "$CANARY_DOC" >/dev/null; then
  fail "runbook still disables the obsolete ERR trap"
fi

FAILURE_HELPERS_FILE="$TMP_DIR/failure-helpers.sh"
awk '
  /^set_failure_context\(\) \{/ {copy = 1}
  copy {print}
  copy && /^trap .*handle_canary_exit.* EXIT$/ {found = 1; exit}
  END {if (!found) exit 1}
' "$CANARY_DOC" > "$FAILURE_HELPERS_FILE"
test -s "$FAILURE_HELPERS_FILE" || fail "missing documented failure helpers"

mkdir -p "$TMP_DIR/failure-exit" "$TMP_DIR/failure-success"
set +e
bash -c '
  set -euo pipefail
  CANARY_STATE_DIR="$1"
  SCOPE_ID="scope-exit"
  TEAM_ID="team-exit"
  MEMBER_ID="m-exit"
  SCHEDULE_ID="sch-exit"
  RUN_ID="run-exit"
  source "$2"
  set_failure_context \
    "cleanup_member" "" "http_500" "member_delete_failed" "42"
  exit 7
' _ "$TMP_DIR/failure-exit" "$FAILURE_HELPERS_FILE"
FAILURE_EXIT_STATUS=$?
set -e
test "$FAILURE_EXIT_STATUS" = "7" \
  || fail "EXIT trap did not preserve explicit status 7"
jq -e '
  (keys | sort) == ([
    "failureCode", "memberId", "observedAtUtc", "operationId", "runId",
    "scheduleId", "scopeId", "stateVersion", "status", "teamId"
  ] | sort)
  and .scopeId == "scope-exit"
  and .teamId == "team-exit"
  and .memberId == "m-exit"
  and .scheduleId == "sch-exit"
  and .runId == "run-exit"
  and .operationId == ""
  and .status == "http_500"
  and .failureCode == "member_delete_failed"
  and .stateVersion == "42"
  and (.observedAtUtc | test(
    "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$"
  ))
' "$TMP_DIR/failure-exit/failure-allowlist.json" >/dev/null

bash -c '
  set -euo pipefail
  CANARY_STATE_DIR="$1"
  SCOPE_ID="scope-success"
  TEAM_ID="team-success"
  MEMBER_ID="m-success"
  SCHEDULE_ID="sch-success"
  RUN_ID="run-success"
  source "$2"
  set_failure_context "final_readiness" "" "http_200" "final_readiness_failed" "0"
  exit 0
' _ "$TMP_DIR/failure-success" "$FAILURE_HELPERS_FILE"
test ! -e "$TMP_DIR/failure-success/failure-allowlist.json" \
  || fail "successful EXIT unexpectedly emitted failure evidence"

FAILURE_COUNT_FILE="$TMP_DIR/failure-writer-count"
set +e
bash -c '
  set -euo pipefail
  CANARY_STATE_DIR="$1"
  SCOPE_ID="scope-writer"
  TEAM_ID="team-writer"
  MEMBER_ID="m-writer"
  SCHEDULE_ID="sch-writer"
  RUN_ID="run-writer"
  source "$2"
  FAILURE_COUNT_FILE="$3"
  record_failure() {
    printf x >> "$FAILURE_COUNT_FILE"
    exit 9
  }
  exit 7
' _ "$TMP_DIR/failure-exit" "$FAILURE_HELPERS_FILE" "$FAILURE_COUNT_FILE"
FAILURE_WRITER_STATUS=$?
set -e
test "$FAILURE_WRITER_STATUS" = "7" \
  || fail "failure-writer error replaced the original exit status"
test "$(wc -c < "$FAILURE_COUNT_FILE" | tr -d ' ')" = "1" \
  || fail "EXIT trap invoked failure evidence more than once"

for expected_call in \
  'set_failure_context "create" "$CREATE_OPERATION_ID"' \
  'set_failure_context "run" "$RUN_OPERATION_ID"' \
  'set_failure_context "delete" "$DELETE_OPERATION_ID"' \
  'set_failure_context "revocation" "$DELETE_OPERATION_ID"'
do
  rg -F "$expected_call" "$CANARY_DOC" >/dev/null \
    || fail "missing failure context call: $expected_call"
done
if rg -q 'RUN_OPERATION_ID:-\$\{CREATE_OPERATION_ID' "$CANARY_DOC"; then
  fail "failure evidence still falls back across operation identities"
fi
if rg -F 'set_failure_context "cleanup" ""' "$CANARY_DOC"; then
  fail "runbook still uses one generic cleanup failure context"
fi

line_of_unique() {
  local needle="$1" matches count
  matches="$(rg -n -F -- "$needle" "$CANARY_DOC" || true)"
  count="$(printf '%s\n' "$matches" | sed '/^$/d' | wc -l | tr -d ' ')"
  test "$count" = "1" || fail "expected one runbook line: $needle"
  printf '%s\n' "${matches%%:*}"
}

assert_context_order() {
  local label="$1" pre="$2" response="$3" post="$4" response_gap="${5:-1}"
  local pre_line response_line post_line
  pre_line="$(line_of_unique "$pre")"
  response_line="$(line_of_unique "$response")"
  post_line="$(line_of_unique "$post")"
  if ! test "$pre_line" -lt "$response_line" \
      || ! test "$post_line" -eq "$((response_line + response_gap))"; then
    fail "$label failure context is not ordered around its response"
  fi
}

assert_context_order \
  "revision retire" \
  'set_failure_context "cleanup_revision" "" "not_observed" "revision_retire_failed" "0"' \
  '"$CANARY_STATE_DIR/retire-revision.json")"' \
  'set_failure_context "cleanup_revision" "" "http_$STATUS" "revision_retire_failed" "0"'
assert_context_order \
  "member delete" \
  'set_failure_context "cleanup_member" "" "not_observed" "member_delete_failed" "0"' \
  '"$CANARY_STATE_DIR/delete-member.json")"' \
  'set_failure_context "cleanup_member" "" "http_$STATUS" "member_delete_failed" "0"'
assert_context_order \
  "member delete observation" \
  'set_failure_context "cleanup_member_observe" "" "not_observed" "member_delete_observe_failed" "0"' \
  '"$CANARY_STATE_DIR/member-after-delete.json")"' \
  'set_failure_context "cleanup_member_observe" "" "http_$STATUS" "member_delete_observe_failed" "0"'
assert_context_order \
  "draft delete" \
  'set_failure_context "cleanup_draft" "" "not_observed" "draft_delete_failed" "0"' \
  '"$CANARY_STATE_DIR/delete-draft.json")"' \
  'set_failure_context "cleanup_draft" "" "http_$STATUS" "draft_delete_failed" "0"'
assert_context_order \
  "draft delete observation" \
  'set_failure_context "cleanup_draft_observe" "" "not_observed" "draft_delete_observe_failed" "0"' \
  '"$CANARY_STATE_DIR/draft-after-delete.json")"' \
  'set_failure_context "cleanup_draft_observe" "" "http_$STATUS" "draft_delete_observe_failed" "0"'
assert_context_order \
  "Team archive" \
  'set_failure_context "cleanup_team" "" "not_observed" "team_archive_failed" "0"' \
  '"$CANARY_STATE_DIR/archive-team.json")"' \
  'set_failure_context "cleanup_team" "" "http_$STATUS" "team_archive_failed" "0"'
assert_context_order \
  "Team archive observation" \
  'set_failure_context "cleanup_team_observe" "" "not_observed" "team_archive_observe_failed" "0"' \
  '"$CANARY_STATE_DIR/team-after-archive.json")"' \
  'set_failure_context "cleanup_team_observe" "" "http_$STATUS" "team_archive_observe_failed" "0"'
assert_context_order \
  "final UserConfig observation" \
  'set_failure_context "final_user_config" "" "not_observed" "final_user_config_failed" "0"' \
  '"$CANARY_STATE_DIR/user-config-after.json")"' \
  'set_failure_context "final_user_config" "" "http_$STATUS" "final_user_config_failed" "0"'
assert_context_order \
  "final readiness observation" \
  'set_failure_context "final_readiness" "" "not_observed" "final_readiness_failed" "0"' \
  '--output "$CANARY_STATE_DIR/final-ready.json" --write-out' \
  'set_failure_context "final_readiness" "" "http_$HTTP_STATUS" "final_readiness_failed" "0"' \
  2

FINAL_KEY_CONTEXT='set_failure_context "final_key" "" "not_observed" "final_key_observe_failed" "0"'
FINAL_KEY_POST='set_failure_context "final_key" "" "observed" "final_key_observe_failed" "0"'
FINAL_KEY_LINES="$(rg -n -F '"$CANARY_STATE_DIR/api-key-final.json"' "$CANARY_DOC")"
test "$(printf '%s\n' "$FINAL_KEY_LINES" | wc -l | tr -d ' ')" = "2" \
  || fail "final key observation does not have exactly one capture and assertion"
FINAL_KEY_CAPTURE_LINE="$(printf '%s\n' "$FINAL_KEY_LINES" | head -1 | cut -d: -f1)"
FINAL_KEY_ASSERT_LINE="$(printf '%s\n' "$FINAL_KEY_LINES" | tail -1 | cut -d: -f1)"
test "$(line_of_unique "$FINAL_KEY_CONTEXT")" -lt "$FINAL_KEY_CAPTURE_LINE" \
  && test "$FINAL_KEY_CAPTURE_LINE" -lt "$(line_of_unique "$FINAL_KEY_POST")" \
  && test "$(line_of_unique "$FINAL_KEY_POST")" -lt "$FINAL_KEY_ASSERT_LINE" \
  || fail "final key observation lacks ordered pre/post context"

rg -F 'test "$USER_CONFIG_DISPOSITION" = "leave_selected"' "$CANARY_DOC" >/dev/null \
  || fail "runbook does not require leave_selected disposition"
rg -F 'USER_CONFIG_LEAVE_SELECTED_APPROVED' "$CANARY_DOC" >/dev/null \
  || fail "runbook lacks explicit leave-selected approval"
if rg -q 'case "\$USER_CONFIG_DISPOSITION" in restore\|keep' "$CANARY_DOC"; then
  fail "runbook still accepts unsupported restore disposition"
fi

printf '%s\n' 'scheduled member automation audit query tests: PASS'
