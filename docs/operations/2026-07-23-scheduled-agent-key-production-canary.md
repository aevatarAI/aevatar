---
title: "Scheduled Agent Key Production Canary"
status: active
owner: platform
---

# Scheduled Agent Key Production Canary

## Current Status

This runbook was prepared from read-only production checks on 2026-07-23 and
executed once on 2026-07-24. The production canary completed successfully:

| Proof | Observed result |
| --- | --- |
| Release and workload | Short tag `f1a18bac` uniquely resolved to source `f1a18bac0c86df2dd5e1f1fd20bbe32e41c97330` at verification time. The registry tag resolved to digest `sha256:cffd1aef30b1dff7ede81ebd780dced55a7697928703d9199b11e7d909d6cc75`, and the running Pod image ID equaled that digest. The Pod was Ready and `/health/ready` returned ready. |
| Pre-mutation inventory | One scoped member, zero automations, zero pending authorization operations, and zero active Agent Key automations were observed. No drain pause was required. |
| Live contract | The production OpenAPI exposed typed UserConfig selection, all five owner-LLM fields, and both revocation-track fields while omitting caller, key, secret-reference, raw-key, and ciphertext fields. |
| Exact selection and plan | The typed UserConfig GET observed the exact UserService selection. Preflight returned one non-wildcard service grant with both wildcard flags false. |
| Dedicated key use | The exact deterministic key was active and unused before `run-now`. The workflow completed with its unique marker, and the same key's `last_used_at` changed from empty to `2026-07-24T13:25:59.746+00:00`. |
| Audit and revocation | The allowlisted `6201` audit proved the verified binding. The allowlisted `6202` audit proved NyxID `Completed` and Vault `Completed` before the owner-correct detail `404` was accepted. |
| Cleanup | The exact key was absent or inactive, the automation list was `0/0`, the revision was retired, the member and draft returned `404`, the Team was archived, and the exact UserConfig selection remained in place. |

The external release system did not provide an immutable full-source-SHA to
image-digest attestation. This execution therefore used an explicit
operator-approved provenance exception backed by the unique short-tag
resolution, registry digest, matching Pod image ID, live contract, and rollout
timeline. That correlated evidence is not an immutable attestation and must not
be represented as one. This was a one-time, non-precedential exception and does
not alter Section 1. Future executions remain blocked until the required
immutable release manifest is available.

Do not run any section under **Mutation Boundary** while any deployment,
contract, authentication, UserConfig, or service gate is unresolved.

## Purpose And Proof

The canary proves one narrow production behavior end to end:

1. A scoped Team workflow member is bound from `workflows/simple_qa.yaml`.
2. Studio preflight derives an exact, non-wildcard NyxID permission plan for
   UserService `4061b904-62de-4cee-9125-5e3ec8365afd`.
3. Automation creation provisions a dedicated
   `scheduled_invocation_agent_key` while the recurring schedule remains
   disabled.
4. `run-now` completes `simple_qa` with a unique marker.
5. The non-projected create event proves the exact verified NyxID binding used
   for the accepted schedule/operation while the public schema omits it.
6. Active and post-run views prove the exact persisted owner LLM route kind,
   route, UserService ID, service slug, and model.
7. The exact deterministic NyxID key changes from an empty `last_used_at` to a
   populated value. This is the required proof that the Agent Key, rather than
   the interactive owner bearer, performed the scheduled LLM call.
8. Both projected revocation tracks become terminal, deletion reaches HTTP
   `404`, the exact NyxID key ID and name are absent or inactive, and
   only then are the member, draft, revision, and Team cleaned up.

An accepted mutation receipt is never completion evidence. Completion comes
from the projected automation/run state and the independent NyxID key state.

## Safety Rules

- Run the commands from a reviewed checkout containing the required fix.
- Use Bash with `set +x`; do not enable xtrace at any point.
- Keep the Studio bearer in an existing mode-`0600` file. Never pass it in a
  command argument, print it, write it to the ledger, or attach it to a change
  ticket.
- Send the bearer to `curl` through `--config <(printf ...)` only.
- Use the verified NyxID CLI version `0.7.1` for this dated runbook.
- Never run `nyxid api-key create --terminal`. Studio must be the sole key
  creator for this canary.
- `nyxid api-key list/show` do not return raw key material. Even so, persist
  only the exact key ID and expected name, active state, wildcard flags,
  allowed service IDs, expiry, and `last_used_at`.
- Never print refresh tokens, raw Agent Keys, Vault references or ciphertext,
  bearer headers, unfiltered API bodies, or unfiltered production logs.
- The exact post-deploy UserConfig PUT is required and changes the owner-wide
  selection. Obtain explicit approval before the mutation boundary to leave
  that exact selection in place after the canary.
- Do not delete the member, delete the draft, retire the revision, or archive
  the Team while credential revocation is pending.
- Every pending or failed-track cleanup attempt must replay the same canonical
  DELETE with the original normalized owner, reason, `operationId`, and
  `idempotencyKey`. Derive a fresh bearer before each replay; fresh delete
  identities are forbidden.
- Do not use `kubectl exec`, `apply`, `delete`, rollout restart, or any other
  workload mutation in this runbook.
- Use only the reviewed service ports from the immutable release manifest in
  overrides, tunnels, or recovery commands.

## Fixed Contract

```bash
set +x
set -euo pipefail
umask 077

export AEVATAR_BASE_URL="https://aevatar-console-backend-api.aevatar.ai"
export SCOPE_ID="5d0d7b72-acff-49af-bb1b-9f30bbb7c102"
export NYXID_USER_SERVICE_ID="4061b904-62de-4cee-9125-5e3ec8365afd"
export NYXID_SERVICE_SLUG="chrono-llm-public"
export NYXID_PROXY_ROUTE="/api/v1/proxy/s/chrono-llm-public"
export EXPECTED_MODEL="gpt-5.5"
export EXPECTED_POLICY_VERSION="nyxid-api-key/scheduled-invocation/v2"
NYXID_BIN="/Users/eanzhao/.local/bin/nyxid"

capture_exact_nyxid_key() {
  local expected_name="$1" expected_id="$2" output="$3"
  "$NYXID_BIN" api-key list --output json \
    | jq \
      --arg name "$expected_name" \
      --arg keyId "$expected_id" '
      [.keys[]
       | select(
           .name == $name
           or ($keyId != "" and .id == $keyId)
         )
       | {
           id,
           name,
           is_active,
           allow_all_services,
           allow_all_nodes,
           allowed_service_ids,
           expires_at,
           last_used_at
         }] as $matching
      | if ($matching | length) > 1
        then error("ambiguous exact Agent Key identity")
        else ($matching[0] // null)
        end
    ' > "$output"
}

REPO_ROOT="$(git rev-parse --show-toplevel)"
test -x "$NYXID_BIN"
test "$("$NYXID_BIN" --version)" = "nyxid 0.7.1"
test -f "$REPO_ROOT/workflows/simple_qa.yaml"
rg -q '^name: simple_qa$' "$REPO_ROOT/workflows/simple_qa.yaml"
```

## Deployment And Contract Gates

### 0. Drain The Old Binary Immediately Before Deploy

This gate runs against the old binary. Caller authority is non-projected, so
the canonical automation API can prove lifecycle state but cannot prove a
binding. An active Agent Key automation may remain enabled only when an
approved non-projected audit export correlates all six values
`scopeId/teamId/memberId/scheduleId/operationId/bindingId`. Otherwise pause it
through the canonical member automation API. Do not create an admin endpoint,
infer a binding from another identity, or reuse a workflow binding-run ID.

Set `PREDEPLOY_CALLER_AUDIT_JSON` to an approved sanitized JSON array, or leave
it unset to treat every active Agent Key automation as uncovered. The export
must contain only the six named fields.

```bash
: "${STUDIO_TOKEN_FILE:?set to the existing mode-0600 Studio bearer file}"
test -f "$STUDIO_TOKEN_FILE"
test "$(stat -f '%Lp' "$STUDIO_TOKEN_FILE" 2>/dev/null \
  || stat -c '%a' "$STUDIO_TOKEN_FILE")" = "600"

DRAIN_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-integrity-drain.XXXXXX")"
chmod 700 "$DRAIN_DIR"
DRAIN_PAUSED_JSONL="$DRAIN_DIR/paused.jsonl"
: > "$DRAIN_PAUSED_JSONL"

DRAIN_BEARER=""
IFS= read -r DRAIN_BEARER < "$STUDIO_TOKEN_FILE" || true
test -n "$DRAIN_BEARER"

drain_request() {
  local method="$1" path="$2" output="$3" body="${4:-}"
  local -a args=(
    --disable --silent --show-error
    --proto '=https' --connect-timeout 10 --max-time 60
    --request "$method" --output "$output" --write-out '%{http_code}'
  )
  case "$path" in /*) ;; *) return 2 ;; esac
  if test -n "$body"; then
    args+=(--header 'Content-Type: application/json' --data-binary "@$body")
  fi
  curl "${args[@]}" \
    --config <(printf 'header = "Authorization: Bearer %s"\n' "$DRAIN_BEARER") \
    "$AEVATAR_BASE_URL$path"
}

drain_expect_status() {
  local expected="$1" actual="$2" operation="$3"
  if test "$actual" != "$expected"; then
    printf 'STOP: %s returned HTTP %s; expected %s\n' \
      "$operation" "$actual" "$expected" >&2
    return 1
  fi
}

AUDIT_FILE="$DRAIN_DIR/approved-audit.json"
if test -n "${PREDEPLOY_CALLER_AUDIT_JSON:-}"; then
  test -f "$PREDEPLOY_CALLER_AUDIT_JSON"
  jq -e '
    type == "array"
    and all(.[];
      (keys | sort) == ([
        "bindingId", "memberId", "operationId",
        "scheduleId", "scopeId", "teamId"
      ] | sort)
      and all([
        .scopeId, .teamId, .memberId, .scheduleId, .operationId, .bindingId
      ][]; type == "string" and length > 0)
    )
  ' "$PREDEPLOY_CALLER_AUDIT_JSON" >/dev/null
  jq '[.[] | {
    scopeId, teamId, memberId, scheduleId, operationId, bindingId
  }]' "$PREDEPLOY_CALLER_AUDIT_JSON" > "$AUDIT_FILE"
else
  printf '[]\n' > "$AUDIT_FILE"
fi

MEMBERS_JSONL="$DRAIN_DIR/members.jsonl"
: > "$MEMBERS_JSONL"
PAGE_TOKEN=""
while :; do
  PAGE_PATH="/api/scopes/$SCOPE_ID/members?pageSize=200"
  if test -n "$PAGE_TOKEN"; then
    ENCODED_TOKEN="$(jq -rn --arg value "$PAGE_TOKEN" '$value | @uri')"
    PAGE_PATH="$PAGE_PATH&pageToken=$ENCODED_TOKEN"
  fi
  STATUS="$(drain_request GET "$PAGE_PATH" "$DRAIN_DIR/members-page.json")"
  drain_expect_status 200 "$STATUS" predeploy-list-members
  jq -e '.members | type == "array"' "$DRAIN_DIR/members-page.json" >/dev/null
  jq -c '.members[] | select(.teamId != null) | {
    teamId, memberId
  }' "$DRAIN_DIR/members-page.json" >> "$MEMBERS_JSONL"
  PAGE_TOKEN="$(jq -r '.nextPageToken // empty' "$DRAIN_DIR/members-page.json")"
  test -n "$PAGE_TOKEN" || break
done

AUTOMATIONS_JSONL="$DRAIN_DIR/automations.jsonl"
: > "$AUTOMATIONS_JSONL"
while IFS= read -r MEMBER_ROW; do
  TEAM_ID_DRAIN="$(jq -er '.teamId' <<<"$MEMBER_ROW")"
  MEMBER_ID_DRAIN="$(jq -er '.memberId' <<<"$MEMBER_ROW")"
  CURSOR=""
  while :; do
    PAGE_PATH="/api/scopes/$SCOPE_ID/teams/$TEAM_ID_DRAIN/members/$MEMBER_ID_DRAIN/automations?take=100"
    if test -n "$CURSOR"; then
      ENCODED_CURSOR="$(jq -rn --arg value "$CURSOR" '$value | @uri')"
      PAGE_PATH="$PAGE_PATH&cursor=$ENCODED_CURSOR"
    fi
    STATUS="$(drain_request GET "$PAGE_PATH" "$DRAIN_DIR/automations-page.json")"
    drain_expect_status 200 "$STATUS" predeploy-list-automations
    jq -e '.items | type == "array"' "$DRAIN_DIR/automations-page.json" >/dev/null
    jq -c '.items[] | {
      scopeId, teamId, memberId, scheduleId, operationId,
      authorizationStatus, credentialSourceKind, enabled, stateVersion
    }' "$DRAIN_DIR/automations-page.json" >> "$AUTOMATIONS_JSONL"
    CURSOR="$(jq -r '.nextCursor // empty' "$DRAIN_DIR/automations-page.json")"
    test -n "$CURSOR" || break
  done
done < "$MEMBERS_JSONL"

jq -s -e '
  all(
    .authorizationStatus != "provisioning_pending"
    and .authorizationStatus != "replacement_pending"
  )
' "$AUTOMATIONS_JSONL" >/dev/null

jq -c -s --slurpfile audit "$AUDIT_FILE" '
  .[]
  | select(
      .authorizationStatus == "active"
      and .credentialSourceKind == "scheduled_invocation_agent_key"
    )
  | . as $automation
  | select(
      ([$audit[0][]
        | select(
            .scopeId == $automation.scopeId
            and .teamId == $automation.teamId
            and .memberId == $automation.memberId
            and .scheduleId == $automation.scheduleId
            and .operationId == $automation.operationId
            and (.bindingId | type == "string" and length > 0)
          )]
       | length) != 1
    )
' "$AUTOMATIONS_JSONL" > "$DRAIN_DIR/uncovered.jsonl"
```

