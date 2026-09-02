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
if rg -q 'scope-alpha|team-alpha|m-alpha|sch-alpha|op-create-alpha|binding-alpha' \
    "$TMP_DIR/duplicate.out" "$TMP_DIR/duplicate.err"; then
  fail "duplicate failure leaked an audit field value"
fi

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
if rg -q 'scope-alpha|team-alpha|m-alpha|sch-alpha|op-create-alpha|binding-alpha|binding-conflict' \
    "$TMP_DIR/conflict.out" "$TMP_DIR/conflict.err"; then
  fail "conflicting failure leaked an audit field value"
fi

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
rg -F '"/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/preflight"' \
  "$CANARY_DOC" >/dev/null || fail "runbook lost the retained nested preflight route"
if rg -q 'APPROVED_(CREATE|REVOCATION)_AUDIT_QUERY' "$CANARY_DOC"; then
  fail "runbook still depends on unspecified audit executables"
fi

CANONICAL_DELETE_LINES="$(
  awk '
    /api_request DELETE/ {
      in_delete = 1
      start_line = NR
      block = $0 "\n"
      next
    }
    in_delete {
      block = block $0 "\n"
      if (/\)"$/) {
        if (block ~ /"\/api\/schedules\/\$SCHEDULE_ID"/ &&
            block ~ /"\$CANARY_STATE_DIR\/delete\.json"/) {
          print start_line
        }
        in_delete = 0
        block = ""
      }
    }
  ' "$CANARY_DOC"
)"
CANONICAL_DELETE_CALLS="$(
  printf '%s\n' "$CANONICAL_DELETE_LINES" |
    awk 'NF {count++} END {print count + 0}'
)"
test "$CANONICAL_DELETE_CALLS" -eq 2 \
  || fail "expected 2 canonical cleanup DELETE calls, found $CANONICAL_DELETE_CALLS"

NESTED_AUTOMATION_DELETE_CALLS="$(
  awk '
    /api_request DELETE/ {
      in_delete = 1
      block = $0 "\n"
      next
    }
    in_delete {
      block = block $0 "\n"
      if (/\)"$/) {
        if (block ~ /\/automations\/\$SCHEDULE_ID/) {
          count++
        }
        in_delete = 0
        block = ""
      }
    }
    END {print count + 0}
  ' "$CANARY_DOC"
)"
test "$NESTED_AUTOMATION_DELETE_CALLS" -eq 0 \
  || fail "runbook still calls a nested automation DELETE route"
if rg -q 'retry-revocation' "$CANARY_DOC"; then
  fail "runbook still advertises a public retry-revocation action"
fi

SECOND_CANONICAL_DELETE_LINE="$(
  printf '%s\n' "$CANONICAL_DELETE_LINES" | sed -n '2p'
)"
RETRY_READ_BEARER_LINE="$(
  awk '
    /^if test "\$REVOCATION_RETRY_REQUIRED" = "true"; then$/ {
      in_retry = 1
    }
    in_retry && /^  read_bearer$/ {
      print NR
      exit
    }
  ' "$CANARY_DOC"
)"
test -n "$RETRY_READ_BEARER_LINE" \
  && test "$RETRY_READ_BEARER_LINE" -lt "$SECOND_CANONICAL_DELETE_LINE" \
  || fail "canonical DELETE replay does not derive a fresh bearer first"

DELETE_BODY_BUILDER="$TMP_DIR/canonical-delete-body.sh"
if ! awk '
  /^set_failure_context "delete" "\$DELETE_OPERATION_ID"$/ {
    in_delete = 1
    next
  }
  in_delete && /^jq -n \\$/ {
    copy = 1
  }
  copy {
    print
  }
  copy && /^'\'' > "\$CANARY_STATE_DIR\/delete\.json"$/ {
    found = 1
    exit
  }
  END {
    if (!found) {
      exit 1
    }
  }
' "$CANARY_DOC" > "$DELETE_BODY_BUILDER"; then
  fail "runbook does not define one canonical delete.json body"
