---
title: "Projection Version Regression Repair Runbook"
status: active
owner: platform
---

# Projection Version Regression Repair Runbook

## Purpose And Boundary

This runbook is the code-only incident-recovery procedure for these two
current-state replicas:

- Studio Workspace;
- personal NyxID authorization catalog used by scheduled Agent Key
  authorization.

It applies only when inspection proves that the Elasticsearch document version
is higher than the surviving authoritative actor/event-store version:

```text
expected document version > expected source version > 0
```

The two guarded admin routes are:

```text
POST /api/admin/scheduled-agent-key/projection-repair/workspace
POST /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog
```

Operators must use these routes. Do not manually edit, lower, or delete
Elasticsearch documents. Do not edit or delete Garnet actor state or event
documents. Do not hydrate actor state from Elasticsearch, replay events in a
request path, or add projection priming to a normal query. The application
performs an exact actor/version/event fingerprint check and an Elasticsearch
optimistic-concurrency delete before starting the authoritative rebuild when
the document exists. Once a prior guarded delete has made it absent, the code
cannot re-verify that deleted fingerprint; the narrower operator/audit retry
rule is documented below.

Examples below use only synthetic identities:

```text
scopeId = scope-alpha
workspaceActorId = studio-workspace:scope-alpha
catalogOwnerSubject = user-alpha
catalogActorId = catalog-actor-id-from-inspection-alpha
repairRequestId = repair-alpha
```

`catalog-actor-id-from-inspection-alpha` is deliberately an opaque placeholder.
Replace it only with the exact `actor_id` returned by catalog inspection; never
derive a catalog actor ID from the owner subject.

Never record a real bearer, Agent Key, refresh token, Vault reference,
production identity, or catalog contents in a command transcript, ticket,
report, screenshot, or evidence artifact. Keep shell tracing disabled while a
bearer is present and retain only the allowlisted, redacted response fields
shown by these endpoints.

## Preconditions

Before either repair:

1. Confirm the deployed release includes the guarded repair routes and the
   target-specific repair services.
2. Obtain an elevated platform-owner bearer for synthetic example subject
   `user-alpha`. Put it in a dedicated mode-`0600` file outside the repository;
   do not print it, export it as a shell value, or persist it anywhere else.
3. Assign an incident/change record and the synthetic example request ID
   `repair-alpha`. A real execution must use its own non-secret request ID and a
   concise reason containing no catalog or credential data.
4. Confirm normal query traffic is not being used to repair or prime either
   projection.

Run the examples as a protected, mode-`0600` non-interactive Bash script. Do
not paste them into an interactive terminal or a recorded operator session.
The script disables history and tracing, reads the bearer only through a
mode-`0600` file, keeps request/response bodies in a mode-`0700` temporary
directory, prints only HTTP status codes, and removes the directory on exit.