The executable join above compares `scopeId`, `teamId`, `memberId`,
`scheduleId`, and `operationId`, then requires exactly one matching audit event
with a non-empty `bindingId`. A duplicate or missing event leaves the automation
uncovered. Do not weaken the gate to a partial identity match.

Pause every uncovered row with a fresh operation/idempotency pair, then observe
the projected `enabled == false` state at a newer version. The canonical route
is the only permitted mutation surface:

```bash
while IFS= read -r ROW; do
  TEAM_ID_DRAIN="$(jq -er '.teamId' <<<"$ROW")"
  MEMBER_ID_DRAIN="$(jq -er '.memberId' <<<"$ROW")"
  SCHEDULE_ID_DRAIN="$(jq -er '.scheduleId' <<<"$ROW")"
  PRIOR_VERSION="$(jq -er '.stateVersion' <<<"$ROW")"
  ID_SUFFIX="$(printf '%s' "$SCHEDULE_ID_DRAIN" \
    | openssl dgst -sha256 -r | awk '{print substr($1, 1, 20)}')"
  jq -n \
    --arg operationId "op-integrity-drain-$ID_SUFFIX" \
    --arg idempotencyKey "idem-integrity-drain-$ID_SUFFIX" '
    {operationId: $operationId, idempotencyKey: $idempotencyKey}
  ' > "$DRAIN_DIR/pause.json"
  STATUS="$(drain_request POST \
    "/api/scopes/$SCOPE_ID/teams/$TEAM_ID_DRAIN/members/$MEMBER_ID_DRAIN/automations/$SCHEDULE_ID_DRAIN/pause" \
    "$DRAIN_DIR/pause-response.json" "$DRAIN_DIR/pause.json")"
  drain_expect_status 202 "$STATUS" predeploy-pause-automation

  PAUSED=false
  for _ in $(seq 1 60); do
    STATUS="$(drain_request GET \
      "/api/scopes/$SCOPE_ID/teams/$TEAM_ID_DRAIN/members/$MEMBER_ID_DRAIN/automations/$SCHEDULE_ID_DRAIN" \
      "$DRAIN_DIR/paused-detail.json")"
    drain_expect_status 200 "$STATUS" observe-predeploy-pause
    if jq -e --argjson prior "$PRIOR_VERSION" '
        .enabled == false and .stateVersion > $prior
      ' "$DRAIN_DIR/paused-detail.json" >/dev/null; then
      PAUSED=true
      break
    fi
    sleep 2
  done
  test "$PAUSED" = "true"
  jq -c '{scopeId, teamId, memberId, scheduleId, operationId,
    authorizationStatus, enabled, stateVersion}' \
    "$DRAIN_DIR/paused-detail.json" >> "$DRAIN_PAUSED_JSONL"
done < "$DRAIN_DIR/uncovered.jsonl"

: "${DRAIN_EVIDENCE_RECORDED:?set to yes after recording the allowlisted drain result}"
test "$DRAIN_EVIDENCE_RECORDED" = "yes"
unset DRAIN_BEARER
rm -rf -- "$DRAIN_DIR"
test ! -e "$DRAIN_DIR"
```

Copy the allowlisted paused rows to the approved release ticket before setting
`DRAIN_EVIDENCE_RECORDED=yes`. After the new atomic release is visible, run fresh preflight and
`reauthorize` for each paused schedule; do not resume it until the projected
status is `active` at a newer authoritative version. The canary remains blocked
while any drain disposition is unresolved.

### 1. Prove The Running Image And Source

Get the final pushed source commit and atomic component/image evidence from one
immutable release manifest. Do not use a pre-rebase implementation SHA or
infer a commit from a mutable tag.

```bash
: "${FINAL_PUSHED_RELEASE_SHA:?set the final pushed/release source SHA}"
: "${DEPLOYED_SOURCE_SHA:?set from the same immutable release manifest}"
: "${RELEASE_MANIFEST_JSON:?set to the verified local manifest JSON path}"

[[ "$FINAL_PUSHED_RELEASE_SHA" =~ ^[0-9a-f]{40}$ ]]
[[ "$DEPLOYED_SOURCE_SHA" =~ ^[0-9a-f]{40}$ ]]
test -f "$RELEASE_MANIFEST_JSON"

git fetch --quiet origin
git cat-file -e "$FINAL_PUSHED_RELEASE_SHA^{commit}"
git cat-file -e "$DEPLOYED_SOURCE_SHA^{commit}"
git merge-base --is-ancestor "$FINAL_PUSHED_RELEASE_SHA" "$DEPLOYED_SOURCE_SHA"

jq -e \
  --arg source "$DEPLOYED_SOURCE_SHA" '
  .sourceSha == $source
  and ([.components[].name] | sort) == ([
    "authorization-fact",
    "authorization-plan",
    "schedule-actor-state",
    "scheduled-current-state-projector",
    "studio-automation-api"
  ] | sort)
  and ([.components[].sourceSha] | unique) == [$source]
  and ([.components[].imageDigest]
       | all(test("^sha256:[0-9a-f]{64}$")))
' "$RELEASE_MANIFEST_JSON" >/dev/null

RELEASE_IMAGE_DIGEST="$(jq -er '
  [.components[].imageDigest] | unique
  | if length == 1 then .[0] else error("release is not one atomic image") end
' "$RELEASE_MANIFEST_JSON")"
```

If read-only Kubernetes access is available, require every selected backend Pod
to be running and ready on one immutable image digest, then compare that digest
with the release record.

```bash
export KUBECONFIG="$HOME/Code/aelf-shared-k8s-prod.yaml"

test "$(kubectl -n aismart-app-mainnet auth can-i get pods)" = "yes"
KUBE_SNAPSHOT="$(mktemp "${TMPDIR:-/tmp}/aevatar-canary-pods.XXXXXX")"
kubectl -n aismart-app-mainnet get pods -l app=aevatar-console-backend -o json \
  > "$KUBE_SNAPSHOT"

jq -e '
  (.items | length) as $podCount
  | $podCount > 0
    and ([.items[].status.phase] | all(. == "Running"))
    and ([.items[].status.containerStatuses[]?
          | select(.name == "aevatar-console-backend")]
         | length == $podCount)
    and ([.items[].status.containerStatuses[]?
          | select(.name == "aevatar-console-backend")
          | .ready]
         | all(. == true))
' "$KUBE_SNAPSHOT" >/dev/null

POD_IMAGE_ID="$(jq -er '
  [.items[].status.containerStatuses[]?
   | select(.name == "aevatar-console-backend")
   | .imageID]
  | unique
  | if length == 1 then .[0] else error("backend Pods use multiple image IDs") end
' "$KUBE_SNAPSHOT")"
POD_IMAGE_DIGEST="${POD_IMAGE_ID##*@}"
test "$POD_IMAGE_DIGEST" = "$RELEASE_IMAGE_DIGEST"
rm -f "$KUBE_SNAPSHOT"
unset KUBE_SNAPSHOT POD_IMAGE_ID POD_IMAGE_DIGEST
```

If Kubernetes still returns `403`, stop. A healthy HTTP endpoint does not prove
which source commit is running.

### 2. Prove Health And The Live OpenAPI Contract

```bash
PUBLIC_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-canary-public.XXXXXX")"

HTTP_STATUS="$(curl --disable --silent --show-error \
  --proto '=https' --connect-timeout 10 --max-time 60 \
  --output "$PUBLIC_DIR/ready.json" --write-out '%{http_code}' \
  "$AEVATAR_BASE_URL/health/ready")"
test "$HTTP_STATUS" = "200"
jq -e '
  .ok == true
  and .status == "ready"
  and ([.components[].name] | index("workflow-bundle") != null)
  and ([.components[].name] | index("gagent-service") != null)
  and ([.components[].name] | index("studio") != null)
' "$PUBLIC_DIR/ready.json" >/dev/null

HTTP_STATUS="$(curl --disable --silent --show-error \
  --proto '=https' --connect-timeout 10 --max-time 60 \
  --output "$PUBLIC_DIR/openapi.json" --write-out '%{http_code}' \
  "$AEVATAR_BASE_URL/api/openapi.json")"
test "$HTTP_STATUS" = "200"
jq -e '
  (.paths | has("/api/user-config/llm"))
  and ([
    .components.schemas
    | to_entries[]
    | select(.key | endswith("UserLlmSettingsResponse"))
    | .value.properties
    | (has("savedRouteKind")
       and has("savedUserServiceId")
       and has("savedServiceSlug"))
  ] == [true])
  and ([
    .components.schemas
    | to_entries[]
    | select(.key | endswith("StudioMemberAutomationView"))
    | .value.properties
    | (has("ownerLLMRouteKind")
       and has("ownerLLMRoute")
       and has("ownerLLMUserServiceId")
       and has("ownerLLMServiceSlug")
       and has("ownerLLMModel")
       and has("nyxIdRevocationStatus")
       and has("vaultRevocationStatus")
       and (has("callerAuthority") | not)
       and (has("verifiedBindingId") | not)
       and (has("secretReference") | not)
       and (has("apiKeyId") | not)
       and (has("fullKey") | not)
       and (has("ciphertext") | not))
  ] == [true])
' "$PUBLIC_DIR/openapi.json" >/dev/null

rm -rf "$PUBLIC_DIR"
unset PUBLIC_DIR HTTP_STATUS
```

The implemented Studio view exposes both fields. Their exact case-sensitive
wire values are `NotRequired`, `Pending`, `Completed`, and `Failed`, derived
from the projector's Protobuf enum names. The live OpenAPI assertion above must
still pass before mutation. For this canary, only `Completed` on both tracks is
terminal; `Failed`, an empty value, or a missing field fails closed. The public
GET hides a deleted row once revocation is no longer pending, so it cannot be
used to claim that a terminal `Completed/Completed` row was observed. The
repository audit query below proves those final values before its owner-correct
`404` is accepted.

## Authentication, Owner, And UserConfig Gates

### 3. Initialize A Secret-Safe Local Session

Set `STUDIO_TOKEN_FILE` to the existing one-line bearer file. The file is not a
canary artifact and must not be copied into the repository.