fi
if ! (
  set -euo pipefail
  export CANARY_STATE_DIR="$TMP_DIR/canonical-delete-state"
  export DELETE_OPERATION_ID="delete-operation-alpha"
  export DELETE_IDEMPOTENCY_KEY="delete-idempotency-alpha"
  export SCOPE_ID="scope-alpha"
  export TEAM_ID="team-alpha"
  export MEMBER_ID="m-alpha"
  mkdir -p "$CANARY_STATE_DIR"
  source "$DELETE_BODY_BUILDER"
  jq -e '
    (keys | sort) == ([
      "idempotencyKey",
      "operationId",
      "owner",
      "reason"
    ] | sort)
    and .reason == "scheduled_agent_key_canary_cleanup"
    and .operationId == "delete-operation-alpha"
    and .idempotencyKey == "delete-idempotency-alpha"
    and (.owner | keys | sort) == ([
      "kind",
      "memberId",
      "scopeId",
      "teamId"
    ] | sort)
    and .owner == {
      kind: "studio_member_automation",
      scopeId: "scope-alpha",
      teamId: "team-alpha",
      memberId: "m-alpha"
    }
  ' "$CANARY_STATE_DIR/delete.json" >/dev/null
); then
  fail "runbook canonical delete.json contract drifted"
fi

STATE_HELPERS_FILE="$TMP_DIR/canary-state-helpers.sh"
if ! awk '
  /^CANARY_STATE_CONTRACT_VERSION=/ {copy = 1}
  copy {print}
  /^remove_canary_state_dir\(\) \{/ {in_remove = 1}
  copy && in_remove && /^}/ {found = 1; exit}
  END {if (!found) exit 1}
' "$CANARY_DOC" > "$STATE_HELPERS_FILE"; then
  fail "runbook does not define owned canary state helpers"
fi

STATE_INITIALIZATION_FILE="$TMP_DIR/canary-state-initialization.sh"
if ! awk '
  /^if ! VALIDATED_CANARY_STATE_DIR=/ {copy = 1}
  copy {print}
  copy && /^chmod 600 "\$LEDGER"$/ {found = 1; exit}
  END {if (!found) exit 1}
' "$CANARY_DOC" > "$STATE_INITIALIZATION_FILE"; then
  fail "runbook does not explicitly guard canary state initialization"
fi

