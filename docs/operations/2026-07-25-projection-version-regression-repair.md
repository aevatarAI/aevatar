---
title: "Projection Version Regression Repair Runbook"
status: active
owner: platform
---

# Projection Version Regression Repair Runbook

## Purpose And Boundary

This runbook is the code-only incident-recovery procedure for these
current-state replicas:

- Studio Workspace;
- personal NyxID authorization catalog used by scheduled Agent Key
  authorization.
- the cluster-singleton Aevatar OAuth client used by NyxID login finalization.

It applies only when inspection proves that the Elasticsearch document version
is higher than the surviving authoritative actor/event-store version:

```text
expected document version > expected source version > 0
```

The guarded admin routes are:

```text
POST /api/admin/scheduled-agent-key/projection-repair/workspace
POST /api/admin/scheduled-agent-key/projection-repair/nyxid-catalog
POST /api/admin/identity/projection-repair/aevatar-oauth-client
```

Operators must use these routes. Do not manually edit, lower, or delete
Elasticsearch documents. Do not edit or delete Garnet actor state or event
documents. Do not hydrate actor state from Elasticsearch, replay events in a
request path, or add projection priming to a normal query. The application
performs an exact actor/version/event fingerprint check and an Elasticsearch
optimistic-concurrency delete before starting the authoritative rebuild when
the document exists. It reads the authoritative EventStore version again
immediately before deletion. If the delete result is transport-ambiguous, the
repair adapter performs one bounded exact reinspection of the leased
index/document/revision and treats it as absent only when that exact revision is
proven absent. Once a prior guarded delete has made it absent, the code cannot
re-verify that deleted fingerprint. The OAuth route therefore rejects a new
apply against an already-missing document; use the separately governed client
projection rebuild endpoint described below. Existing target-specific retry
rules for Workspace and Catalog remain documented below.

For the OAuth client, first inspect with `{ "apply": false }`, then apply the
exact actor ID, source version, document version, and last-event ID returned by
that response. A `202` means the exact stale replica was deleted (or the same
invocation reconciled an ambiguous delete against its leased revision) and
authoritative republish was accepted; it is not visibility proof. The repair
actor requires its current version to be at least the inspected version and to
equal the live EventStore commit point. This accepts a legitimate newer commit
while rejecting an ahead zombie activation. Poll
the normal OAuth client status/config reads for visibility. Never include HMAC material, vault
references, client secrets, or bearers in repair requests or incident evidence.

The repair capability is a separate Elasticsearch-only opt-in adapter. It is
not exposed by the ordinary projection store, other read models, or the
in-memory provider. Catalog repair command and refresh adapters are also
separate and are composed only with the Elasticsearch repair path.

After a guarded delete returns `Deleted`, or the same leased delete operation
reconciles as `AlreadyAbsent`, Workspace/OAuth republish dispatch and Catalog
refresh continue independently of the HTTP request cancellation token. Closing
the client connection does not cancel that authoritative recovery. A
disconnected client may miss the response or the visibility follow-up, so
establish completion through the normal read surfaces below; do not assume
disconnect means the repair stopped.

Unexpected downstream inspection or apply exceptions return a bodyless,
sanitized HTTP `503`. The response never serializes exception text, bearer or
credential values, or catalog contents. A cancellation exception propagates
only when the request token is actually canceled; authorization failures remain
fail-closed as `403`.

This hardening requires no new secret or configuration setting. OAuth recovery
from a post-delete process failure uses the existing governed client projection
rebuild endpoint; it never treats a missing document as proof of a prior delete.

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

Before any repair:

1. Confirm the deployed release includes the guarded repair routes and the
   target-specific repair services.
2. Obtain an elevated platform-owner bearer for synthetic example subject
   `user-alpha`. The credential owner must inject it into a disposable,
   one-run, mode-`0600` file outside the repository and provide that disposable
   path to this procedure. Never point this script at the long-lived
   secret-store source. By supplying the path, the credential owner transfers
   deletion responsibility to this script. Do not print the bearer, export it
   as a shell value, or persist it anywhere else.
3. Assign an incident/change record and the synthetic example request ID
   `repair-alpha`. A real execution must use its own non-secret request ID and a
   concise reason containing no catalog or credential data.
4. Confirm normal query traffic is not being used to repair or prime any
   target projection.
5. For OAuth version regression, use the platform-approved read-only runtime
   state inspection to prove that the initialized committed-state publication
   checkpoint equals the inspected EventStore source version and exact
   committed event, and that any runtime actor snapshot is absent or no newer
   than that source. Stop before apply if the checkpoint is ahead, a snapshot is
   ahead, or this proof is unavailable. This route deliberately does not lower
   or rewrite runtime checkpoints or snapshots.