```bash
: "${STUDIO_TOKEN_FILE:?set to the existing Studio bearer file}"
test -f "$STUDIO_TOKEN_FILE"
TOKEN_MODE="$(stat -f '%Lp' "$STUDIO_TOKEN_FILE" 2>/dev/null \
  || stat -c '%a' "$STUDIO_TOKEN_FILE")"
test "$TOKEN_MODE" = "600"
test "$(awk 'END {print NR}' "$STUDIO_TOKEN_FILE")" = "1"

CANARY_STATE_CONTRACT_VERSION="aevatar-agent-key-canary-state-v1"
CANARY_STATE_SENTINEL_NAME=".aevatar-agent-key-canary-state.json"

canary_path_owner_id() {
  stat -f '%u' "$1" 2>/dev/null || stat -c '%u' "$1"
}

canary_path_mode() {
  stat -f '%Lp' "$1" 2>/dev/null || stat -c '%a' "$1"
}

canonicalize_canary_state_dir() {
  local requested="${1:?canary state directory required}"
  test -d "$requested" || return 1
  test ! -L "$requested" || return 1
  (cd -P -- "$requested" >/dev/null 2>&1 && pwd -P)
}

validate_canary_state_dir() {
  local requested="${1:?canary state directory required}"
  local canonical sentinel current_uid
  canonical="$(canonicalize_canary_state_dir "$requested")" || return 1
  test "$requested" = "$canonical" || return 1
  current_uid="$(id -u)"
  test "$(canary_path_owner_id "$canonical")" = "$current_uid" || return 1
  test "$(canary_path_mode "$canonical")" = "700" || return 1

  sentinel="$canonical/$CANARY_STATE_SENTINEL_NAME"
  test -f "$sentinel" || return 1
  test ! -L "$sentinel" || return 1
  test "$(canary_path_owner_id "$sentinel")" = "$current_uid" || return 1
  test "$(canary_path_mode "$sentinel")" = "600" || return 1
  jq -e \
    --arg contractVersion "$CANARY_STATE_CONTRACT_VERSION" \
    --arg canonicalPath "$canonical" '
    type == "object"
    and (keys == ["canonicalPath", "contractVersion"])
    and .contractVersion == $contractVersion
    and .canonicalPath == $canonicalPath
  ' "$sentinel" >/dev/null 2>&1 || return 1
  printf '%s\n' "$canonical"
}

create_or_resume_canary_state_dir() {
  local requested="${1:-}"
  local created canonical sentinel current_uid
  if test -n "$requested"; then
    validate_canary_state_dir "$requested"
    return
  fi

  created="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-agent-key-canary.XXXXXX")" || return 1
  canonical="$(canonicalize_canary_state_dir "$created")" || return 1
  current_uid="$(id -u)"
  test "$(canary_path_owner_id "$canonical")" = "$current_uid" || return 1
  chmod 700 "$canonical" || return 1
  sentinel="$canonical/$CANARY_STATE_SENTINEL_NAME"
  (umask 077; jq -n \
    --arg contractVersion "$CANARY_STATE_CONTRACT_VERSION" \
    --arg canonicalPath "$canonical" \
    '{contractVersion: $contractVersion, canonicalPath: $canonicalPath}' \
    > "$sentinel") || return 1
  chmod 600 "$sentinel" || return 1
  validate_canary_state_dir "$canonical"
}

remove_canary_state_dir() {
  local requested="${1:?canary state directory required}"
  local canonical
  canonical="$(validate_canary_state_dir "$requested")" || return 1
  test "$canonical" = "$requested" || return 1
  rm -rf -- "$canonical" || return 1
  test ! -e "$canonical"
}

if ! VALIDATED_CANARY_STATE_DIR="$(
    create_or_resume_canary_state_dir "${CANARY_STATE_DIR:-}"
  )"; then
  printf 'STOP: canary state directory ownership validation failed\n' >&2
  exit 1
fi
CANARY_STATE_DIR="$VALIDATED_CANARY_STATE_DIR"
unset VALIDATED_CANARY_STATE_DIR
LEDGER="$CANARY_STATE_DIR/ledger.json"
if ! test -f "$LEDGER"; then
  printf '{}\n' > "$LEDGER"
fi
chmod 600 "$LEDGER"

read_bearer() {
  STUDIO_BEARER=""
  IFS= read -r STUDIO_BEARER < "$STUDIO_TOKEN_FILE" || true
  test -n "$STUDIO_BEARER"
  case "$STUDIO_BEARER" in
    *$'\r'*|*,*) return 1 ;;
  esac
}

api_request() {
  local method="$1"
  local path="$2"
  local output="$3"
  local body="${4:-}"
  local -a args=(
    --disable --silent --show-error
    --proto '=https' --connect-timeout 10 --max-time 60
    --request "$method"
    --output "$output"
    --write-out '%{http_code}'
  )
  case "$path" in /*) ;; *) return 2 ;; esac
  if test -n "$body"; then
    args+=(--header 'Content-Type: application/json' --data-binary "@$body")
  fi
  curl "${args[@]}" \
    --config <(printf 'header = "Authorization: Bearer %s"\n' "$STUDIO_BEARER") \
    "$AEVATAR_BASE_URL$path"
}

expect_status() {
  local expected="$1"
  local actual="$2"
  local operation="$3"
  if test "$actual" != "$expected"; then
    printf 'STOP: %s returned HTTP %s; expected %s\n' \
      "$operation" "$actual" "$expected" >&2
    return 1
  fi
}

ledger_set() {
  local key="$1"
  local value="$2"
  local next="$CANARY_STATE_DIR/ledger.next.json"
  jq --arg key "$key" --arg value "$value" '. + {($key): $value}' \
    "$LEDGER" > "$next"
  mv "$next" "$LEDGER"
}

read_bearer
```

The state directory may contain authenticated response bodies and owner PII.
An unset `CANARY_STATE_DIR` creates it and its versioned path-binding sentinel;
a set value is resume-only and must pass the same canonical-path, ownership,
mode, and sentinel validation before any write. Keep the directory mode `0700`,
do not attach it wholesale to a ticket, and remove it after extracting the
non-secret evidence checklist at the end.

### 4. Require One Authenticated Owner In The Exact Scope

```bash
STATUS="$(api_request GET /api/auth/me "$CANARY_STATE_DIR/auth-me.json")"
expect_status 200 "$STATUS" auth-me
jq -e --arg scope "$SCOPE_ID" '
  .authenticated == true
  and .session.authenticated == true
  and .scopeId == $scope
  and .session.scopeId == $scope
  and (.profile.subject | type == "string" and length > 0)
' "$CANARY_STATE_DIR/auth-me.json" >/dev/null

"$NYXID_BIN" whoami --output json \
  > "$CANARY_STATE_DIR/nyxid-whoami.json"

STUDIO_SUBJECT="$(jq -er '.profile.subject' "$CANARY_STATE_DIR/auth-me.json")"
NYXID_SUBJECT="$(jq -er '.id' "$CANARY_STATE_DIR/nyxid-whoami.json")"
test "$STUDIO_SUBJECT" = "$NYXID_SUBJECT"
unset STUDIO_SUBJECT NYXID_SUBJECT
```

Do not print either subject. If either authentication check fails, refresh the
credentials through the normal login flow and restart from this gate.

### 5. Require The Exact Connected NyxID UserService

```bash
"$NYXID_BIN" service list --output json \
  > "$CANARY_STATE_DIR/nyxid-services.json"

jq -e \
  --arg id "$NYXID_USER_SERVICE_ID" \
  --arg slug "$NYXID_SERVICE_SLUG" \
  --arg route "$NYXID_PROXY_ROUTE" '
  [.keys[]
   | select(
       .id == $id
       and .slug == $slug
       and .is_active == true
       and .connected == true
       and (.proxy_url_slug | type == "string")
       and (.proxy_url_slug == $route or (.proxy_url_slug | endswith($route + "/{path}"))))]
  | length == 1
' "$CANARY_STATE_DIR/nyxid-services.json" >/dev/null
```

`serviceSlug` and the proxy route are integrity/display data. The UUID above is
the only service identity admitted into the authorization plan.

## Mutation Boundary

The preflight checks above are read-only. The first production mutation below
is the approved owner-wide UserConfig reselection. Later steps create, bind,
invoke, revoke, retire, delete, or archive temporary production resources.

Before continuing, record a change ticket, the release provenance evidence,
the canary owner, and explicit approval for temporary production mutations.
The repository-owned sanitized operational audit query is also a hard gate. It
filters at the source and writes only its documented allowlist to stdout. Never
replace it with a raw backend-log dump.

Both modes receive their exact filters through `AEVATAR_AUDIT_*` environment
variables. The create query writes a JSON array containing only
`scopeId`, `teamId`, `memberId`, `scheduleId`, `operationId`, and `bindingId`.
The revocation query writes a JSON array containing only `scopeId`, `teamId`,
`memberId`, `scheduleId`, `operationId`, `nyxIdRevocationStatus`,
`vaultRevocationStatus`, `stateVersion`, and `observedAtUtc`. Missing,
malformed, duplicate, or conflicting exact records fail closed.

```bash
: "${CHANGE_TICKET:?set the approved production change ticket}"
: "${PRODUCTION_CANARY_APPROVED:?set only after approval}"
test "$PRODUCTION_CANARY_APPROVED" = "yes"
AUDIT_QUERY_TOOL="$REPO_ROOT/tools/schedules/query_member_automation_audit.sh"
test -x "$AUDIT_QUERY_TOOL"

CANARY_SUFFIX="$(date -u +%Y%m%d%H%M%S)-$(openssl rand -hex 4)"
TEAM_ID="canary-team-$CANARY_SUFFIX"
MEMBER_ID="m-canary-$CANARY_SUFFIX"
DRAFT_FILE_NAME="canary-simple-qa-$CANARY_SUFFIX.yaml"
CANARY_MARKER="AEVATAR_AGENT_KEY_CANARY_${CANARY_SUFFIX//-/_}"
CANARY_PROMPT="Reply with exactly $CANARY_MARKER and no other text."
CREATE_OPERATION_ID="op-create-$CANARY_SUFFIX"
CREATE_IDEMPOTENCY_KEY="idem-create-$CANARY_SUFFIX"
RUN_OPERATION_ID="op-run-$CANARY_SUFFIX"
RUN_IDEMPOTENCY_KEY="idem-run-$CANARY_SUFFIX"
DELETE_OPERATION_ID="op-delete-$CANARY_SUFFIX"
DELETE_IDEMPOTENCY_KEY="idem-delete-$CANARY_SUFFIX"

ledger_set canarySuffix "$CANARY_SUFFIX"
ledger_set scopeId "$SCOPE_ID"
ledger_set teamId "$TEAM_ID"
ledger_set memberId "$MEMBER_ID"
ledger_set draftFileName "$DRAFT_FILE_NAME"
ledger_set marker "$CANARY_MARKER"
ledger_set createOperationId "$CREATE_OPERATION_ID"
ledger_set createIdempotencyKey "$CREATE_IDEMPOTENCY_KEY"
ledger_set runOperationId "$RUN_OPERATION_ID"
ledger_set runIdempotencyKey "$RUN_IDEMPOTENCY_KEY"
ledger_set deleteOperationId "$DELETE_OPERATION_ID"
ledger_set deleteIdempotencyKey "$DELETE_IDEMPOTENCY_KEY"

set_failure_context() {
  FAILURE_PHASE="${1:?failure phase required}"
  FAILURE_OPERATION_ID="${2:-}"
  FAILURE_STATUS="${3:-not_observed}"
  FAILURE_CODE="${4:-canary_failed}"
  FAILURE_STATE_VERSION="${5:-0}"
  FAILURE_SCOPE_ID="${SCOPE_ID:-}"
  FAILURE_TEAM_ID="${TEAM_ID:-}"
  FAILURE_MEMBER_ID="${MEMBER_ID:-}"
  FAILURE_SCHEDULE_ID="${SCHEDULE_ID:-}"
  FAILURE_RUN_ID="${RUN_ID:-}"
}

set_failure_context "scaffold" ""
record_failure() {
  local observed_at
  observed_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  jq -n \
    --arg scopeId "$FAILURE_SCOPE_ID" \
    --arg teamId "$FAILURE_TEAM_ID" \
    --arg memberId "$FAILURE_MEMBER_ID" \
    --arg scheduleId "$FAILURE_SCHEDULE_ID" \
    --arg runId "$FAILURE_RUN_ID" \
    --arg operationId "$FAILURE_OPERATION_ID" \
    --arg status "$FAILURE_STATUS" \
    --arg code "$FAILURE_CODE" \
    --arg stateVersion "$FAILURE_STATE_VERSION" \
    --arg observedAtUtc "$observed_at" '{
      scopeId: $scopeId,
      teamId: $teamId,
      memberId: $memberId,
      scheduleId: $scheduleId,
      runId: $runId,
      operationId: $operationId,
      status: $status,
      failureCode: $code,
      stateVersion: $stateVersion,
      observedAtUtc: $observedAtUtc
    }' > "$CANARY_STATE_DIR/failure-allowlist.json"
}
handle_canary_exit() {
  local exit_status="$1"
  trap - EXIT
  if test "$exit_status" -ne 0; then
    (record_failure) >/dev/null 2>&1 || true
  fi
  exit "$exit_status"
}
trap 'handle_canary_exit "$?"' EXIT
```

Inspect only the ledger keys, never the bearer:

```bash
jq -e 'has("teamId") and has("memberId") and has("createOperationId")' \
  "$LEDGER" >/dev/null
```

Before each bounded operation, call `set_failure_context` and update it from
the latest typed response. The `EXIT` handler records a nonzero exit exactly
once while preserving its original status. It records only the
allowlisted IDs, lifecycle/completion status, stable code, authoritative
version, and UTC timestamp. Do not add exception text, request/response bodies,
headers, caller subjects, or log messages to failure evidence.

### 6. Reselect And Observe The Exact Typed UserConfig

This protected request is also the second authentication gate and the first
production mutation. Capture the original typed selection, then perform the
approved post-deploy reselection. `202 Accepted` is only dispatch evidence; the
canary remains blocked until the typed GET observes the committed exact
selection.

