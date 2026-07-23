#!/usr/bin/env bash
set -euo pipefail

fail() {
  printf 'member automation audit query failed: %s\n' "$1" >&2
  exit 1
}

test "$#" -eq 1 || fail "expected create or revocation mode"
MODE="$1"
case "$MODE" in
  create)
    EVENT_ID="6201"
    ;;
  revocation)
    EVENT_ID="6202"
    ;;
  *)
    fail "expected create or revocation mode"
    ;;
esac

: "${AEVATAR_AUDIT_SCOPE_ID:?AEVATAR_AUDIT_SCOPE_ID is required}"
: "${AEVATAR_AUDIT_TEAM_ID:?AEVATAR_AUDIT_TEAM_ID is required}"
: "${AEVATAR_AUDIT_MEMBER_ID:?AEVATAR_AUDIT_MEMBER_ID is required}"
: "${AEVATAR_AUDIT_SCHEDULE_ID:?AEVATAR_AUDIT_SCHEDULE_ID is required}"
: "${AEVATAR_AUDIT_OPERATION_ID:?AEVATAR_AUDIT_OPERATION_ID is required}"

validate_filter() {
  local value="$1"
  test -n "$value" || fail "empty identity filter"
  case "$value" in
    *$'\n'*|*$'\r'*) fail "invalid identity filter" ;;
  esac
}

validate_filter "$AEVATAR_AUDIT_SCOPE_ID"
validate_filter "$AEVATAR_AUDIT_TEAM_ID"
validate_filter "$AEVATAR_AUDIT_MEMBER_ID"
validate_filter "$AEVATAR_AUDIT_SCHEDULE_ID"
validate_filter "$AEVATAR_AUDIT_OPERATION_ID"

read_logs() {
  if test -n "${AEVATAR_AUDIT_LOG_INPUT:-}"; then
    test -f "$AEVATAR_AUDIT_LOG_INPUT" || fail "fixture input is not a regular file"
    cat -- "$AEVATAR_AUDIT_LOG_INPUT"
    return
  fi

  local namespace="${AEVATAR_AUDIT_NAMESPACE:-aismart-app-mainnet}"
  local selector="${AEVATAR_AUDIT_LABEL_SELECTOR:-app=aevatar-console-backend}"
  local since="${AEVATAR_AUDIT_SINCE:-30m}"
  case "$since" in
    [1-9]*s|[1-9]*m|[1-9]*h) ;;
    *) fail "invalid bounded since value" ;;
  esac
  [[ "$since" =~ ^[1-9][0-9]*[smh]$ ]] || fail "invalid bounded since value"
  command -v kubectl >/dev/null 2>&1 || fail "kubectl is unavailable"
  kubectl -n "$namespace" logs -l "$selector" \
    --tail=-1 --timestamps --all-containers=true --since="$since"
}

read_logs | jq -Rsc \
  --arg mode "$MODE" \
  --arg eventId "$EVENT_ID" \
  --arg scopeId "$AEVATAR_AUDIT_SCOPE_ID" \
  --arg teamId "$AEVATAR_AUDIT_TEAM_ID" \
  --arg memberId "$AEVATAR_AUDIT_MEMBER_ID" \
  --arg scheduleId "$AEVATAR_AUDIT_SCHEDULE_ID" \
  --arg operationId "$AEVATAR_AUDIT_OPERATION_ID" '
  def captured($value; $pattern):
    ((try ($value | capture($pattern)) catch null) // null);

  def create_record($value):
    $value | {
      scopeId,
      teamId,
      memberId,
      scheduleId,
      operationId,
      bindingId
    };

  def revocation_record($value):
    $value | {
      scopeId,
      teamId,
      memberId,
      scheduleId,
      operationId,
      nyxIdRevocationStatus: "Completed",
      vaultRevocationStatus: "Completed",
      stateVersion: (.stateVersion | tonumber),
      observedAtUtc
    };

  split("\n") | map(sub("\r$"; "")) as $lines
  | ("^[^[:space:]]+[[:space:]]+info:[[:space:]]+" +
     "Aevatar\\.Studio\\.MemberAutomation\\[" + $eventId +
     "\\][[:space:]]*$") as $headerPattern
  | "^[^[:space:]]+[[:space:]]+(?<message>.*)$" as $linePattern
  | ("^Accepted Studio member automation create for scope " +
    "(?<scopeId>[^,[:space:]]+), team (?<teamId>[^,[:space:]]+), " +
    "member (?<memberId>[^,[:space:]]+), schedule (?<scheduleId>[^,[:space:]]+), " +
    "operation (?<operationId>[^,[:space:]]+), and verified binding " +
    "(?<bindingId>[^.[:space:]]+)\\.[[:space:]]*$") as $createPattern
  | ("^Completed Studio member automation revocation for scope " +
    "(?<scopeId>[^,[:space:]]+), team (?<teamId>[^,[:space:]]+), " +
    "member (?<memberId>[^,[:space:]]+), schedule (?<scheduleId>[^,[:space:]]+), " +
    "operation (?<operationId>[^,[:space:]]+), NyxID status Completed, " +
    "Vault status Completed, state version (?<stateVersion>[1-9][0-9]*), " +
    "observed at (?<observedAtUtc>[0-9]{4}-(0[1-9]|1[0-2])-" +
    "(0[1-9]|[12][0-9]|3[01])T([01][0-9]|2[0-3]):[0-5][0-9]:" +
    "[0-5][0-9]\\.[0-9]{7}(Z|[+-]((0[0-9]|1[0-3]):" +
    "[0-5][0-9]|14:00)))\\.[[:space:]]*$") as $revocationPattern
  | reduce range(0; ($lines | length)) as $index (
      {matches: [], malformed: false};
      captured($lines[$index]; $headerPattern) as $header
      | if $header == null then
          .
        elif ($index + 1) >= ($lines | length) then
          .malformed = true
        else
          captured($lines[$index + 1]; $linePattern) as $line
          | if $line == null then
              .malformed = true
            else
              captured(
                $line.message;
                if $mode == "create" then $createPattern else $revocationPattern end
              ) as $parsed
              | if $parsed == null then
                  .malformed = true
                elif $parsed.scopeId == $scopeId
                  and $parsed.teamId == $teamId
                  and $parsed.memberId == $memberId
                  and $parsed.scheduleId == $scheduleId
                  and $parsed.operationId == $operationId then
                  .matches += [
                    if $mode == "create"
                    then create_record($parsed)
                    else revocation_record($parsed)
                    end
                  ]
                else
                  .
                end
            end
        end
    )
  | if .malformed then
      error("malformed member automation audit event")
    elif (.matches | length) != 1 then
      error("member automation audit event cardinality mismatch")
    else
      .matches
    end
'