```bash
set +x
unset HISTFILE
set +o history 2>/dev/null || true
set -euo pipefail
umask 077

: "${AEVATAR_BASE_URL:?set the deployed Aevatar base URL}"
: "${ELEVATED_OWNER_BEARER_FILE:?set the mode-0600 elevated owner bearer file}"
test -f "$ELEVATED_OWNER_BEARER_FILE"

if stat -f '%Lp' "$ELEVATED_OWNER_BEARER_FILE" >/dev/null 2>&1; then
  BEARER_FILE_MODE="$(stat -f '%Lp' "$ELEVATED_OWNER_BEARER_FILE")"
else
  BEARER_FILE_MODE="$(stat -c '%a' "$ELEVATED_OWNER_BEARER_FILE")"
fi
test "$BEARER_FILE_MODE" = "600"
unset BEARER_FILE_MODE

awk '
  NR == 1 {
    if ($0 == "" || $0 ~ /[\r"\\]/) exit 1
    valid = 1
    next
  }
  { exit 1 }
  END { if (valid != 1) exit 1 }
' "$ELEVATED_OWNER_BEARER_FILE" >/dev/null

REPAIR_DIR="$(mktemp -d "${TMPDIR:-/tmp}/aevatar-projection-repair.XXXXXX")"
chmod 700 "$REPAIR_DIR"
cleanup_repair_dir() {
  rm -rf -- "$REPAIR_DIR"
}
trap cleanup_repair_dir EXIT
trap 'exit 1' HUP INT TERM

protected_api_request() {
  local method="$1"
  local path="$2"
  local request_file="$3"
  local response_file="$4"
  local max_time="${5:-60}"
  local -a args=(
    --disable --silent --show-error
    --proto '=https' --connect-timeout 10 --max-time "$max_time"
    --request "$method"
    --output "$response_file"
    --write-out '%{http_code}'
  )
  case "$path" in /*) ;; *) return 2 ;; esac
  if test -n "$request_file"; then
    test -f "$request_file"
    args+=(
      --header 'Content-Type: application/json'
      --data-binary "@$request_file"
    )
  fi

  curl "${args[@]}" \
    --config <(
      awk '
        NR == 1 {
          printf "header = \"Authorization: Bearer %s\"\n", $0
          exit
        }
      ' "$ELEVATED_OWNER_BEARER_FILE"
    ) \
    "$AEVATAR_BASE_URL$path"
}

expect_http_status() {
  local expected="$1"
  local actual="$2"
  local operation="$3"
  if test "$actual" != "$expected"; then
    printf 'STOP: %s returned HTTP %s; expected %s\n' \
      "$operation" "$actual" "$expected" >&2
    return 1
  fi
}
```

Set the bearer-file path and other inputs outside shell history, then execute
the protected script with a clean non-interactive shell. Do not print or attach
the protected request/response directory.

## Workspace Repair

### 1. Inspect With `apply=false`

Build the Workspace inspection body in the protected directory and capture the
response without printing it:

```bash
SCOPE_ID="scope-alpha"
WORKSPACE_INSPECT_REQUEST="$REPAIR_DIR/workspace-inspect-request.json"
WORKSPACE_INSPECT_RESPONSE="$REPAIR_DIR/workspace-inspect-response.json"

jq -n --arg scopeId "$SCOPE_ID" '
  {
    scope_id: $scopeId,
    apply: false,
    expected_actor_id: "",
    expected_source_state_version: 0,
    expected_document_state_version: 0,
    expected_document_last_event_id: "",
    repair_request_id: "",
    repair_reason: ""
  }
' > "$WORKSPACE_INSPECT_REQUEST"

HTTP_STATUS="$(protected_api_request POST \
  /api/admin/scheduled-agent-key/projection-repair/workspace \
  "$WORKSPACE_INSPECT_REQUEST" "$WORKSPACE_INSPECT_RESPONSE")"
expect_http_status 200 "$HTTP_STATUS" workspace-inspection
```

Continue only when HTTP `200` reports:

- `repairable: true`;
- `actor_id: "studio-workspace:scope-alpha"`;
- `document_actor_id` exactly equal to `actor_id`;
- `source_state_version > 0`;
- `document_state_version > source_state_version`;
- a non-empty `document_last_event_id`.

Copy the exact `actor_id`, source version, document version, and last event ID
from this response. Do not normalize, infer, increment, or otherwise edit them.

```bash
jq -e '
  .status == "inspection"
  and .repairable == true
  and .actor_id == "studio-workspace:scope-alpha"
  and .document_actor_id == .actor_id
  and .source_state_version > 0
  and .document_state_version > .source_state_version
  and (.document_last_event_id | type == "string" and length > 0)
' "$WORKSPACE_INSPECT_RESPONSE" >/dev/null

WORKSPACE_ACTOR_ID="$(jq -er '.actor_id' "$WORKSPACE_INSPECT_RESPONSE")"
WORKSPACE_SOURCE_VERSION="$(
  jq -er '.source_state_version' "$WORKSPACE_INSPECT_RESPONSE"
)"
WORKSPACE_DOCUMENT_VERSION="$(
  jq -er '.document_state_version' "$WORKSPACE_INSPECT_RESPONSE"
)"
WORKSPACE_LAST_EVENT_ID="$(
  jq -er '.document_last_event_id' "$WORKSPACE_INSPECT_RESPONSE"
)"
```