```bash
: "${USER_CONFIG_RESELECTION_APPROVED:?set only after owner-wide change approval}"
: "${USER_CONFIG_DISPOSITION:?set to leave_selected}"
: "${USER_CONFIG_LEAVE_SELECTED_APPROVED:?set only after leave-selected approval}"
test "$USER_CONFIG_RESELECTION_APPROVED" = "yes"
test "$USER_CONFIG_DISPOSITION" = "leave_selected"
test "$USER_CONFIG_LEAVE_SELECTED_APPROVED" = "yes"

set_failure_context "user_config_baseline" "" "not_observed" "user_config_baseline_failed" "0"
STATUS="$(api_request GET /api/user-config/llm \
  "$CANARY_STATE_DIR/user-config-before.json")"
set_failure_context "user_config_baseline" "" "http_$STATUS" "user_config_baseline_failed" "0"
expect_status 200 "$STATUS" user-config-llm

jq -e '
  (.savedRouteKind == "unspecified"
   or .savedRouteKind == "gateway"
   or .savedRouteKind == "nyx_id_user_service")
  and (.savedRoute | type == "string")
  and (.defaultModel | type == "string")
' "$CANARY_STATE_DIR/user-config-before.json" >/dev/null
jq '{
  savedRouteKind,
  savedRoute,
  savedUserServiceId,
  savedServiceSlug,
  defaultModel
}' "$CANARY_STATE_DIR/user-config-before.json" \
  > "$CANARY_STATE_DIR/user-config-original-allowlist.json"

jq -n \
  --arg userServiceId "$NYXID_USER_SERVICE_ID" \
  --arg model "$EXPECTED_MODEL" '
  {userServiceId: $userServiceId, model: $model}
' > "$CANARY_STATE_DIR/user-config-select.json"

set_failure_context "user_config_reselection" "" "not_dispatched" "user_config_reselection_failed" "0"
STATUS="$(api_request PUT /api/user-config/llm \
  "$CANARY_STATE_DIR/user-config-select-response.json" \
  "$CANARY_STATE_DIR/user-config-select.json")"
set_failure_context "user_config_reselection" "" "http_$STATUS" "user_config_reselection_failed" "0"
expect_status 202 "$STATUS" select-exact-owner-llm
jq -e '.accepted == true and .ackStage == "accepted"' \
  "$CANARY_STATE_DIR/user-config-select-response.json" >/dev/null

USER_CONFIG_OBSERVED=false
set_failure_context "user_config_observation" "" "not_observed" "user_config_observation_failed" "0"
for _ in $(seq 1 60); do
  STATUS="$(api_request GET /api/user-config/llm \
    "$CANARY_STATE_DIR/user-config-selected.json")"
  set_failure_context "user_config_observation" "" "http_$STATUS" "user_config_observation_failed" "0"
  expect_status 200 "$STATUS" observe-exact-owner-llm
  if jq -e \
      --arg id "$NYXID_USER_SERVICE_ID" \
      --arg slug "$NYXID_SERVICE_SLUG" \
      --arg route "$NYXID_PROXY_ROUTE" \
      --arg model "$EXPECTED_MODEL" '
      .savedRouteKind == "nyx_id_user_service"
      and .savedUserServiceId == $id
      and .savedServiceSlug == $slug
      and .savedRoute == $route
      and .defaultModel == $model
    ' "$CANARY_STATE_DIR/user-config-selected.json" >/dev/null; then
    USER_CONFIG_OBSERVED=true
    set_failure_context "user_config_observation" "" "observed" "user_config_observation_failed" "0"
    break
  fi
  sleep 2
done
test "$USER_CONFIG_OBSERVED" = "true"
ledger_set userConfigDisposition "$USER_CONFIG_DISPOSITION"
```

The exact selected owner-wide UserConfig remains in place after the canary.
Never use only `savedRoute` to infer `savedUserServiceId`.

### 7. Capture A Fresh Collision Baseline

```bash
STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/teams" \
  "$CANARY_STATE_DIR/teams-before.json")"
expect_status 200 "$STATUS" list-teams
jq -e --arg id "$TEAM_ID" '[.teams[] | select(.teamId == $id)] | length == 0' \
  "$CANARY_STATE_DIR/teams-before.json" >/dev/null

STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/members" \
  "$CANARY_STATE_DIR/members-before.json")"
expect_status 200 "$STATUS" list-members
jq -e --arg id "$MEMBER_ID" '[.members[] | select(.memberId == $id)] | length == 0' \
  "$CANARY_STATE_DIR/members-before.json" >/dev/null

STATUS="$(api_request GET "/api/workspace/workflow-drafts?scopeId=$SCOPE_ID" \
  "$CANARY_STATE_DIR/drafts-before.json")"
expect_status 200 "$STATUS" list-workflow-drafts
jq -e --arg file "$DRAFT_FILE_NAME" \
  '[.[] | select(.fileName == $file)] | length == 0' \
  "$CANARY_STATE_DIR/drafts-before.json" >/dev/null
```

Record the fresh counts in the change ticket without listing unrelated owner
resources or identities.

### 8. Create And Observe The Team

```bash
jq -n \
  --arg displayName "Agent Key canary $CANARY_SUFFIX" \
  --arg description "Temporary scheduled Agent Key production canary" \
  --arg teamId "$TEAM_ID" '
  {displayName: $displayName, description: $description, teamId: $teamId}
' > "$CANARY_STATE_DIR/team-create.json"

STATUS="$(api_request POST "/api/scopes/$SCOPE_ID/teams" \
  "$CANARY_STATE_DIR/team-create-response.json" \
  "$CANARY_STATE_DIR/team-create.json")"
expect_status 201 "$STATUS" create-team
jq -e --arg scope "$SCOPE_ID" --arg team "$TEAM_ID" '
  .scopeId == $scope and .teamId == $team and .lifecycleStage == "active"
' "$CANARY_STATE_DIR/team-create-response.json" >/dev/null

TEAM_VISIBLE=false
for _ in $(seq 1 30); do
  STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/teams/$TEAM_ID" \
    "$CANARY_STATE_DIR/team-detail.json")"
  if test "$STATUS" = "200" && jq -e --arg team "$TEAM_ID" \
      '.teamId == $team and .lifecycleStage == "active"' \
      "$CANARY_STATE_DIR/team-detail.json" >/dev/null; then
    TEAM_VISIBLE=true
    break
  fi
  sleep 2
done
test "$TEAM_VISIBLE" = "true"
```

### 9. Create And Observe The Scoped Workflow Draft

Resolve the single scope-owned workflow directory. Do not invent a directory
identity or use a workflow name as its ID.

```bash
STATUS="$(api_request GET "/api/workspace?scopeId=$SCOPE_ID" \
  "$CANARY_STATE_DIR/workspace.json")"
expect_status 200 "$STATUS" get-workspace
jq -e '.directories | length == 1' "$CANARY_STATE_DIR/workspace.json" >/dev/null
DIRECTORY_ID="$(jq -er '.directories[0].directoryId' \
  "$CANARY_STATE_DIR/workspace.json")"
ledger_set directoryId "$DIRECTORY_ID"

jq -n \
  --arg directoryId "$DIRECTORY_ID" \
  --arg workflowName "simple_qa" \
  --arg fileName "$DRAFT_FILE_NAME" \
  --rawfile yaml "$REPO_ROOT/workflows/simple_qa.yaml" '
  {
    directoryId: $directoryId,
    workflowName: $workflowName,
    fileName: $fileName,
    yaml: $yaml
  }
' > "$CANARY_STATE_DIR/draft-create.json"

STATUS="$(api_request POST "/api/workspace/workflow-drafts?scopeId=$SCOPE_ID" \
  "$CANARY_STATE_DIR/draft-create-response.json" \
  "$CANARY_STATE_DIR/draft-create.json")"
expect_status 202 "$STATUS" create-workflow-draft
jq -e '.accepted == true and (.workflowId | type == "string" and length > 0)' \
  "$CANARY_STATE_DIR/draft-create-response.json" >/dev/null

DRAFT_WORKFLOW_ID="$(jq -er '.workflowId' \
  "$CANARY_STATE_DIR/draft-create-response.json")"
test "$DRAFT_WORKFLOW_ID" != "$MEMBER_ID"
ledger_set draftWorkflowId "$DRAFT_WORKFLOW_ID"

DRAFT_VISIBLE=false
for _ in $(seq 1 60); do
  STATUS="$(api_request GET \
    "/api/workspace/workflow-drafts/$DRAFT_WORKFLOW_ID?scopeId=$SCOPE_ID" \
    "$CANARY_STATE_DIR/draft-detail.json")"
  if test "$STATUS" = "200" && jq -e \
      --arg workflow "$DRAFT_WORKFLOW_ID" \
      --arg file "$DRAFT_FILE_NAME" '
      .workflowId == $workflow
      and .name == "simple_qa"
      and .fileName == $file
    ' "$CANARY_STATE_DIR/draft-detail.json" >/dev/null; then
    DRAFT_VISIBLE=true
    break
  fi
  test "$STATUS" = "404"
  sleep 2
done
test "$DRAFT_VISIBLE" = "true"
```

`DRAFT_WORKFLOW_ID` is a draft identity. It must never be passed as
`memberId` or `publishedServiceId`.

### 10. Create And Observe The Distinct Workflow Member

Member creation must omit `implementationRef`. Binding is the only supported
path for attaching the workflow draft.

```bash
jq -n \
  --arg displayName "Agent Key canary member $CANARY_SUFFIX" \
  --arg description "Temporary workflow member for Agent Key canary" \
  --arg memberId "$MEMBER_ID" \
  --arg teamId "$TEAM_ID" '
  {
    displayName: $displayName,
    implementationKind: "workflow",
    description: $description,
    memberId: $memberId,
    teamId: $teamId
  }
' > "$CANARY_STATE_DIR/member-create.json"

STATUS="$(api_request POST "/api/scopes/$SCOPE_ID/members" \
  "$CANARY_STATE_DIR/member-create-response.json" \
  "$CANARY_STATE_DIR/member-create.json")"
expect_status 201 "$STATUS" create-member
jq -e --arg scope "$SCOPE_ID" --arg team "$TEAM_ID" --arg member "$MEMBER_ID" '
  .scopeId == $scope
  and .teamId == $team
  and .memberId == $member
  and .implementationKind == "workflow"
  and (.publishedServiceId | type == "string" and length > 0)
' "$CANARY_STATE_DIR/member-create-response.json" >/dev/null

PUBLISHED_SERVICE_ID="$(jq -er '.publishedServiceId' \
  "$CANARY_STATE_DIR/member-create-response.json")"
test "$PUBLISHED_SERVICE_ID" != "$MEMBER_ID"
test "$PUBLISHED_SERVICE_ID" != "$DRAFT_WORKFLOW_ID"
ledger_set publishedServiceId "$PUBLISHED_SERVICE_ID"

MEMBER_VISIBLE=false
for _ in $(seq 1 60); do
  STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/members/$MEMBER_ID" \
    "$CANARY_STATE_DIR/member-detail.json")"
  if test "$STATUS" = "200" && jq -e \
      --arg member "$MEMBER_ID" \
      --arg service "$PUBLISHED_SERVICE_ID" \
      --arg team "$TEAM_ID" '
      .summary.memberId == $member
      and .summary.publishedServiceId == $service
      and .summary.teamId == $team
    ' "$CANARY_STATE_DIR/member-detail.json" >/dev/null; then
    MEMBER_VISIBLE=true
    break
  fi
  test "$STATUS" = "404"
  sleep 2
done
test "$MEMBER_VISIBLE" = "true"
```

### 11. Bind The Member And Require Invocation Readiness

The path uses `MEMBER_ID`; the body uses `DRAFT_WORKFLOW_ID`. Keep those
identities distinct.