if ! (
  set -euo pipefail
  source "$STATE_HELPERS_FILE"

  STATE_FIXTURE_ROOT="$TMP_DIR/canary-state"
  mkdir -p "$STATE_FIXTURE_ROOT/tmp"
  export TMPDIR="$STATE_FIXTURE_ROOT/tmp"

  HOME="$STATE_FIXTURE_ROOT/home"
  mkdir -m 700 "$HOME"
  : > "$HOME/must-survive"
  if remove_canary_state_dir "$HOME" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$HOME/must-survive"

  ARBITRARY_DIR="$STATE_FIXTURE_ROOT/arbitrary"
  mkdir -m 700 "$ARBITRARY_DIR"
  : > "$ARBITRARY_DIR/must-survive"
  if create_or_resume_canary_state_dir "$ARBITRARY_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  if remove_canary_state_dir "$ARBITRARY_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$ARBITRARY_DIR/must-survive"
  test ! -e "$ARBITRARY_DIR/$CANARY_STATE_SENTINEL_NAME"

  COPIED_SOURCE="$(create_or_resume_canary_state_dir "")"
  COPIED_DIR="$STATE_FIXTURE_ROOT/copied-sentinel"
  mkdir -m 700 "$COPIED_DIR"
  cp "$COPIED_SOURCE/$CANARY_STATE_SENTINEL_NAME" \
    "$COPIED_DIR/$CANARY_STATE_SENTINEL_NAME"
  chmod 600 "$COPIED_DIR/$CANARY_STATE_SENTINEL_NAME"
  : > "$COPIED_DIR/must-survive"
  if create_or_resume_canary_state_dir "$COPIED_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  if remove_canary_state_dir "$COPIED_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$COPIED_DIR/must-survive"
  remove_canary_state_dir "$COPIED_SOURCE"

  MOVED_SOURCE="$(create_or_resume_canary_state_dir "")"
  MOVED_DIR="$STATE_FIXTURE_ROOT/moved-state"
  mv "$MOVED_SOURCE" "$MOVED_DIR"
  : > "$MOVED_DIR/must-survive"
  if create_or_resume_canary_state_dir "$MOVED_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  if remove_canary_state_dir "$MOVED_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$MOVED_DIR/must-survive"

  SYMLINK_DIR="$(create_or_resume_canary_state_dir "")"
  rm "$SYMLINK_DIR/$CANARY_STATE_SENTINEL_NAME"
  ln -s "$ARBITRARY_DIR/must-survive" \
    "$SYMLINK_DIR/$CANARY_STATE_SENTINEL_NAME"
  : > "$SYMLINK_DIR/must-survive"
  if remove_canary_state_dir "$SYMLINK_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$SYMLINK_DIR/must-survive"

  WRONG_MODE_DIR="$(create_or_resume_canary_state_dir "")"
  chmod 755 "$WRONG_MODE_DIR"
  : > "$WRONG_MODE_DIR/must-survive"
  if remove_canary_state_dir "$WRONG_MODE_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$WRONG_MODE_DIR/must-survive"

  WRONG_SENTINEL_MODE_DIR="$(create_or_resume_canary_state_dir "")"
  chmod 644 "$WRONG_SENTINEL_MODE_DIR/$CANARY_STATE_SENTINEL_NAME"
  : > "$WRONG_SENTINEL_MODE_DIR/must-survive"
  if remove_canary_state_dir "$WRONG_SENTINEL_MODE_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$WRONG_SENTINEL_MODE_DIR/must-survive"

  WRONG_OWNER_DIR="$(create_or_resume_canary_state_dir "")"
  : > "$WRONG_OWNER_DIR/must-survive"
  bash -c '
    set -euo pipefail
    source "$1"
    WRONG_OWNER_ID="$(( $(id -u) + 1 ))"
    canary_path_owner_id() {
      printf "%s\n" "$WRONG_OWNER_ID"
    }
    if remove_canary_state_dir "$2" >/dev/null 2>&1; then
      exit 1
    fi
    test -f "$2/must-survive"
  ' _ "$STATE_HELPERS_FILE" "$WRONG_OWNER_DIR"
  remove_canary_state_dir "$WRONG_OWNER_DIR"

  MALFORMED_DIR="$(create_or_resume_canary_state_dir "")"
  printf '{}\n' > "$MALFORMED_DIR/$CANARY_STATE_SENTINEL_NAME"
  chmod 600 "$MALFORMED_DIR/$CANARY_STATE_SENTINEL_NAME"
  : > "$MALFORMED_DIR/must-survive"
  if remove_canary_state_dir "$MALFORMED_DIR" >/dev/null 2>&1; then
    exit 1
  fi
  test -f "$MALFORMED_DIR/must-survive"

  OWNED_DIR="$(create_or_resume_canary_state_dir "")"
  test "$(create_or_resume_canary_state_dir "$OWNED_DIR")" = "$OWNED_DIR"
  test "$(validate_canary_state_dir "$OWNED_DIR")" = "$OWNED_DIR"
  remove_canary_state_dir "$OWNED_DIR"
  test ! -e "$OWNED_DIR"
); then
  fail "runbook canary state ownership safety check failed"
fi

NO_WRITE_DIR="$TMP_DIR/canary-state-no-write"
mkdir -m 700 "$NO_WRITE_DIR"
: > "$NO_WRITE_DIR/must-survive"
set +e
bash -c '
  set -u
  source "$1"
  CANARY_STATE_DIR="$2"
  source "$3"
  : > "$4"
' _ \
  "$STATE_HELPERS_FILE" \
  "$NO_WRITE_DIR" \
  "$STATE_INITIALIZATION_FILE" \
  "$TMP_DIR/invalid-state-initialization-continued" \
  > "$TMP_DIR/invalid-state-initialization.out" \
  2> "$TMP_DIR/invalid-state-initialization.err"
INVALID_STATE_STATUS=$?
set -e
test "$INVALID_STATE_STATUS" -ne 0 \
  || fail "invalid canary state initialization unexpectedly succeeded"
test ! -e "$TMP_DIR/invalid-state-initialization-continued" \
  || fail "invalid canary state initialization continued after validation"
test -f "$NO_WRITE_DIR/must-survive" \
  || fail "invalid canary state initialization mutated caller content"
test ! -e "$NO_WRITE_DIR/ledger.json" \
  || fail "invalid canary state initialization wrote a ledger"
test ! -s "$TMP_DIR/invalid-state-initialization.out" \
  || fail "invalid canary state initialization emitted stdout"
rg -Fx 'STOP: canary state directory ownership validation failed' \
  "$TMP_DIR/invalid-state-initialization.err" >/dev/null \
  || fail "invalid canary state initialization lacks generic failure context"
if rg -F "$NO_WRITE_DIR" \
    "$TMP_DIR/invalid-state-initialization.err" >/dev/null; then
  fail "invalid canary state initialization exposed the rejected path"
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