### 2. Apply The Exact Workspace Manifest

Build `apply=true` only from the exact values extracted above:

```bash
WORKSPACE_REPAIR_REQUEST_ID="repair-alpha"
WORKSPACE_REPAIR_REASON="restore the regressed workspace current-state replica"
WORKSPACE_APPLY_REQUEST="$REPAIR_DIR/workspace-apply-request.json"
WORKSPACE_APPLY_RESPONSE="$REPAIR_DIR/workspace-apply-response.json"

jq -n \
  --arg scopeId "$SCOPE_ID" \
  --arg actorId "$WORKSPACE_ACTOR_ID" \
  --argjson sourceVersion "$WORKSPACE_SOURCE_VERSION" \
  --argjson documentVersion "$WORKSPACE_DOCUMENT_VERSION" \
  --arg lastEventId "$WORKSPACE_LAST_EVENT_ID" \
  --arg repairRequestId "$WORKSPACE_REPAIR_REQUEST_ID" \
  --arg repairReason "$WORKSPACE_REPAIR_REASON" '
  {
    scope_id: $scopeId,
    apply: true,
    expected_actor_id: $actorId,
    expected_source_state_version: $sourceVersion,
    expected_document_state_version: $documentVersion,
    expected_document_last_event_id: $lastEventId,
    repair_request_id: $repairRequestId,
    repair_reason: $repairReason
  }
' > "$WORKSPACE_APPLY_REQUEST"

HTTP_STATUS="$(protected_api_request POST \
  /api/admin/scheduled-agent-key/projection-repair/workspace \
  "$WORKSPACE_APPLY_REQUEST" "$WORKSPACE_APPLY_RESPONSE")"
expect_http_status 202 "$HTTP_STATUS" workspace-apply
jq -e '
  .status == "accepted"
  and (.command_id | type == "string" and length > 0)
' "$WORKSPACE_APPLY_RESPONSE" >/dev/null
```

The only successful apply response is HTTP `202 Accepted` with
`status: "accepted"`. It proves only that the guarded delete completed or was
already absent and the typed Workspace republish command was accepted for
dispatch. `command_id` is correlation evidence, not visibility evidence.

Workspace republish is valid because the Workspace actor still owns a positive
committed version and its current state is the authoritative source. The actor
re-emits that committed current state through the existing committed-fact
Projection Pipeline without appending a synthetic repair event.

### 3. Establish Workspace Visibility Through The Normal API

After `202 Accepted`, query the ordinary Workspace-backed API until the expected
draft is visible from the repaired read model. For an example draft `wf-alpha`,
use the normal read surface:

```text
GET /api/workspace/workflow-drafts/wf-alpha?scopeId=scope-alpha
```

Do not treat the admin response, command dispatch, process logs, or a direct
Elasticsearch read as the final user-visible proof. If normal visibility is not
established, stop and investigate the standard projection pipeline; do not
repeat deletion with an edited manifest.

If `wf-alpha` is the incident's temporary or hidden-draft cleanup target, delete
it only after visibility is established, using the ordinary Workspace mutation:

```text
DELETE /api/workspace/workflow-drafts/wf-alpha?scopeId=scope-alpha
```

Then verify the normal GET returns not found. This cleanup is a Workspace
business mutation; it is not a direct Elasticsearch or Garnet deletion.

## NyxID Catalog Repair

### 1. Inspect With The Same Elevated Owner Bearer

The catalog owner is not accepted in the HTTP body. The Host derives it from
the elevated caller, so inspection and apply must use the same bearer file
whose verified subject is represented by synthetic example `user-alpha`:

```bash
CATALOG_INSPECT_REQUEST="$REPAIR_DIR/catalog-inspect-request.json"
CATALOG_INSPECT_RESPONSE="$REPAIR_DIR/catalog-inspect-response.json"

jq -n '
  {
    apply: false,
    expected_actor_id: "",
    expected_source_state_version: 0,
    expected_document_state_version: 0,
    expected_document_last_event_id: "",
    repair_request_id: "",
    repair_reason: ""
  }
' > "$CATALOG_INSPECT_REQUEST"

HTTP_STATUS="$(protected_api_request POST \
  /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog \
  "$CATALOG_INSPECT_REQUEST" "$CATALOG_INSPECT_RESPONSE")"
expect_http_status 200 "$HTTP_STATUS" catalog-inspection
```

Continue only when HTTP `200` reports `repairable: true`, a positive source
version, a higher document version, matching source/document actor identities,
and a non-empty last event ID. Copy the exact returned `actor_id`, source
version, document version, and last event ID. In the example below,
`catalog-actor-id-from-inspection-alpha` stands for that exact opaque actor ID.

```bash
jq -e '
  .status == "inspection"
  and .repairable == true
  and .document_actor_id == .actor_id
  and .source_state_version > 0
  and .document_state_version > .source_state_version
  and (.document_last_event_id | type == "string" and length > 0)
' "$CATALOG_INSPECT_RESPONSE" >/dev/null

CATALOG_ACTOR_ID="$(jq -er '.actor_id' "$CATALOG_INSPECT_RESPONSE")"
CATALOG_SOURCE_VERSION="$(
  jq -er '.source_state_version' "$CATALOG_INSPECT_RESPONSE"
)"
CATALOG_DOCUMENT_VERSION="$(
  jq -er '.document_state_version' "$CATALOG_INSPECT_RESPONSE"
)"
CATALOG_LAST_EVENT_ID="$(
  jq -er '.document_last_event_id' "$CATALOG_INSPECT_RESPONSE"
)"
```

### 2. Apply And Fresh-Refresh

Use the same elevated bearer file and build the apply body from the exact
inspection manifest:

```bash
CATALOG_REPAIR_REQUEST_ID="repair-alpha"
CATALOG_REPAIR_REASON="restore the regressed personal authorization catalog replica"
CATALOG_APPLY_REQUEST="$REPAIR_DIR/catalog-apply-request.json"
CATALOG_APPLY_RESPONSE="$REPAIR_DIR/catalog-apply-response.json"

jq -n \
  --arg actorId "$CATALOG_ACTOR_ID" \
  --argjson sourceVersion "$CATALOG_SOURCE_VERSION" \
  --argjson documentVersion "$CATALOG_DOCUMENT_VERSION" \
  --arg lastEventId "$CATALOG_LAST_EVENT_ID" \
  --arg repairRequestId "$CATALOG_REPAIR_REQUEST_ID" \
  --arg repairReason "$CATALOG_REPAIR_REASON" '
  {
    apply: true,
    expected_actor_id: $actorId,
    expected_source_state_version: $sourceVersion,
    expected_document_state_version: $documentVersion,
    expected_document_last_event_id: $lastEventId,
    repair_request_id: $repairRequestId,
    repair_reason: $repairReason
  }
' > "$CATALOG_APPLY_REQUEST"

HTTP_STATUS="$(protected_api_request POST \
  /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog \
  "$CATALOG_APPLY_REQUEST" "$CATALOG_APPLY_RESPONSE")"

case "$HTTP_STATUS" in
  200)
    jq -e '
      .status == "ready"
      and .refresh_status == "observed"
      and .visibility_status == "ready"
      and .required_state_version > 0
      and .visible_state_version >= .required_state_version
    ' "$CATALOG_APPLY_RESPONSE" >/dev/null
    ;;
  202)
    jq -e '
      .status == "projection_pending"
      and .refresh_status == "observed"
      and .visibility_status == "projection_pending"
      and .required_state_version > 0
    ' "$CATALOG_APPLY_RESPONSE" >/dev/null
    ;;
  *)
    printf 'STOP: catalog apply returned HTTP %s\n' "$HTTP_STATUS" >&2
    exit 1
    ;;
esac
```