```bash
jq -n \
  --arg workflowId "$DRAFT_WORKFLOW_ID" \
  --rawfile yaml "$REPO_ROOT/workflows/simple_qa.yaml" '
  {workflow: {workflowId: $workflowId, workflowYamls: [$yaml]}}
' > "$CANARY_STATE_DIR/bind.json"

STATUS="$(api_request PUT "/api/scopes/$SCOPE_ID/members/$MEMBER_ID/binding" \
  "$CANARY_STATE_DIR/bind-response.json" \
  "$CANARY_STATE_DIR/bind.json")"
expect_status 202 "$STATUS" bind-member
jq -e --arg member "$MEMBER_ID" '
  .status == "accepted"
  and .memberId == $member
  and (.bindingRunId | type == "string" and length > 0)
' "$CANARY_STATE_DIR/bind-response.json" >/dev/null

BINDING_RUN_ID="$(jq -er '.bindingRunId' "$CANARY_STATE_DIR/bind-response.json")"
ledger_set bindingRunId "$BINDING_RUN_ID"

BINDING_SUCCEEDED=false
for _ in $(seq 1 90); do
  STATUS="$(api_request GET \
    "/api/scopes/$SCOPE_ID/members/$MEMBER_ID/binding-runs/$BINDING_RUN_ID" \
    "$CANARY_STATE_DIR/binding-run.json")"
  if test "$STATUS" = "404"; then
    sleep 2
    continue
  fi
  expect_status 200 "$STATUS" get-binding-run
  BINDING_STATUS="$(jq -er '.status' "$CANARY_STATE_DIR/binding-run.json")"
  case "$BINDING_STATUS" in
    succeeded)
      BINDING_SUCCEEDED=true
      break
      ;;
    failed|rejected)
      printf 'STOP: binding reached terminal status %s\n' "$BINDING_STATUS" >&2
      exit 1
      ;;
  esac
  sleep 2
done
test "$BINDING_SUCCEEDED" = "true"

jq -e \
  --arg member "$MEMBER_ID" \
  --arg service "$PUBLISHED_SERVICE_ID" '
  .memberId == $member
  and .result.publishedServiceId == $service
  and (.result.revisionId | type == "string" and length > 0)
' "$CANARY_STATE_DIR/binding-run.json" >/dev/null

REVISION_ID="$(jq -er '.result.revisionId' "$CANARY_STATE_DIR/binding-run.json")"
ledger_set revisionId "$REVISION_ID"

ENDPOINT_READY=false
for _ in $(seq 1 60); do
  STATUS="$(api_request GET \
    "/api/scopes/$SCOPE_ID/members/$MEMBER_ID/endpoints/chat/contract" \
    "$CANARY_STATE_DIR/chat-contract.json")"
  if test "$STATUS" = "200" && jq -e \
      --arg member "$MEMBER_ID" \
      --arg service "$PUBLISHED_SERVICE_ID" \
      --arg revision "$REVISION_ID" '
      .memberId == $member
      and .publishedServiceId == $service
      and .revisionId == $revision
      and .endpointId == "chat"
      and .invocationReadiness.canInvoke == true
    ' "$CANARY_STATE_DIR/chat-contract.json" >/dev/null; then
    ENDPOINT_READY=true
    break
  fi
  test "$STATUS" = "404" -o "$STATUS" = "200"
  sleep 2
done
test "$ENDPOINT_READY" = "true"
```

### 12. Refresh The Owner Catalog Exactly Once

This is the only explicit catalog refresh in the canary. Do not loop or prime a
projection from a query path.

```bash
STATUS="$(api_request POST /api/auth/nyxid/authorization-catalog:refresh \
  "$CANARY_STATE_DIR/catalog-refresh.json")"
ledger_set catalogRefreshHttpStatus "$STATUS"
expect_status 200 "$STATUS" authorization-catalog-refresh
jq -e '
  .ready == true
  and .refreshStatus == "observed"
  and .visibilityStatus == "ready"
  and .requiredStateVersion > 0
  and .visibleStateVersion >= .requiredStateVersion
' "$CANARY_STATE_DIR/catalog-refresh.json" >/dev/null
```

HTTP `202` means the committed refresh is not yet visible. HTTP `503` means the
catalog is unavailable or invalid. For either result, stop before automation
creation and clean up only the non-credential scaffold. Do not issue another
refresh in the same canary.

### 13. Preflight The Exact Disabled Schedule

Use an annual far-future cron and keep the recurring schedule disabled. Manual
`run-now` is admitted independently after the credential becomes active.

```bash
jq -n \
  --arg cron "0 0 1 1 *" \
  --arg timezone "UTC" \
  --arg prompt "$CANARY_PROMPT" \
  --arg displayName "Agent Key canary automation $CANARY_SUFFIX" '
  {
    scheduleCron: $cron,
    scheduleTimezone: $timezone,
    prompt: $prompt,
    displayName: $displayName,
    enabled: false
  }
' > "$CANARY_STATE_DIR/preflight.json"

STATUS="$(api_request POST \
  "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/preflight" \
  "$CANARY_STATE_DIR/preflight-response.json" \
  "$CANARY_STATE_DIR/preflight.json")"
expect_status 200 "$STATUS" automation-preflight

jq -e \
  --arg scope "$SCOPE_ID" \
  --arg team "$TEAM_ID" \
  --arg member "$MEMBER_ID" \
  --arg workflow "$DRAFT_WORKFLOW_ID" \
  --arg revision "$REVISION_ID" \
  --arg service "$PUBLISHED_SERVICE_ID" \
  --arg userService "$NYXID_USER_SERVICE_ID" \
  --arg slug "$NYXID_SERVICE_SLUG" \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg model "$EXPECTED_MODEL" \
  --arg policy "$EXPECTED_POLICY_VERSION" '
  .success == true
  and .plan.invocationTarget.studioMember.scopeId == $scope
  and .plan.invocationTarget.studioMember.teamId == $team
  and .plan.invocationTarget.studioMember.memberId == $member
  and .plan.invocationTarget.studioMember.draftWorkflowId == $workflow
  and .plan.invocationTarget.studioMember.workflowRevisionId == $revision
  and .plan.invocationTarget.studioMember.publishedServiceId == $service
  and .plan.credentialPolicy.allowAllServices == false
  and .plan.credentialPolicy.allowAllNodes == false
  and .plan.credentialPolicy.policyVersion == $policy
  and (([.plan.nyxIdServiceGrants[].userServiceId] | sort) == [$userService])
  and (([.plan.nyxIdServiceGrants[].serviceSlug] | sort) == [$slug])
  and .plan.ownerLlmSelection.routeKind == 2
  and .plan.ownerLlmSelection.routeValue == $route
  and .plan.ownerLlmSelection.nyxIdUserServiceId == $userService
  and .plan.ownerLlmSelection.serviceSlugSnapshot == $slug
  and .plan.ownerLlmSelection.model == $model
  and (.plan.permissionDigest | type == "string" and length > 0)
' "$CANARY_STATE_DIR/preflight-response.json" >/dev/null

PERMISSION_DIGEST="$(jq -er '.plan.permissionDigest' \
  "$CANARY_STATE_DIR/preflight-response.json")"
POLICY_VERSION="$(jq -er '.plan.credentialPolicy.policyVersion' \
  "$CANARY_STATE_DIR/preflight-response.json")"
test "$POLICY_VERSION" = "$EXPECTED_POLICY_VERSION"
ledger_set permissionDigest "$PERMISSION_DIGEST"
ledger_set policyVersion "$POLICY_VERSION"
```

`ownerLlmSelection.routeKind == 2` is the generated Protobuf JSON enum value for
`NyxIdUserService` on this response contract. Do not replace the exact typed
selection with route-string or slug inference.

No browser or operator supplies `workflowId`, `publishedServiceId`, service
grants, credential expiry, key ID, secret reference, or raw credential in the
automation mutation. They are server-derived from the exact member binding and
authorization plan.

### 14. Create And Observe The Dedicated Credential

```bash
set_failure_context "create" "$CREATE_OPERATION_ID"
jq -n \
  --arg cron "0 0 1 1 *" \
  --arg timezone "UTC" \
  --arg prompt "$CANARY_PROMPT" \
  --arg displayName "Agent Key canary automation $CANARY_SUFFIX" \
  --arg digest "$PERMISSION_DIGEST" \
  --arg policy "$POLICY_VERSION" \
  --arg operationId "$CREATE_OPERATION_ID" \
  --arg idempotencyKey "$CREATE_IDEMPOTENCY_KEY" '
  {
    scheduleCron: $cron,
    scheduleTimezone: $timezone,
    prompt: $prompt,
    displayName: $displayName,
    enabled: false,
    confirmedPermissionDigest: $digest,
    confirmedPolicyVersion: $policy,
    credentialProvisioningKind: "dedicated_scheduled_invocation_agent_key",
    operationId: $operationId,
    idempotencyKey: $idempotencyKey
  }
' > "$CANARY_STATE_DIR/automation-create.json"

STATUS="$(api_request POST \
  "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations" \
  "$CANARY_STATE_DIR/automation-create-response.json" \
  "$CANARY_STATE_DIR/automation-create.json")"
set_failure_context "create" "$CREATE_OPERATION_ID" "http_$STATUS" "create_failed"
expect_status 202 "$STATUS" create-automation
jq -e --arg operation "$CREATE_OPERATION_ID" '
  .accepted == true
  and .operationId == $operation
  and (.scheduleId | type == "string" and length > 0)
' "$CANARY_STATE_DIR/automation-create-response.json" >/dev/null

SCHEDULE_ID="$(jq -er '.scheduleId' \
  "$CANARY_STATE_DIR/automation-create-response.json")"
set_failure_context "create" "$CREATE_OPERATION_ID" "accepted" "create_observation_failed"

EXPECTED_SCHEDULE_ID="studio-member-workflow-$(
  printf '%s\n%s\n%s' "$SCOPE_ID" "$MEMBER_ID" "$CREATE_IDEMPOTENCY_KEY" \
    | openssl dgst -sha256 -r | awk '{print substr($1, 1, 32)}'
)"
test "$SCHEDULE_ID" = "$EXPECTED_SCHEDULE_ID"

digest24() {
  printf '%s' "$1" | openssl dgst -sha256 -r \
    | awk '{print substr($1, 1, 24)}'
}
EXPECTED_KEY_NAME="studio-schedule-$(digest24 "$SCHEDULE_ID")-$(digest24 "$CREATE_OPERATION_ID")"

ledger_set scheduleId "$SCHEDULE_ID"
ledger_set expectedKeyName "$EXPECTED_KEY_NAME"

jq -ne \
  --arg team "$TEAM_ID" \
  --arg member "$MEMBER_ID" \
  --arg workflow "$DRAFT_WORKFLOW_ID" \
  --arg service "$PUBLISHED_SERVICE_ID" \
  --arg schedule "$SCHEDULE_ID" '
  [$team, $member, $workflow, $service, $schedule] as $identities
  | ($identities | length) == 5
    and ($identities | unique | length) == 5
' >/dev/null
```

The uniqueness assertion is mandatory: Team, member, draft workflow, published
service, and schedule are five isolated identities.

If the request times out or returns anything other than `202`, do not invent a
new operation or idempotency key. Follow **Ambiguous Create Recovery** below.

Require the sanitized create event before invoking the schedule. The event is
write-side operational evidence only; do not add its binding to a projection or
public response.

```bash
CREATE_AUDIT_OBSERVED=false
for _ in $(seq 1 60); do
  if ! AEVATAR_AUDIT_SCOPE_ID="$SCOPE_ID" \
  AEVATAR_AUDIT_TEAM_ID="$TEAM_ID" \
  AEVATAR_AUDIT_MEMBER_ID="$MEMBER_ID" \
  AEVATAR_AUDIT_SCHEDULE_ID="$SCHEDULE_ID" \
  AEVATAR_AUDIT_OPERATION_ID="$CREATE_OPERATION_ID" \
    "$AUDIT_QUERY_TOOL" create \
      > "$CANARY_STATE_DIR/create-acceptance-audit.json"; then
    sleep 2
    continue
  fi

  jq -e '
    type == "array"
    and length <= 1
    and all(.[];
      (keys | sort) == ([
        "bindingId", "memberId", "operationId",
        "scheduleId", "scopeId", "teamId"
      ] | sort)
      and (.bindingId | type == "string" and length > 0)
    )
  ' "$CANARY_STATE_DIR/create-acceptance-audit.json" >/dev/null

  if jq -e \
      --arg scope "$SCOPE_ID" \
      --arg team "$TEAM_ID" \
      --arg member "$MEMBER_ID" \
      --arg schedule "$SCHEDULE_ID" \
      --arg operation "$CREATE_OPERATION_ID" '
      length == 1
      and .[0].scopeId == $scope
      and .[0].teamId == $team
      and .[0].memberId == $member
      and .[0].scheduleId == $schedule
      and .[0].operationId == $operation
    ' "$CANARY_STATE_DIR/create-acceptance-audit.json" >/dev/null; then
    CREATE_AUDIT_OBSERVED=true
    break
  fi
  sleep 2
done
test "$CREATE_AUDIT_OBSERVED" = "true"
VERIFIED_BINDING_ID="$(jq -er '.[0].bindingId' \
  "$CANARY_STATE_DIR/create-acceptance-audit.json")"
ledger_set verifiedBindingId "$VERIFIED_BINDING_ID"
```

Poll the owner-scoped detail until the projected lifecycle is active.