CHANGE_TICKET_GATE_LINE="$(line_of_unique ': "${CHANGE_TICKET:?set the approved production change ticket}"')"
MUTATION_APPROVAL_LINE="$(line_of_unique ': "${PRODUCTION_CANARY_APPROVED:?set only after approval}"')"
EXIT_TRAP_LINE="$(line_of_unique "trap 'handle_canary_exit \"\$?\"' EXIT")"
USER_CONFIG_PUT_LINE="$(line_of_unique 'STATUS="$(api_request PUT /api/user-config/llm \')"
test "$CHANGE_TICKET_GATE_LINE" -lt "$USER_CONFIG_PUT_LINE" \
  && test "$MUTATION_APPROVAL_LINE" -lt "$USER_CONFIG_PUT_LINE" \
  && test "$EXIT_TRAP_LINE" -lt "$USER_CONFIG_PUT_LINE" \
  || fail "UserConfig PUT precedes the mutation gate or EXIT trap"
if rg -F 'Everything above is read-only.' "$CANARY_DOC" >/dev/null; then
  fail "runbook still claims the pre-gate UserConfig mutation is read-only"
fi

assert_context_order \
  "UserConfig baseline observation" \
  'set_failure_context "user_config_baseline" "" "not_observed" "user_config_baseline_failed" "0"' \
  '"$CANARY_STATE_DIR/user-config-before.json")"' \
  'set_failure_context "user_config_baseline" "" "http_$STATUS" "user_config_baseline_failed" "0"'
assert_context_order \
  "UserConfig reselection" \
  'set_failure_context "user_config_reselection" "" "not_dispatched" "user_config_reselection_failed" "0"' \
  '"$CANARY_STATE_DIR/user-config-select.json")"' \
  'set_failure_context "user_config_reselection" "" "http_$STATUS" "user_config_reselection_failed" "0"'
assert_context_order \
  "UserConfig selection observation" \
  'set_failure_context "user_config_observation" "" "not_observed" "user_config_observation_failed" "0"' \
  '"$CANARY_STATE_DIR/user-config-selected.json")"' \
  'set_failure_context "user_config_observation" "" "http_$STATUS" "user_config_observation_failed" "0"'

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

DRAIN_BEARER_READ_FILE="$TMP_DIR/drain-bearer-read.sh"
awk '
  /^IFS= read -r DRAIN_BEARER / {
    print
    if (getline <= 0) exit 2
    print
    found = 1
    exit
  }
  END {if (!found) exit 1}
' "$CANARY_DOC" > "$DRAIN_BEARER_READ_FILE"
test -s "$DRAIN_BEARER_READ_FILE" || fail "missing Section 0 bearer read"

printf '%s' 'fixture-bearer-no-newline' > "$TMP_DIR/bearer-no-newline"
set +e
bash -c '
  set -euo pipefail
  STUDIO_TOKEN_FILE="$1"
  source "$2"
  test "$DRAIN_BEARER" = "fixture-bearer-no-newline"
  : > "$3"
' _ \
  "$TMP_DIR/bearer-no-newline" \
  "$DRAIN_BEARER_READ_FILE" \
  "$TMP_DIR/bearer-no-newline-confirmed"
DRAIN_BEARER_STATUS=$?
set -e
if test "$DRAIN_BEARER_STATUS" -ne 0; then
  test ! -e "$TMP_DIR/bearer-no-newline-confirmed" \
    || fail "failed Section 0 read unexpectedly reached confirmation"
  fail "Section 0 rejected a non-newline Studio bearer"
fi
test -e "$TMP_DIR/bearer-no-newline-confirmed" \
  || fail "Section 0 did not confirm the captured non-newline bearer"

: > "$TMP_DIR/bearer-empty"
set +e
bash -c '
  set -euo pipefail
  STUDIO_TOKEN_FILE="$1"
  source "$2"
  : > "$3"
' _ \
  "$TMP_DIR/bearer-empty" \
  "$DRAIN_BEARER_READ_FILE" \
  "$TMP_DIR/bearer-empty-confirmed"
EMPTY_BEARER_STATUS=$?
set -e
test "$EMPTY_BEARER_STATUS" -ne 0 \
  || fail "Section 0 accepted an empty Studio bearer"
test ! -e "$TMP_DIR/bearer-empty-confirmed" \
  || fail "empty Section 0 bearer unexpectedly reached confirmation"

printf '%s\n' 'scheduled member automation audit query tests: PASS'