Catalog recovery must perform a fresh NyxID observation with the verified
owner's bearer. It must not republish an empty or older catalog actor state:
after lineage loss, the surviving actor state may not contain the authorization
evidence represented by the ahead replica, and Elasticsearch is never an
authority from which to hydrate it. Fresh refresh reconstructs typed catalog
facts through the normal actor-owned command/event path.

Interpret completion fields separately:

- HTTP `200`, `status: "ready"`, `refresh_status: "observed"`, and
  `visibility_status: "ready"` means the refresh committed and the required
  authoritative version is visible.
- HTTP `202 Accepted`, `status: "projection_pending"`, and
  `refresh_status: "observed"` means the actor committed the refreshed catalog,
  but the read model has not yet reached `required_state_version`.
  `observed` is not the same as `ready`.
- HTTP `503` means refresh or visibility failed or is unavailable. Stop and
  investigate; do not fabricate readiness from pending or stale evidence.

Mutation, automation creation, Agent Key creation, and canary execution must
remain stopped while catalog visibility is pending. The canonical Team
automation preflight is a pure planner/read-model query and is the only
permitted bounded readiness probe after the canary's non-credential
workflow/Team/member/published-service scaffold exists.

There is no standalone catalog GET for this proof. The dated canary creates the
non-credential scaffold before catalog refresh, so its normal preflight can
read the same catalog replica without creating an automation or credential.

For HTTP `202`, record `required_state_version` from the protected repair
response. Do not call repair apply or catalog refresh again. Reuse the exact
disabled schedule request that will later be reviewed for creation, and poll
only:

```text
POST /api/scopes/{scopeId}/teams/{teamId}/members/{memberId}/automations/preflight
```

The following synthetic scaffold identities stand for resources already
created by the dated canary before its catalog refresh:

```bash
TEAM_ID="team-alpha"
MEMBER_ID="m-alpha"
PUBLISHED_SERVICE_ID="svc-alpha"
EXPECTED_USER_SERVICE_ID="us-alpha"
EXPECTED_SERVICE_SLUG="service-alpha"
PREFLIGHT_REQUEST="$REPAIR_DIR/preflight-request.json"
PREFLIGHT_RESPONSE="$REPAIR_DIR/preflight-response.json"

jq -n '
  {
    scheduleCron: "0 0 1 1 *",
    scheduleTimezone: "UTC",
    prompt: "projection readiness probe alpha",
    displayName: "Projection readiness probe alpha",
    enabled: false
  }
' > "$PREFLIGHT_REQUEST"
```

If catalog apply returned `202`, run a bounded read-only probe. Continue only
when HTTP `200` returns `success: true`, the plan's catalog authority version is
at least the recorded required version, and the plan contains exactly the
expected non-wildcard UserService grant:

```bash
if test "$HTTP_STATUS" = "202"; then
  REQUIRED_CATALOG_STATE_VERSION="$(
    jq -er '.required_state_version' "$CATALOG_APPLY_RESPONSE"
  )"
  test "$REQUIRED_CATALOG_STATE_VERSION" -gt 0

  PREFLIGHT_READY=false
  PREFLIGHT_STARTED_AT="$SECONDS"
  PREFLIGHT_MAX_ATTEMPTS=12
  PREFLIGHT_DEADLINE_SECONDS=180

  for _ in $(seq 1 "$PREFLIGHT_MAX_ATTEMPTS"); do
    if test "$((SECONDS - PREFLIGHT_STARTED_AT))" \
        -ge "$PREFLIGHT_DEADLINE_SECONDS"; then
      break
    fi

    PREFLIGHT_STATUS="$(protected_api_request POST \
      "/api/scopes/$SCOPE_ID/teams/$TEAM_ID/members/$MEMBER_ID/automations/preflight" \
      "$PREFLIGHT_REQUEST" "$PREFLIGHT_RESPONSE" 10)"

    if test "$PREFLIGHT_STATUS" != "200"; then
      printf 'STOP: readiness preflight returned HTTP %s\n' \
        "$PREFLIGHT_STATUS" >&2
      break
    fi

    if jq -e \
        --argjson required "$REQUIRED_CATALOG_STATE_VERSION" \
        --arg scope "$SCOPE_ID" \
        --arg team "$TEAM_ID" \
        --arg member "$MEMBER_ID" \
        --arg publishedService "$PUBLISHED_SERVICE_ID" \
        --arg userService "$EXPECTED_USER_SERVICE_ID" \
        --arg serviceSlug "$EXPECTED_SERVICE_SLUG" '
        .success == true
        and .plan.catalogAuthority.actorStateVersion >= $required
        and .plan.invocationTarget.studioMember.scopeId == $scope
        and .plan.invocationTarget.studioMember.teamId == $team
        and .plan.invocationTarget.studioMember.memberId == $member
        and .plan.invocationTarget.studioMember.publishedServiceId
            == $publishedService
        and .plan.credentialPolicy.allowAllServices == false
        and .plan.credentialPolicy.allowAllNodes == false
        and (.plan.nyxIdServiceGrants | length) == 1
        and .plan.nyxIdServiceGrants[0].userServiceId == $userService
        and .plan.nyxIdServiceGrants[0].serviceSlug == $serviceSlug
      ' "$PREFLIGHT_RESPONSE" >/dev/null; then
      PREFLIGHT_READY=true
      break
    fi

    sleep 5
  done

  if test "$PREFLIGHT_READY" != "true"; then
    printf 'STOP: catalog readiness was not proven within the bounded preflight window\n' \
      >&2
    exit 1
  fi
fi
```

If the bounded probe does not succeed, stop and clean up only the
non-credential scaffold through its normal workflow/member/Team APIs. No
automation mutation, Agent Key, Vault secret, run-now, or repair retry is
allowed.

Once catalog apply returned `200` ready or the bounded preflight proves the
required version and exact grant, continue with the redacted production canary
defined in
[`2026-07-23-scheduled-agent-key-production-canary.md`](./2026-07-23-scheduled-agent-key-production-canary.md).
The repair route is incident recovery only and is never a preflight, query, or
readiness fallback.

## Conflict And Retry Rules

Any HTTP `409 Conflict` means an actor identity, source version, document
fingerprint, or Elasticsearch revision changed. Stop immediately and re-run
`apply=false`. Do not edit the old manifest to make it pass, do not retry a
lower-version write, and do not manually delete the document.

An already missing document can continue only as an operator-controlled
idempotent retry of the previously inspected strict manifest. The service
cannot reconstruct or cryptographically verify the deleted document's prior
actor/version/event fingerprint once the replica is absent. Reuse the exact
prior
`expected_actor_id`, source version, higher document version, last event ID,
request ID, and reason only when:

```text
prior expected document version > prior expected source version > 0
```

This narrow retry is an operator/audit control backed by the protected incident
record; it is not code-enforced proof of the deleted document's provenance. The
code still enforces the current canonical actor identity, a positive unchanged
source version, and strict
`expected_document_state_version > expected_source_state_version` before it
continues. A fresh inspection that merely says the document is absent does not
authorize inventing a document version or event ID. If the authoritative source
version or actor identity has changed, stop and escalate instead of reusing the
prior manifest. Enforceable prior-document provenance would require a future
signed or leased inspection token carried from inspection into apply/retry.

## Completion Evidence

Retain only non-secret evidence:

- deployed source SHA and change/incident reference;
- route and HTTP status;
- synthetic labels or one-way-redacted scope, owner, actor, event, repair, and
  command identifiers;
- source/document version numbers copied during the guarded operation;
- Workspace normal-API visibility and cleanup result;
- catalog delete, refresh, visibility, required-version, and visible-version
  statuses;
- scheduled preflight/canary pass or stop decision.

Never retain the bearer, Agent Key, refresh token, Vault reference, production
IDs, raw last event ID, raw command ID, full catalog payload, service inventory,
or catalog contents.