```bash
AUTOMATION_ACTIVE=false
for _ in $(seq 1 90); do
  STATUS="$(api_request GET \
    "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/$SCHEDULE_ID" \
    "$CANARY_STATE_DIR/automation-detail.json")"
  if test "$STATUS" = "404"; then
    sleep 2
    continue
  fi
  expect_status 200 "$STATUS" get-automation
  AUTHORIZATION_STATUS="$(jq -er '.authorizationStatus' \
    "$CANARY_STATE_DIR/automation-detail.json")"
  case "$AUTHORIZATION_STATUS" in
    active)
      AUTOMATION_ACTIVE=true
      break
      ;;
    failed|needs_authorization|revocation_pending)
      printf 'STOP: automation reached %s before active\n' \
        "$AUTHORIZATION_STATUS" >&2
      exit 1
      ;;
  esac
  sleep 2
done
test "$AUTOMATION_ACTIVE" = "true"

jq -e \
  --arg scope "$SCOPE_ID" \
  --arg team "$TEAM_ID" \
  --arg member "$MEMBER_ID" \
  --arg schedule "$SCHEDULE_ID" \
  --arg publishedService "$PUBLISHED_SERVICE_ID" \
  --arg userService "$NYXID_USER_SERVICE_ID" \
  --arg slug "$NYXID_SERVICE_SLUG" \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg model "$EXPECTED_MODEL" \
  --arg operation "$CREATE_OPERATION_ID" '
  .scopeId == $scope
  and .teamId == $team
  and .memberId == $member
  and .scheduleId == $schedule
  and .publishedServiceId == $publishedService
  and .operationId == $operation
  and .authorizationStatus == "active"
  and .credentialSourceKind == "scheduled_invocation_agent_key"
  and .credentialGeneration > 0
  and .revocationPending == false
  and .nyxIdRevocationStatus == "NotRequired"
  and .vaultRevocationStatus == "NotRequired"
  and .ownerLLMRouteKind == "nyx_id_user_service"
  and .ownerLLMRoute == $route
  and .ownerLLMUserServiceId == $userService
  and .ownerLLMServiceSlug == $slug
  and .ownerLLMModel == $model
  and .enabled == false
  and .stateVersion > 0
' "$CANARY_STATE_DIR/automation-detail.json" >/dev/null

ACTIVE_STATE_VERSION="$(jq -er '.stateVersion' \
  "$CANARY_STATE_DIR/automation-detail.json")"
set_failure_context "create" "$CREATE_OPERATION_ID" \
  "active" "create_observation_failed" "$ACTIVE_STATE_VERSION"
ledger_set activeStateVersion "$ACTIVE_STATE_VERSION"
```

Independently locate the one exact NyxID key. Persist no key material.

```bash
capture_exact_nyxid_key \
  "$EXPECTED_KEY_NAME" "" "$CANARY_STATE_DIR/api-key-before-run.json"

jq -e \
  --arg name "$EXPECTED_KEY_NAME" \
  --arg service "$NYXID_USER_SERVICE_ID" '
  .name == $name
  and .is_active == true
  and .allow_all_services == false
  and .allow_all_nodes == false
  and ((.allowed_service_ids | sort) == [$service])
  and (.expires_at | type == "string" and length > 0)
' "$CANARY_STATE_DIR/api-key-before-run.json" >/dev/null

NYXID_KEY_ID="$(jq -er '.id' \
  "$CANARY_STATE_DIR/api-key-before-run.json")"
KEY_LAST_USED_BEFORE="$(jq -r '
  .last_used_at
  | if . == null then "" else . end
' "$CANARY_STATE_DIR/api-key-before-run.json")"
test -z "$KEY_LAST_USED_BEFORE"
ledger_set nyxIdApiKeyId "$NYXID_KEY_ID"
```

### 15. Run Now And Prove Agent Key Use

```bash
set_failure_context "run" "$RUN_OPERATION_ID"
jq -n \
  --arg operationId "$RUN_OPERATION_ID" \
  --arg idempotencyKey "$RUN_IDEMPOTENCY_KEY" '
  {operationId: $operationId, idempotencyKey: $idempotencyKey}
' > "$CANARY_STATE_DIR/run-now.json"

RUN_REQUESTED_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
ledger_set runRequestedAt "$RUN_REQUESTED_AT"

STATUS="$(api_request POST \
  "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/$SCHEDULE_ID/run-now" \
  "$CANARY_STATE_DIR/run-now-response.json" \
  "$CANARY_STATE_DIR/run-now.json")"
set_failure_context "run" "$RUN_OPERATION_ID" "http_$STATUS" "run_failed"
expect_status 202 "$STATUS" automation-run-now
jq -e \
  --arg schedule "$SCHEDULE_ID" \
  --arg operation "$RUN_OPERATION_ID" '
  .accepted == true
  and .scheduleId == $schedule
  and .operationId == $operation
' "$CANARY_STATE_DIR/run-now-response.json" >/dev/null

RUN_COMPLETED=false
for _ in $(seq 1 120); do
  STATUS="$(api_request GET \
    "/api/scopes/$SCOPE_ID/members/$MEMBER_ID/runs?take=20&scheduleId=$SCHEDULE_ID" \
    "$CANARY_STATE_DIR/member-runs.json")"
  expect_status 200 "$STATUS" list-member-runs
  if jq -e \
      --arg schedule "$SCHEDULE_ID" \
      --arg marker "$CANARY_MARKER" '
      [.runs[] | select(.scheduleId == $schedule)] as $matching
      | ($matching | length) == 1
        and $matching[0].workflowName == "simple_qa"
        and $matching[0].completionStatus == 1
        and $matching[0].lastSuccess == true
        and ($matching[0].lastOutput | type == "string")
        and ($matching[0].lastOutput | contains($marker))
    ' "$CANARY_STATE_DIR/member-runs.json" >/dev/null; then
    RUN_COMPLETED=true
    break
  fi
  sleep 2
done
test "$RUN_COMPLETED" = "true"

RUN_ID="$(jq -er --arg schedule "$SCHEDULE_ID" '
  .runs[] | select(.scheduleId == $schedule) | .runId
' "$CANARY_STATE_DIR/member-runs.json")"
set_failure_context "run" "$RUN_OPERATION_ID" "completed" "run_evidence_failed"
ledger_set runId "$RUN_ID"

capture_exact_nyxid_key \
  "$EXPECTED_KEY_NAME" "$NYXID_KEY_ID" \
  "$CANARY_STATE_DIR/api-key-after-run.json"

jq -e \
  --arg name "$EXPECTED_KEY_NAME" \
  --arg keyId "$NYXID_KEY_ID" \
  --arg service "$NYXID_USER_SERVICE_ID" '
  .name == $name
  and .id == $keyId
  and .is_active == true
  and .allow_all_services == false
  and .allow_all_nodes == false
  and ((.allowed_service_ids | sort) == [$service])
  and (.last_used_at | type == "string" and length > 0)
' "$CANARY_STATE_DIR/api-key-after-run.json" >/dev/null

KEY_LAST_USED_AFTER="$(jq -er '.last_used_at' \
  "$CANARY_STATE_DIR/api-key-after-run.json")"
test -n "$KEY_LAST_USED_AFTER"
test "$KEY_LAST_USED_AFTER" != "$KEY_LAST_USED_BEFORE"
ledger_set keyLastUsedAt "$KEY_LAST_USED_AFTER"

STATUS="$(api_request GET \
  "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/$SCHEDULE_ID" \
  "$CANARY_STATE_DIR/automation-after-run.json")"
expect_status 200 "$STATUS" get-automation-after-run
jq -e \
  --argjson priorVersion "$ACTIVE_STATE_VERSION" \
  --arg userService "$NYXID_USER_SERVICE_ID" \
  --arg slug "$NYXID_SERVICE_SLUG" \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg model "$EXPECTED_MODEL" '
  .authorizationStatus == "active"
  and .credentialSourceKind == "scheduled_invocation_agent_key"
  and .revocationPending == false
  and .nyxIdRevocationStatus == "NotRequired"
  and .vaultRevocationStatus == "NotRequired"
  and .ownerLLMRouteKind == "nyx_id_user_service"
  and .ownerLLMRoute == $route
  and .ownerLLMUserServiceId == $userService
  and .ownerLLMServiceSlug == $slug
  and .ownerLLMModel == $model
  and .lastFireAt != null
  and .stateVersion > $priorVersion
' "$CANARY_STATE_DIR/automation-after-run.json" >/dev/null
POST_RUN_STATE_VERSION="$(jq -er '.stateVersion' \
  "$CANARY_STATE_DIR/automation-after-run.json")"
set_failure_context "run" "$RUN_OPERATION_ID" \
  "completed" "run_evidence_failed" "$POST_RUN_STATE_VERSION"
ledger_set postRunStateVersion "$POST_RUN_STATE_VERSION"
```

Do not accept successful LLM prose, an accepted run receipt, or a completed run
alone as proof of credential selection. The exact key's `last_used_at`
transition is mandatory.

### 16. Delete And Wait For Both Revocation Tracks

Create the canonical delete body once. Every pending or failed-track replay
uses this same file; only the Host-derived bearer may be fresh.

```bash
set_failure_context "delete" "$DELETE_OPERATION_ID"
jq -n \
  --arg operationId "$DELETE_OPERATION_ID" \
  --arg idempotencyKey "$DELETE_IDEMPOTENCY_KEY" \
  --arg scopeId "$SCOPE_ID" \
  --arg teamId "$TEAM_ID" \
  --arg memberId "$MEMBER_ID" '
  {
    reason: "scheduled_agent_key_canary_cleanup",
    operationId: $operationId,
    idempotencyKey: $idempotencyKey,
    owner: {
      kind: "studio_member_automation",
      scopeId: $scopeId,
      teamId: $teamId,
      memberId: $memberId
    }
  }
' > "$CANARY_STATE_DIR/delete.json"

STATUS="$(api_request DELETE \
  "/api/schedules/$SCHEDULE_ID" \
  "$CANARY_STATE_DIR/delete-response.json" \
  "$CANARY_STATE_DIR/delete.json")"
set_failure_context "delete" "$DELETE_OPERATION_ID" "http_$STATUS" "delete_failed"
expect_status 202 "$STATUS" delete-automation
jq -e \
  --arg schedule "$SCHEDULE_ID" \
  --arg operation "$DELETE_OPERATION_ID" '
  .accepted == true
  and .status == "pending"
  and .scheduleId == $schedule
  and .operationId == $operation
' "$CANARY_STATE_DIR/delete-response.json" >/dev/null
```

The `202 Accepted` receipt is admission only. Continue reading the canonical
owner detail until both revocation tracks are terminal and the row becomes not
found.

Use one bounded observer. While the public row is visible, its two track values
must be exact implemented values and an empty historical value fails closed.
When detail first returns owner-correct `404`, the observer waits for the
repository audit query to prove exactly `Completed/Completed`. Only then does
it accept the `404`.