6. For OAuth version regression, confirm every active pod runs the committed-
   version-bounded EventStore reader and the projection transport has returned
   to its normal lag baseline. Do not delete the replica while an older image
   can still publish the orphaned envelope. The stable visibility window below
   remains mandatory because persistent transport is at-least-once.

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
: "${ELEVATED_OWNER_BEARER_FILE:?set the disposable mode-0600 bearer file}"

cleanup_repair_material() {
  local bearer_file="${ELEVATED_OWNER_BEARER_FILE:-}"
  local repair_dir="${REPAIR_DIR:-}"
  local cleanup_failed=false

  if test -n "$bearer_file"; then
    rm -f -- "$bearer_file" || cleanup_failed=true
    if test -e "$bearer_file"; then
      cleanup_failed=true
    fi
  fi
  if test -n "$repair_dir"; then
    rm -rf -- "$repair_dir" || cleanup_failed=true
    if test -e "$repair_dir"; then
      cleanup_failed=true
    fi
  fi

  unset ELEVATED_OWNER_BEARER_FILE BEARER_FILE_MODE REPAIR_DIR
  if test "$cleanup_failed" = "true"; then
    printf 'STOP: protected repair material cleanup failed\n' >&2
    return 1
  fi
}
trap cleanup_repair_material EXIT
trap 'exit 1' HUP INT TERM

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

Set the disposable bearer-file path and other inputs outside shell history,
then execute the protected script with a clean non-interactive shell. The trap
deletes and verifies removal of both the disposable bearer file and protected
request/response directory, then clears bearer-related variables. Do not print
or attach either path or their contents.

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
```

### 2. Apply The Exact Workspace Manifest

Build `apply=true` by having `jq` read the protected inspection request,
inspection response, and a small protected operator-control manifest directly.
The inspected actor ID, versions, event ID, and scope ID never become process
arguments:

```bash
WORKSPACE_REPAIR_REQUEST_ID="repair-alpha"
WORKSPACE_REPAIR_REASON="restore the regressed workspace current-state replica"
WORKSPACE_APPLY_CONTROL="$REPAIR_DIR/workspace-apply-control.json"
WORKSPACE_APPLY_REQUEST="$REPAIR_DIR/workspace-apply-request.json"
WORKSPACE_APPLY_RESPONSE="$REPAIR_DIR/workspace-apply-response.json"

jq -n \
  --arg repairRequestId "$WORKSPACE_REPAIR_REQUEST_ID" \
  --arg repairReason "$WORKSPACE_REPAIR_REASON" '
  {
    repair_request_id: $repairRequestId,
    repair_reason: $repairReason
  }
' > "$WORKSPACE_APPLY_CONTROL"

jq -s '
  .[0] as $inspectionRequest
  | .[1] as $inspection
  | .[2] as $control
  | {
      scope_id: $inspectionRequest.scope_id,
      apply: true,
      expected_actor_id: $inspection.actor_id,
      expected_source_state_version: $inspection.source_state_version,
      expected_document_state_version: $inspection.document_state_version,
      expected_document_last_event_id: $inspection.document_last_event_id,
      repair_request_id: $control.repair_request_id,
      repair_reason: $control.repair_reason
    }
' \
  "$WORKSPACE_INSPECT_REQUEST" \
  "$WORKSPACE_INSPECT_RESPONSE" \
  "$WORKSPACE_APPLY_CONTROL" \
  > "$WORKSPACE_APPLY_REQUEST"

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
accepts the inspected source version as a minimum and re-emits its actual latest
committed current state through the existing committed-fact Projection
Pipeline. The republished version is greater than or equal to the inspected
minimum, and no synthetic repair event is appended.

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
```

### 2. Apply And Fresh-Refresh

Use the same elevated bearer file and have `jq` build the apply body directly
from the protected inspection response and a protected operator-control
manifest. No incident fingerprint or production identity is placed in process
arguments:

```bash
CATALOG_REPAIR_REQUEST_ID="repair-alpha"
CATALOG_REPAIR_REASON="restore the regressed personal authorization catalog replica"
CATALOG_APPLY_CONTROL="$REPAIR_DIR/catalog-apply-control.json"
CATALOG_APPLY_REQUEST="$REPAIR_DIR/catalog-apply-request.json"
CATALOG_APPLY_RESPONSE="$REPAIR_DIR/catalog-apply-response.json"

jq -n \
  --arg repairRequestId "$CATALOG_REPAIR_REQUEST_ID" \
  --arg repairReason "$CATALOG_REPAIR_REASON" '
  {
    repair_request_id: $repairRequestId,
    repair_reason: $repairReason
  }
