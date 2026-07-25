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
optimistic-concurrency delete before starting the authoritative rebuild.

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
   `user-alpha`. Keep it only in a protected process environment; do not print
   it or write it to a file.
3. Assign an incident/change record and the synthetic example request ID
   `repair-alpha`. A real execution must use its own non-secret request ID and a
   concise reason containing no catalog or credential data.
4. Confirm normal query traffic is not being used to repair or prime either
   projection.

For shell examples, supply the host and bearer through protected environment
variables and leave tracing off:

```bash
set +x
: "${AEVATAR_BASE_URL:?set the deployed Aevatar base URL}"
: "${ELEVATED_OWNER_BEARER:?set the elevated owner bearer without printing it}"
```

## Workspace Repair

### 1. Inspect With `apply=false`

Call the Workspace route with only the scope identity and an empty, non-applying
manifest:

```bash
curl --fail-with-body --silent --show-error \
  -X POST \
  -H "Authorization: Bearer ${ELEVATED_OWNER_BEARER}" \
  -H "Content-Type: application/json" \
  "${AEVATAR_BASE_URL}/api/admin/scheduled-agent-key/projection-repair/workspace" \
  --data '{
    "scope_id": "scope-alpha",
    "apply": false,
    "expected_actor_id": "",
    "expected_source_state_version": 0,
    "expected_document_state_version": 0,
    "expected_document_last_event_id": "",
    "repair_request_id": "",
    "repair_reason": ""
  }'
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

### 2. Apply The Exact Workspace Manifest

The following values are examples. Replace the versions and event ID with the
exact inspection response while preserving every other identity boundary:

```bash
curl --fail-with-body --silent --show-error \
  -X POST \
  -H "Authorization: Bearer ${ELEVATED_OWNER_BEARER}" \
  -H "Content-Type: application/json" \
  "${AEVATAR_BASE_URL}/api/admin/scheduled-agent-key/projection-repair/workspace" \
  --data '{
    "scope_id": "scope-alpha",
    "apply": true,
    "expected_actor_id": "studio-workspace:scope-alpha",
    "expected_source_state_version": 7,
    "expected_document_state_version": 12,
    "expected_document_last_event_id": "workspace-event-alpha-12",
    "repair_request_id": "repair-alpha",
    "repair_reason": "restore the regressed workspace current-state replica"
  }'
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
the elevated caller, so inspection and apply must use the same bearer whose
verified subject is `user-alpha`:

```bash
curl --fail-with-body --silent --show-error \
  -X POST \
  -H "Authorization: Bearer ${ELEVATED_OWNER_BEARER}" \
  -H "Content-Type: application/json" \
  "${AEVATAR_BASE_URL}/api/admin/scheduled-agent-key/projection-repair/nyxid-catalog" \
  --data '{
    "apply": false,
    "expected_actor_id": "",
    "expected_source_state_version": 0,
    "expected_document_state_version": 0,
    "expected_document_last_event_id": "",
    "repair_request_id": "",
    "repair_reason": ""
  }'
```

Continue only when HTTP `200` reports `repairable: true`, a positive source
version, a higher document version, matching source/document actor identities,
and a non-empty last event ID. Copy the exact returned `actor_id`, source
version, document version, and last event ID. In the example below,
`catalog-actor-id-from-inspection-alpha` stands for that exact opaque actor ID.

### 2. Apply And Fresh-Refresh

Use the same elevated bearer and the exact inspection manifest:

```bash
curl --fail-with-body --silent --show-error \
  -X POST \
  -H "Authorization: Bearer ${ELEVATED_OWNER_BEARER}" \
  -H "Content-Type: application/json" \
  "${AEVATAR_BASE_URL}/api/admin/scheduled-agent-key/projection-repair/nyxid-catalog" \
  --data '{
    "apply": true,
    "expected_actor_id": "catalog-actor-id-from-inspection-alpha",
    "expected_source_state_version": 7,
    "expected_document_state_version": 12,
    "expected_document_last_event_id": "catalog-event-alpha-12",
    "repair_request_id": "repair-alpha",
    "repair_reason": "restore the regressed personal authorization catalog replica"
  }'
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

Do not run scheduled Agent Key preflight while catalog visibility is pending.
Once the catalog response is fully `observed/ready`, run the canonical
member-owned scheduled Agent Key preflight and the redacted production canary
defined in
[`2026-07-23-scheduled-agent-key-production-canary.md`](./2026-07-23-scheduled-agent-key-production-canary.md).
The repair route is incident recovery only and is never a preflight, query, or
readiness fallback.

## Conflict And Retry Rules

Any HTTP `409 Conflict` means an actor identity, source version, document
fingerprint, or Elasticsearch revision changed. Stop immediately and re-run
`apply=false`. Do not edit the old manifest to make it pass, do not retry a
lower-version write, and do not manually delete the document.

An already missing document is accepted only as an idempotent continuation of
the previously inspected strict manifest. Reuse the exact prior
`expected_actor_id`, source version, higher document version, last event ID,
request ID, and reason only when:

```text
prior expected document version > prior expected source version > 0
```

This narrow retry covers a prior guarded delete whose republish/refresh response
was interrupted or failed after deletion. A fresh inspection that merely says
the document is absent does not authorize inventing a document version or event
ID. If the authoritative source version or actor identity has changed, stop and
escalate instead of reusing the prior manifest.

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