```bash
set_failure_context "revocation" "$DELETE_OPERATION_ID"
REVOCATION_TERMINAL_OBSERVED=false
REVOCATION_TERMINAL_STATE_VERSION=""
REVOCATION_TERMINAL_OBSERVED_AT=""
LAST_DELETE_HTTP_STATUS=""

capture_terminal_revocation_audit() {
  if ! AEVATAR_AUDIT_SCOPE_ID="$SCOPE_ID" \
  AEVATAR_AUDIT_TEAM_ID="$TEAM_ID" \
  AEVATAR_AUDIT_MEMBER_ID="$MEMBER_ID" \
  AEVATAR_AUDIT_SCHEDULE_ID="$SCHEDULE_ID" \
  AEVATAR_AUDIT_OPERATION_ID="$DELETE_OPERATION_ID" \
    "$AUDIT_QUERY_TOOL" revocation \
      > "$CANARY_STATE_DIR/revocation-terminal-audit.json"; then
    return 1
  fi

  jq -e '
    type == "array"
    and length <= 1
    and all(.[];
      (keys | sort) == ([
        "memberId", "nyxIdRevocationStatus", "observedAtUtc", "operationId",
        "scheduleId", "scopeId", "stateVersion", "teamId",
        "vaultRevocationStatus"
      ] | sort)
      and (.stateVersion | type == "number" and . > 0)
      and (.observedAtUtc | type == "string" and length > 0)
    )
  ' "$CANARY_STATE_DIR/revocation-terminal-audit.json" >/dev/null || {
    printf '%s\n' 'STOP: malformed or ambiguous filtered revocation evidence.' >&2
    exit 1
  }

  if ! jq -e \
      --arg scope "$SCOPE_ID" \
      --arg team "$TEAM_ID" \
      --arg member "$MEMBER_ID" \
      --arg schedule "$SCHEDULE_ID" \
      --arg operation "$DELETE_OPERATION_ID" '
      length == 1
      and .[0].scopeId == $scope
      and .[0].teamId == $team
      and .[0].memberId == $member
      and .[0].scheduleId == $schedule
      and .[0].operationId == $operation
      and .[0].nyxIdRevocationStatus == "Completed"
      and .[0].vaultRevocationStatus == "Completed"
    ' "$CANARY_STATE_DIR/revocation-terminal-audit.json" >/dev/null; then
    return 1
  fi

  REVOCATION_TERMINAL_STATE_VERSION="$(jq -er '.[0].stateVersion' \
    "$CANARY_STATE_DIR/revocation-terminal-audit.json")"
  REVOCATION_TERMINAL_OBSERVED_AT="$(jq -er '.[0].observedAtUtc' \
    "$CANARY_STATE_DIR/revocation-terminal-audit.json")"
  set_failure_context "revocation" "$DELETE_OPERATION_ID" \
    "completed" "revocation_evidence_failed" "$REVOCATION_TERMINAL_STATE_VERSION"
  REVOCATION_TERMINAL_OBSERVED=true
  ledger_set revocationTerminalStateVersion "$REVOCATION_TERMINAL_STATE_VERSION"
  ledger_set revocationTerminalObservedAt "$REVOCATION_TERMINAL_OBSERVED_AT"
}

wait_for_automation_terminal() {
  local attempt status
  for attempt in $(seq 1 60); do
    status="$(api_request GET \
      "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/$SCHEDULE_ID" \
      "$CANARY_STATE_DIR/automation-delete-state.json")"
    LAST_DELETE_HTTP_STATUS="$status"
    set_failure_context "revocation" "$DELETE_OPERATION_ID" \
      "http_$status" "revocation_evidence_failed"
    if test "$status" = "404"; then
      if capture_terminal_revocation_audit; then
        return 0
      fi
      sleep 2
      continue
    fi
    expect_status 200 "$status" observe-automation-delete
    jq -e '
      (
        .authorizationStatus == "active"
        and .revocationPending == false
        and .nyxIdRevocationStatus == "NotRequired"
        and .vaultRevocationStatus == "NotRequired"
      )
      or (
        (.authorizationStatus == "deleting"
         or .authorizationStatus == "revocation_pending")
        and .revocationPending == true
        and ([.nyxIdRevocationStatus, .vaultRevocationStatus]
             | all(. == "Pending" or . == "Completed" or . == "Failed"))
        and ([.nyxIdRevocationStatus, .vaultRevocationStatus]
             != ["Completed", "Completed"])
      )
    ' "$CANARY_STATE_DIR/automation-delete-state.json" >/dev/null
    sleep 2
  done
  return 1
}

REVOCATION_RETRY_REQUIRED=false
if ! wait_for_automation_terminal; then
  if test "$LAST_DELETE_HTTP_STATUS" = "200" && jq -e '
      .authorizationStatus == "revocation_pending"
      and .revocationPending == true
      and ([.nyxIdRevocationStatus, .vaultRevocationStatus]
           | any(. == "Pending" or . == "Failed"))
    ' "$CANARY_STATE_DIR/automation-delete-state.json" >/dev/null; then
    REVOCATION_RETRY_REQUIRED=true
    printf '%s\n' 'STOP: revocation remains pending; refresh the owner bearer before retry.' >&2
  else
    printf '%s\n' 'STOP: terminal revocation evidence is not visible; continue read-only observation.' >&2
    exit 1
  fi
fi
```

If the first observer did not reach `404`, refresh the bearer through the normal
login path, overwrite only the existing `STUDIO_TOKEN_FILE`, and re-run the
owner gate without printing either subject. Replay the same canonical DELETE
only after the last detail read reports `revocation_pending`,
`revocationPending == true`, and at least one `Pending` or `Failed` track:

```bash
if test "$REVOCATION_RETRY_REQUIRED" = "true"; then
  jq -e '
    .authorizationStatus == "revocation_pending"
    and .revocationPending == true
    and ([.nyxIdRevocationStatus, .vaultRevocationStatus]
         | any(. == "Pending" or . == "Failed"))
  ' "$CANARY_STATE_DIR/automation-delete-state.json" >/dev/null

  read_bearer
  STATUS="$(api_request GET /api/auth/me "$CANARY_STATE_DIR/auth-me-retry.json")"
  expect_status 200 "$STATUS" auth-me-before-retry
  jq -e --arg scope "$SCOPE_ID" '
    .authenticated == true
    and .session.authenticated == true
    and .scopeId == $scope
    and .session.scopeId == $scope
    and (.profile.subject | type == "string" and length > 0)
  ' "$CANARY_STATE_DIR/auth-me-retry.json" >/dev/null

  "$NYXID_BIN" whoami --output json \
    > "$CANARY_STATE_DIR/nyxid-whoami-retry.json"
  test "$(jq -er '.profile.subject' "$CANARY_STATE_DIR/auth-me-retry.json")" = \
    "$(jq -er '.id' "$CANARY_STATE_DIR/nyxid-whoami-retry.json")"

  STATUS="$(api_request DELETE \
    "/api/schedules/$SCHEDULE_ID" \
    "$CANARY_STATE_DIR/delete-replay-response.json" \
    "$CANARY_STATE_DIR/delete.json")"
  expect_status 202 "$STATUS" delete-replay
  jq -e \
    --arg schedule "$SCHEDULE_ID" \
    --arg operation "$DELETE_OPERATION_ID" '
    .accepted == true
    and .status == "pending"
    and .scheduleId == $schedule
    and .operationId == $operation
  ' "$CANARY_STATE_DIR/delete-replay-response.json" >/dev/null

  wait_for_automation_terminal
fi

test "$REVOCATION_TERMINAL_OBSERVED" = "true"
test "$LAST_DELETE_HTTP_STATUS" = "404"
```

If another replay is necessary, repeat only that block with a fresh bearer and
the unchanged canonical DELETE path and `delete.json`. Never replace the owner,
reason, or either delete identity.

The filtered operational audit evidence proves both tracks were terminal before the
detail `404` was accepted. Independently require the captured NyxID key ID and
its exact deterministic name to be absent or inactive before owner-resource
cleanup:

```bash
capture_exact_nyxid_key \
  "$EXPECTED_KEY_NAME" "$NYXID_KEY_ID" \
  "$CANARY_STATE_DIR/api-key-after-delete.json"

jq -e \
  --arg name "$EXPECTED_KEY_NAME" \
  --arg keyId "$NYXID_KEY_ID" '
  . == null
  or (.id == $keyId and .name == $name and .is_active == false)
' "$CANARY_STATE_DIR/api-key-after-delete.json" >/dev/null

STATUS="$(api_request GET \
  "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations" \
  "$CANARY_STATE_DIR/automations-after-delete.json")"
expect_status 200 "$STATUS" list-automations-after-delete
jq -e '
  (.items | type == "array" and length == 0)
  and .totalCount == 0
' "$CANARY_STATE_DIR/automations-after-delete.json" >/dev/null
```

If detail is `404` but the exact key remains active, stop and open an incident.
Manual `nyxid api-key delete` is outside this runbook and requires separate
explicit authority. Do not hide the invariant break by deleting the member or
archiving the Team.

### 17. Clean Up Only After Revocation Is Terminal

Keep this order: retire revision, delete member, delete draft, archive Team.

```bash
set_failure_context "cleanup_revision" "" "not_observed" "revision_retire_failed" "0"
STATUS="$(api_request POST \
  "/api/scopes/$SCOPE_ID/members/$MEMBER_ID/binding/revisions/$REVISION_ID:retire" \
  "$CANARY_STATE_DIR/retire-revision.json")"
set_failure_context "cleanup_revision" "" "http_$STATUS" "revision_retire_failed" "0"
expect_status 200 "$STATUS" retire-revision
jq -e \
  --arg member "$MEMBER_ID" \
  --arg service "$PUBLISHED_SERVICE_ID" \
  --arg revision "$REVISION_ID" '
  .memberId == $member
  and .publishedServiceId == $service
  and .revisionId == $revision
  and .status == "retired"
' "$CANARY_STATE_DIR/retire-revision.json" >/dev/null

set_failure_context "cleanup_member" "" "not_observed" "member_delete_failed" "0"
STATUS="$(api_request DELETE "/api/scopes/$SCOPE_ID/members/$MEMBER_ID" \
  "$CANARY_STATE_DIR/delete-member.json")"
set_failure_context "cleanup_member" "" "http_$STATUS" "member_delete_failed" "0"
expect_status 202 "$STATUS" delete-member

MEMBER_DELETED=false
for _ in $(seq 1 60); do
  set_failure_context "cleanup_member_observe" "" "not_observed" "member_delete_observe_failed" "0"
  STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/members/$MEMBER_ID" \
    "$CANARY_STATE_DIR/member-after-delete.json")"
  set_failure_context "cleanup_member_observe" "" "http_$STATUS" "member_delete_observe_failed" "0"
  if test "$STATUS" = "404"; then
    MEMBER_DELETED=true
    break
  fi
  expect_status 200 "$STATUS" observe-member-delete
  sleep 2
done
test "$MEMBER_DELETED" = "true"

set_failure_context "cleanup_draft" "" "not_observed" "draft_delete_failed" "0"
STATUS="$(api_request DELETE \
  "/api/workspace/workflow-drafts/$DRAFT_WORKFLOW_ID?scopeId=$SCOPE_ID" \
  "$CANARY_STATE_DIR/delete-draft.json")"
set_failure_context "cleanup_draft" "" "http_$STATUS" "draft_delete_failed" "0"
expect_status 204 "$STATUS" delete-workflow-draft

DRAFT_DELETED=false
for _ in $(seq 1 60); do
  set_failure_context "cleanup_draft_observe" "" "not_observed" "draft_delete_observe_failed" "0"
  STATUS="$(api_request GET \
    "/api/workspace/workflow-drafts/$DRAFT_WORKFLOW_ID?scopeId=$SCOPE_ID" \
    "$CANARY_STATE_DIR/draft-after-delete.json")"
  set_failure_context "cleanup_draft_observe" "" "http_$STATUS" "draft_delete_observe_failed" "0"
  if test "$STATUS" = "404"; then
    DRAFT_DELETED=true
    break
  fi
  expect_status 200 "$STATUS" observe-draft-delete
  sleep 2
done
test "$DRAFT_DELETED" = "true"

set_failure_context "cleanup_team" "" "not_observed" "team_archive_failed" "0"
STATUS="$(api_request POST "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/archive" \
  "$CANARY_STATE_DIR/archive-team.json")"
set_failure_context "cleanup_team" "" "http_$STATUS" "team_archive_failed" "0"
expect_status 202 "$STATUS" archive-team

TEAM_ARCHIVED=false
for _ in $(seq 1 60); do
  set_failure_context "cleanup_team_observe" "" "not_observed" "team_archive_observe_failed" "0"
  STATUS="$(api_request GET "/api/scopes/$SCOPE_ID/teams/$TEAM_ID" \
    "$CANARY_STATE_DIR/team-after-archive.json")"
  set_failure_context "cleanup_team_observe" "" "http_$STATUS" "team_archive_observe_failed" "0"
  expect_status 200 "$STATUS" observe-team-archive
  if jq -e --arg team "$TEAM_ID" '
      .teamId == $team and .lifecycleStage == "archived"
    ' "$CANARY_STATE_DIR/team-after-archive.json" >/dev/null; then
    TEAM_ARCHIVED=true
    break
  fi
  sleep 2
done
test "$TEAM_ARCHIVED" = "true"
```

### 18. Final Read-Only Assertions

```bash
set_failure_context "final_user_config" "" "not_observed" "final_user_config_failed" "0"
STATUS="$(api_request GET /api/user-config/llm \
  "$CANARY_STATE_DIR/user-config-after.json")"
set_failure_context "final_user_config" "" "http_$STATUS" "final_user_config_failed" "0"
expect_status 200 "$STATUS" final-user-config
jq -e \
  --arg id "$NYXID_USER_SERVICE_ID" \
  --arg slug "$NYXID_SERVICE_SLUG" \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg model "$EXPECTED_MODEL" '
  .savedRouteKind == "nyx_id_user_service"
  and .savedUserServiceId == $id
  and .savedServiceSlug == $slug
  and .savedRoute == $route
  and .defaultModel == $model
' "$CANARY_STATE_DIR/user-config-after.json" >/dev/null

set_failure_context "final_readiness" "" "not_observed" "final_readiness_failed" "0"
HTTP_STATUS="$(curl --disable --silent --show-error \
  --proto '=https' --connect-timeout 10 --max-time 60 \
  --output "$CANARY_STATE_DIR/final-ready.json" --write-out '%{http_code}' \
  "$AEVATAR_BASE_URL/health/ready")"
set_failure_context "final_readiness" "" "http_$HTTP_STATUS" "final_readiness_failed" "0"
test "$HTTP_STATUS" = "200"
jq -e '.ok == true and .status == "ready"' \
  "$CANARY_STATE_DIR/final-ready.json" >/dev/null

set_failure_context "final_key" "" "not_observed" "final_key_observe_failed" "0"
capture_exact_nyxid_key \
  "$EXPECTED_KEY_NAME" "$NYXID_KEY_ID" \
  "$CANARY_STATE_DIR/api-key-final.json"
set_failure_context "final_key" "" "observed" "final_key_observe_failed" "0"
jq -e \
  --arg name "$EXPECTED_KEY_NAME" \
  --arg keyId "$NYXID_KEY_ID" '
  . == null
  or (.id == $keyId and .name == $name and .is_active == false)
' "$CANARY_STATE_DIR/api-key-final.json" >/dev/null
```