' > "$CATALOG_APPLY_CONTROL"

jq -s '
  .[0] as $inspection
  | .[1] as $control
  | {
      apply: true,
      expected_actor_id: $inspection.actor_id,
      expected_source_state_version: $inspection.source_state_version,
      expected_document_state_version: $inspection.document_state_version,
      expected_document_last_event_id: $inspection.document_last_event_id,
      repair_request_id: $control.repair_request_id,
      repair_reason: $control.repair_reason
    }
' \
  "$CATALOG_INSPECT_RESPONSE" \
  "$CATALOG_APPLY_CONTROL" \
  > "$CATALOG_APPLY_REQUEST"

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
facts through the normal actor-owned command/event path. The repair command
checks that the Catalog actor's current version is at least the inspected
minimum and starts refresh with the actor's own lifecycle fence. It never
queries the deleted read model for lifecycle state.

Interpret completion fields separately:

- HTTP `200`, `status: "ready"`, `refresh_status: "observed"`, and
  `visibility_status: "ready"` means the refresh committed and the required
  authoritative version is visible.
- HTTP `202 Accepted`, `status: "projection_pending"`, and
  `refresh_status: "observed"` means the actor committed the refreshed catalog,
  but the read model has not yet reached `required_state_version`.
  `observed` is not the same as `ready`.
- HTTP `503` means refresh or visibility failed or is unavailable. Stop and
  investigate; the body is intentionally sanitized, so use server-side
  correlation and non-secret diagnostics rather than expecting exception
  details in the response. Do not fabricate readiness from pending or stale
  evidence.

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

## Aevatar OAuth Client Repair

Use this target only when the cluster-singleton OAuth client document is ahead
of its committed EventStore stream. It does not rotate HMAC material. If the
post-repair config read still reports `oauth_client_not_provisioned` and the
server-side diagnostic says the authoritative reference itself cannot resolve,
stop and use the separately governed lost-secret HMAC rotation procedure.

### 1. Inspect The OAuth Client

Create the read-only request inside the protected directory:

```bash
OAUTH_INSPECT_REQUEST="$REPAIR_DIR/oauth-inspect-request.json"
OAUTH_INSPECT_RESPONSE="$REPAIR_DIR/oauth-inspect-response.json"

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
' > "$OAUTH_INSPECT_REQUEST"

HTTP_STATUS="$(protected_api_request POST \
  /api/admin/identity/projection-repair/aevatar-oauth-client \
  "$OAUTH_INSPECT_REQUEST" "$OAUTH_INSPECT_RESPONSE")"
expect_http_status 200 "$HTTP_STATUS" oauth-client-inspection

OAUTH_REPAIR_MODE=""
if jq -e '
    .status == "inspection"
    and .repairable == true
    and .actor_id == "aevatar-oauth-client"
    and .document_actor_id == .actor_id
    and .source_state_version > 0
    and .document_state_version > .source_state_version
    and (.document_last_event_id | type == "string" and length > 0)
  ' "$OAUTH_INSPECT_RESPONSE" >/dev/null; then
  OAUTH_REPAIR_MODE="version-regression"
elif jq -e '
    .status == "inspection"
    and .repairable == false
    and .actor_id == "aevatar-oauth-client"
    and .source_state_version > 0
    and .document_state_version == null
  ' "$OAUTH_INSPECT_RESPONSE" >/dev/null; then
  # This is also the recovery branch after a guarded delete was committed but
  # its process died before repair dispatch. It needs no deleted fingerprint:
  # the existing governed rebuild publishes current authoritative actor state.
  OAUTH_REPAIR_MODE="missing-document-rebuild"
else
  printf 'STOP: OAuth client inspection is not an approved repair shape\n' >&2
  exit 1
fi
```

An equal/lower document, source version `0`, or actor mismatch is not
authorization to fabricate a manifest. A missing document with a positive
source version uses only the existing governed projection rebuild path; it is
never submitted as a version-regression apply or treated as proof of a prior
deleted fingerprint.

### 2. Apply The Exact Inspected Manifest

The request ID is an opaque non-secret value of at most 128 ASCII letters,
digits, `.`, `_`, `:`, or `-`. The reason is a single non-secret line of at
most 256 characters. The terminal endpoint audit records the elevated caller,
a one-way request-ID digest, versions, sanitized reason, and HTTP outcome; it
never records the bearer, raw request ID, HMAC material, vault reference, or raw
document event ID.

```bash
OAUTH_REPAIR_REQUEST_ID="repair-alpha"
OAUTH_REPAIR_REASON="restore regressed identity client replica from committed state"
OAUTH_APPLY_CONTROL="$REPAIR_DIR/oauth-apply-control.json"
OAUTH_APPLY_REQUEST="$REPAIR_DIR/oauth-apply-request.json"
OAUTH_APPLY_RESPONSE="$REPAIR_DIR/oauth-apply-response.json"

jq -n \
  --arg repairRequestId "$OAUTH_REPAIR_REQUEST_ID" \
  --arg repairReason "$OAUTH_REPAIR_REASON" '
  {
    repair_request_id: $repairRequestId,
    repair_reason: $repairReason
  }
' > "$OAUTH_APPLY_CONTROL"

jq -s '
  .[0] as $inspection
  | .[1] as $control
  | {
      apply: true,
      expected_actor_id: $inspection.actor_id,
      expected_source_state_version: $inspection.source_state_version,
      expected_document_state_version: $inspection.document_state_version,
      expected_document_last_event_id: $inspection.document_last_event_id,
      repair_request_id: $control.repair_request_id,
      repair_reason: $control.repair_reason
    }
' \
  "$OAUTH_INSPECT_RESPONSE" \
  "$OAUTH_APPLY_CONTROL" \
  > "$OAUTH_APPLY_REQUEST"

OAUTH_APPLY_ACCEPTED=false
if test "$OAUTH_REPAIR_MODE" = "missing-document-rebuild"; then
  OAUTH_REBUILD_REQUEST="$REPAIR_DIR/oauth-rebuild-request.json"
  OAUTH_REBUILD_RESPONSE="$REPAIR_DIR/oauth-rebuild-response.json"
  jq -n '{}' > "$OAUTH_REBUILD_REQUEST"
  HTTP_STATUS="$(protected_api_request POST \
    /api/oauth/aevatar-client/rebuild \
    "$OAUTH_REBUILD_REQUEST" "$OAUTH_REBUILD_RESPONSE")"
  expect_http_status 202 "$HTTP_STATUS" oauth-client-missing-document-rebuild
  jq -e '
    .status == "rebuild_pending"
    and (.projection_rebuild_command_id | type == "string" and length > 0)
  ' "$OAUTH_REBUILD_RESPONSE" >/dev/null
  OAUTH_APPLY_ACCEPTED=true
else
  HTTP_STATUS="$(protected_api_request POST \
    /api/admin/identity/projection-repair/aevatar-oauth-client \
    "$OAUTH_APPLY_REQUEST" "$OAUTH_APPLY_RESPONSE")"

  case "$HTTP_STATUS" in
    202)
      jq -e '
        .status == "accepted"
        and (.command_id | type == "string" and length > 0)
        and (.delete_status == "deleted" or .delete_status == "already_absent")
      ' "$OAUTH_APPLY_RESPONSE" >/dev/null
      OAUTH_APPLY_ACCEPTED=true
      ;;
    409)
      printf 'STOP: OAuth client manifest changed; inspect again\n' >&2
      exit 1
      ;;
    503)
      printf 'STOP: apply failed; restart from inspection, never reuse the old manifest\n' >&2
      exit 1
      ;;
    *)
      printf 'STOP: OAuth client apply returned HTTP %s\n' "$HTTP_STATUS" >&2
      exit 1
      ;;
  esac
fi

if test "$OAUTH_APPLY_ACCEPTED" != "true"; then
  printf 'STOP: OAuth client repair was not accepted after exact retries\n' >&2
  exit 1
fi
```

The apply command requires the actor's in-memory/current version to be at least
the inspected version and then compares it with the live EventStore commit
point inside the actor turn. A lower version is stale; an in-memory version
ahead of EventStore is a zombie activation. Both are rejected. A legitimate
newer committed version is re-published as the current authority. The command
appends no OAuth event and changes no client or HMAC fact.

That actor fence complements, but does not replace, the runtime publication
checkpoint precondition. Never delete the Elasticsearch replica merely to
discover afterward that a checkpoint-ahead actor cannot activate. Treat a
checkpoint or snapshot ahead of EventStore as a different recovery class and
stop this procedure before apply.

### 3. Prove Replica And Login Configuration Visibility

`202 Accepted` proves dispatch admission only. It does not prove that the
re-materialized document won the Elasticsearch write. Run a short stable-window
check through the repair inspection and ordinary login config/status APIs:

```bash
OAUTH_VERIFY_INSPECT_RESPONSE="$REPAIR_DIR/oauth-verify-inspect-response.json"
OAUTH_CONFIG_RESPONSE="$REPAIR_DIR/oauth-config-response.json"
OAUTH_STATUS_RESPONSE="$REPAIR_DIR/oauth-status-response.json"
OAUTH_EXPECTED_SOURCE_VERSION="$(
  jq -er '.source_state_version' "$OAUTH_INSPECT_RESPONSE"
)"
OAUTH_VISIBLE=false
OAUTH_STABLE_SAMPLES=0

for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
  VERIFY_STATUS="$(protected_api_request POST \
    /api/admin/identity/projection-repair/aevatar-oauth-client \
    "$OAUTH_INSPECT_REQUEST" "$OAUTH_VERIFY_INSPECT_RESPONSE")"
  CONFIG_STATUS="$(protected_api_request GET \
    /api/auth/nyxid/config \
    "" "$OAUTH_CONFIG_RESPONSE")"
  STATUS_STATUS="$(protected_api_request GET \
    /api/oauth/aevatar-client/status \
    "" "$OAUTH_STATUS_RESPONSE")"

  if test "$VERIFY_STATUS" = "200" \
      && test "$CONFIG_STATUS" = "200" \
      && test "$STATUS_STATUS" = "200" \
      && jq -e --argjson expected "$OAUTH_EXPECTED_SOURCE_VERSION" '
        .status == "inspection"
        and .repairable == false
        and .actor_id == "aevatar-oauth-client"
        and .document_actor_id == .actor_id
        and .source_state_version >= $expected
        and .document_state_version == .source_state_version
      ' "$OAUTH_VERIFY_INSPECT_RESPONSE" >/dev/null \
      && jq -e '
        (.base_url | type == "string" and length > 0)
        and (.client_id | type == "string" and length > 0)
        and (.scope | type == "string" and length > 0)
      ' "$OAUTH_CONFIG_RESPONSE" >/dev/null \
      && jq -e '.status != "not_provisioned"' "$OAUTH_STATUS_RESPONSE" >/dev/null; then
    OAUTH_STABLE_SAMPLES=$((OAUTH_STABLE_SAMPLES + 1))
    if test "$OAUTH_STABLE_SAMPLES" -ge 3; then
      OAUTH_VISIBLE=true
      break
    fi
  else
    OAUTH_STABLE_SAMPLES=0
  fi

  sleep 5
done

if test "$OAUTH_VISIBLE" != "true"; then
  printf 'STOP: OAuth client replica/config visibility was not stable\n' >&2
  exit 1
fi
```

The durable projection scope intentionally processes the authoritative lower
version even if its historical `lastSuccessfulVersion` remains at the orphaned
higher value. That high-water mark is not the OAuth repair success predicate;
do not lower or reset it. Use the exact document/source equality and ordinary
config/status reads above.

Finally, start a fresh console login and require the browser flow to complete.
Do not replay or retain an authorization code, PKCE verifier, or finalize
request as evidence. If `/api/auth/nyxid/config` becomes `503` again during the
stable window, investigate delayed orphan envelopes and server diagnostics;
do not manually overwrite Elasticsearch.

## Conflict And Retry Rules

Any HTTP `409 Conflict` means an actor identity, source version, document
fingerprint, or Elasticsearch revision changed. Stop immediately and re-run
`apply=false`. Do not edit the old manifest to make it pass, do not retry a
lower-version write, and do not manually delete the document.

Workspace and Catalog retain their target-specific operator-controlled retry
rules. OAuth does not accept a later apply against an already-missing document:
the service cannot reconstruct or cryptographically verify the deleted
document's prior actor/version/event fingerprint once the replica is absent.
Never reuse or reconstruct the old OAuth apply manifest. Restart at
`apply=false`; when it proves the canonical actor has a positive source version
and no document, use only `POST /api/oauth/aevatar-client/rebuild` as shown
above. That existing governed path republishes current authoritative state and
does not require the deleted fingerprint.

For targets whose existing retry contract permits an already-missing document,
reuse the exact prior actor ID, source version, higher document version, last
event ID, request ID, and reason only when:

```text
prior expected document version > prior expected source version > 0
```

That narrow Workspace/Catalog retry remains an operator/audit control backed by
the protected incident record; it is not code-enforced proof of the deleted
document's provenance. A fresh inspection that merely says the document is
absent does not authorize inventing a document version or event ID. OAuth avoids
this gap entirely by rejecting the absent-document apply shape.

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
- OAuth delete/dispatch status, exact source/document version equality, stable
  config/status HTTP outcomes, and fresh console-login pass or stop decision;
- scheduled preflight/canary pass or stop decision.

Never retain the bearer, Agent Key, refresh token, Vault reference, production
IDs, raw last event ID, raw command ID, full catalog payload, service inventory,
or catalog contents.