The Team remains as an archived lifecycle record by design. The member and
draft must return `404`; the automation was already proven absent while the
member still existed.

## Recovery Procedures

### Resume From The Non-Secret Ledger

If the shell exits after a mutation, do not start a new canary. Point
`CANARY_STATE_DIR` at the exact canonical path originally returned by this
runbook, recreate the helper functions above, and let initialization validate
the mode-`0700` directory and its owner-only sentinel before refreshing the
bearer or restoring identities. Never move or copy a sentinel to authorize a
different path. Restore identities without printing them:

```bash
LEDGER="$CANARY_STATE_DIR/ledger.json"
TEAM_ID="$(jq -r '.teamId // empty' "$LEDGER")"
MEMBER_ID="$(jq -r '.memberId // empty' "$LEDGER")"
DRAFT_WORKFLOW_ID="$(jq -r '.draftWorkflowId // empty' "$LEDGER")"
PUBLISHED_SERVICE_ID="$(jq -r '.publishedServiceId // empty' "$LEDGER")"
REVISION_ID="$(jq -r '.revisionId // empty' "$LEDGER")"
SCHEDULE_ID="$(jq -r '.scheduleId // empty' "$LEDGER")"
EXPECTED_KEY_NAME="$(jq -r '.expectedKeyName // empty' "$LEDGER")"
CREATE_OPERATION_ID="$(jq -r '.createOperationId // empty' "$LEDGER")"
CREATE_IDEMPOTENCY_KEY="$(jq -r '.createIdempotencyKey // empty' "$LEDGER")"
DELETE_OPERATION_ID="$(jq -r '.deleteOperationId // empty' "$LEDGER")"
DELETE_IDEMPOTENCY_KEY="$(jq -r '.deleteIdempotencyKey // empty' "$LEDGER")"
```

Missing optional keys mean the canary stopped before that resource existed.
Query each known canonical resource before deciding which cleanup step applies.

### Ambiguous Create Recovery

If automation create was sent but its `202` response was lost or non-terminal:

1. Do not use a new create operation or idempotency key.
2. List the exact member's automations and find a row whose `operationId`
   equals `CREATE_OPERATION_ID`.
3. If found, capture its `scheduleId`, derive the deterministic key name, and
   resume observation or deletion.
4. If the row is not yet visible, continue bounded reads. Also list NyxID keys
   for the deterministic name. Do not print the list.
5. The schedule ID is independently deterministic:
   `studio-member-workflow-` plus the first 32 lowercase hex characters of
   `SHA256(scopeId + "\n" + memberId + "\n" + createIdempotencyKey)`.
6. If an active deterministic key exists but no owner-scoped automation becomes
   visible, stop and open an incident. Do not manually delete owner resources or
   mint a replacement key.

The exact-request replay, if approved by the incident owner, must reuse both
the original create `operationId` and `idempotencyKey` with byte-equivalent
normalized schedule semantics. Payload drift is a conflict, not a second
canary.

### Stop Before Automation Creation

If Team, draft, member, or binding creation succeeded but automation creation
was never attempted, no scheduled key should exist. Verify the deterministic
key name is absent, then clean the scaffold in this order:

1. Retire the revision if binding succeeded.
2. Delete the member and observe `404`.
3. Delete the draft and observe `404`.
4. Archive the Team and observe `lifecycleStage == "archived"`.

Do not use this shortcut after any automation create request was sent.

### Run Failure

If `run-now` is accepted but the run fails, times out, lacks the marker, or does
not update the exact key's `last_used_at`, the canary failed. Do not retry with
new run identities as a way to hide the first outcome. Delete the automation,
drive revocation to terminal `404`, verify the key inactive, then perform normal
scaffold cleanup. Retain only sanitized run ID, status, state version, and UTC
timestamps for diagnosis.

### Revocation Pending

An automation row must remain visible while either NyxID or Vault revocation is
unfinished. Refresh the owner bearer, re-prove the owner/scope, and replay the
same `DELETE /api/schedules/{scheduleId}` with the original `delete.json`.
Never delete the member or archive the Team to make a pending row disappear.

If the error indicates a blocked missing Vault secret reference, ordinary
retry cannot repair it. Stop and use the separately governed Host/Admin
maintenance path; do not fabricate a Vault reference or manually edit state.

## Evidence Checklist

Record these non-secret facts in the approved change ticket:

- required fix SHA, deployed source SHA, immutable release digest, and running
  Pod digest;
- successful merge-base ancestry and OpenAPI field gate;
- health status and component names;
- canary Team ID, member ID, draft workflow ID, published service ID, binding
  run ID, verified binding ID, and revision ID, preserving their distinct
  identity labels;
- preflight permission digest, policy version, both false wildcard flags, and
  exact allowed UserService ID;
- schedule ID, create operation ID, run operation ID, delete operation ID, and
  their corresponding idempotency keys;
- exact NyxID key ID and deterministic name, without key prefix or raw value;
- active automation state version, run ID, marker, completion state, and
  `last_used_at` transition timestamps;
- terminal `Completed/Completed` projection state, delete detail `404`,
  independently inactive/absent NyxID key, owner automation list with both zero
  items and `totalCount == 0`, retired revision, deleted member/draft, and
  archived Team;
- final `/health/ready` and retained exact typed UserConfig assertions.

Do not attach the state directory, bearer file, raw API key, Vault data,
refresh token, auth profile, complete NyxID inventory, or backend logs.

### Extract The Allowlisted Bundle And Remove Local State

Write the explicit non-secret bundle to an approved path outside
`CANARY_STATE_DIR`, validate it, then destroy the entire state directory. The
cleanup helper revalidates the canonical path and owner-only sentinel
immediately before recursive deletion and fails closed on any mismatch. Do not
copy any other file out of that directory.

```bash
: "${CANARY_EVIDENCE_FILE:?set an approved new JSON path outside CANARY_STATE_DIR}"
case "$CANARY_EVIDENCE_FILE" in
  "$CANARY_STATE_DIR"|"$CANARY_STATE_DIR"/*) exit 1 ;;
esac
test ! -e "$CANARY_EVIDENCE_FILE"

jq -n \
  --arg requiredFixSha "$FINAL_PUSHED_RELEASE_SHA" \
  --arg deployedSourceSha "$DEPLOYED_SOURCE_SHA" \
  --arg releaseImageDigest "$RELEASE_IMAGE_DIGEST" \
  --arg scopeId "$SCOPE_ID" \
  --arg teamId "$TEAM_ID" \
  --arg memberId "$MEMBER_ID" \
  --arg draftWorkflowId "$DRAFT_WORKFLOW_ID" \
  --arg publishedServiceId "$PUBLISHED_SERVICE_ID" \
  --arg bindingRunId "$BINDING_RUN_ID" \
  --arg verifiedBindingId "$VERIFIED_BINDING_ID" \
  --arg revisionId "$REVISION_ID" \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg userServiceId "$NYXID_USER_SERVICE_ID" \
  --arg serviceSlug "$NYXID_SERVICE_SLUG" \
  --arg model "$EXPECTED_MODEL" \
  --arg permissionDigest "$PERMISSION_DIGEST" \
  --arg policyVersion "$POLICY_VERSION" \
  --arg scheduleId "$SCHEDULE_ID" \
  --arg createOperationId "$CREATE_OPERATION_ID" \
  --arg createIdempotencyKey "$CREATE_IDEMPOTENCY_KEY" \
  --arg runOperationId "$RUN_OPERATION_ID" \
  --arg runIdempotencyKey "$RUN_IDEMPOTENCY_KEY" \
  --arg deleteOperationId "$DELETE_OPERATION_ID" \
  --arg deleteIdempotencyKey "$DELETE_IDEMPOTENCY_KEY" \
  --arg nyxIdKeyId "$NYXID_KEY_ID" \
  --arg nyxIdKeyName "$EXPECTED_KEY_NAME" \
  --arg runId "$RUN_ID" \
  --arg marker "$CANARY_MARKER" \
  --arg runRequestedAt "$RUN_REQUESTED_AT" \
  --arg keyLastUsedAt "$KEY_LAST_USED_AFTER" \
  --arg revocationObservedAt "$REVOCATION_TERMINAL_OBSERVED_AT" \
  --arg userConfigDisposition "$USER_CONFIG_DISPOSITION" \
  --argjson activeStateVersion "$ACTIVE_STATE_VERSION" \
  --argjson postRunStateVersion "$POST_RUN_STATE_VERSION" \
  --argjson revocationStateVersion "$REVOCATION_TERMINAL_STATE_VERSION" '
  {
    release: {
      requiredFixSha: $requiredFixSha,
      deployedSourceSha: $deployedSourceSha,
      imageDigest: $releaseImageDigest
    },
    identities: {
      scopeId: $scopeId,
      teamId: $teamId,
      memberId: $memberId,
      draftWorkflowId: $draftWorkflowId,
      publishedServiceId: $publishedServiceId,
      bindingRunId: $bindingRunId,
      verifiedBindingId: $verifiedBindingId,
      revisionId: $revisionId,
      scheduleId: $scheduleId,
      runId: $runId
    },
    ownerLLMSelection: {
      routeKind: "nyx_id_user_service",
      route: $route,
      userServiceId: $userServiceId,
      serviceSlug: $serviceSlug,
      model: $model
    },
    authorization: {
      permissionDigest: $permissionDigest,
      policyVersion: $policyVersion,
      allowAllServices: false,
      allowAllNodes: false
    },
    operations: {
      create: {operationId: $createOperationId, idempotencyKey: $createIdempotencyKey},
      run: {operationId: $runOperationId, idempotencyKey: $runIdempotencyKey},
      delete: {operationId: $deleteOperationId, idempotencyKey: $deleteIdempotencyKey}
    },
    keyUse: {
      keyId: $nyxIdKeyId,
      keyName: $nyxIdKeyName,
      marker: $marker,
      runRequestedAt: $runRequestedAt,
      lastUsedAt: $keyLastUsedAt
    },
    versions: {
      active: $activeStateVersion,
      postRun: $postRunStateVersion,
      revocationTerminal: $revocationStateVersion
    },
    revocation: {
      nyxIdRevocationStatus: "Completed",
      vaultRevocationStatus: "Completed",
      observedAtUtc: $revocationObservedAt,
      detailHttpStatus: 404,
      exactKeyInactiveOrAbsent: true
    },
    cleanup: {
      automationItems: 0,
      automationTotalCount: 0,
      revisionRetired: true,
      memberDeleted: true,
      draftDeleted: true,
      teamArchived: true,
      healthStatus: "ready"
    },
    userConfigDisposition: $userConfigDisposition
  }
' > "$CANARY_EVIDENCE_FILE"
chmod 600 "$CANARY_EVIDENCE_FILE"

jq -e \
  --arg route "$NYXID_PROXY_ROUTE" \
  --arg service "$NYXID_USER_SERVICE_ID" \
  --arg model "$EXPECTED_MODEL" '
  .ownerLLMSelection.routeKind == "nyx_id_user_service"
  and .ownerLLMSelection.route == $route
  and .ownerLLMSelection.userServiceId == $service
  and .ownerLLMSelection.model == $model
  and .authorization.allowAllServices == false
  and .authorization.allowAllNodes == false
  and .revocation.nyxIdRevocationStatus == "Completed"
  and .revocation.vaultRevocationStatus == "Completed"
  and .revocation.detailHttpStatus == 404
  and .cleanup.automationItems == 0
  and .cleanup.automationTotalCount == 0
' "$CANARY_EVIDENCE_FILE" >/dev/null

unset STUDIO_BEARER
set_failure_context "local_state_cleanup" "" "not_removed" "local_state_cleanup_failed" "0"
remove_canary_state_dir "$CANARY_STATE_DIR"
test ! -e "$CANARY_STATE_DIR"
unset CANARY_STATE_DIR LEDGER
```

## Contract References

- `docs/canon/scheduled-skill-runners.md`
- `docs/adr/0041-scheduled-invocation-agent-key-credential-reference.md`
- `docs/adr/0043-scheduled-credential-lifecycle-compensation.md`
- `docs/superpowers/specs/2026-07-22-owner-llm-exact-service-identity-design.md`
- `src/Aevatar.Studio.Hosting/Endpoints/StudioMemberAutomationEndpoints.cs`
- `src/Aevatar.Studio.Application/Studio/Services/StudioMemberWorkflowSchedulePort.cs`
- `agents/Aevatar.GAgents.Scheduled/StudioScheduledCredentialMaterializer.cs`
