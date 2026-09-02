---
title: "NyxID Scheduled Agent Key Scope Plan Integration"
status: approved
owner: eanzhao
---

# NyxID Scheduled Agent Key Scope Plan Integration

## Semantic Mismatch

The existing Aevatar authorization model treats NyxID grants as ordered primary/fallback routing topology, while NyxID now publishes canonical service and node permission sets plus a selection-scoped mutation precondition.

This is a contract and runtime mismatch. Aevatar must consume the published permission-set contract without reconstructing route roles, bindings, priorities, or multiplicity.

## Published Authority

NyxID `main` publishes `POST /api/v1/api-keys/scope-plan` through issue #1207 and PR #1209. The request contains the exact selected user-service IDs and an optional target organization. The response contains:

- authenticated actor and intended key owner;
- contract and policy versions;
- one typed node grant per selected service;
- canonical `allowed_service_ids` and `allowed_node_ids` sets;
- completeness and mutation-revalidation declarations;
- an opaque `normalized_grant_digest` accepted as `scope_plan_digest` by key creation.

The digest binds the exact selected service set. A digest obtained for all visible services is not valid for a workflow that selects a subset.

## Ownership Decision

The owner-scoped catalog actor remains the sole Aevatar authority for observed service and node evidence. Its refresh adapter performs:

1. `GET /api/v1/user-services` for active, grant-eligible service inventory.
2. `POST /api/v1/api-keys/scope-plan` for those exact service IDs.
3. Strict response validation and one typed actor observation.
4. Normal committed-state projection into the existing read model.

The catalog persists service identity, resource owner, typed node requirement, exact node IDs, NyxID contract/policy versions, provider evaluation time, local observation time, local freshness, and Aevatar's protobuf content digest. It does not persist the full-inventory `normalized_grant_digest` as a reusable credential precondition.

The API-key issuer owns the transient mutation handshake. Immediately before creation it requests a second scope plan for the authorization plan's exact service subset, compares every owner/service/node fact with the confirmed local plan, and sends that response's digest to key creation. A mismatch fails closed as an authorization-plan change.

## Supported Owner Boundary

Catalog refresh remains personal-owner only. An organization plan depends on the authenticated administrator as well as the intended organization, while the current catalog actor is keyed only by intended owner. Sharing one organization snapshot across administrators would conflate authority.

The key-creation adapter continues to pass `target_org_id` for an already validated organization plan, but this change does not add organization catalog activation.

## Typed Model

`NyxIdAuthorizationServiceEvidence` and `NyxIdServiceGrant` carry:

- exact user-service ID, slug, and display name;
- exact resource owner identity;
- `required` or `not_required` node-grant semantics;
- canonical node IDs associated with that service.

The following unpublished concepts are removed:

- primary/fallback node role;
- edge kind;
- binding identity;
- route priority;
- ordered or duplicate permission-set semantics;
- the redundant runtime-wide node-grant collection.

Service IDs and flat node IDs are ordinal-sorted unique sets. Per-service node association is retained. The same node may be associated with more than one service, while the key's flat node allowlist contains it once.

## Freshness And Integrity

NyxID `evaluated_at` is provider evidence, not an external revision. Aevatar applies an explicit local catalog freshness window and stores it separately.

The local `ContentDigest` remains SHA-256 over the typed protobuf owner and service evidence. The authorization `PermissionDigest` remains SHA-256 over the typed local plan. Neither is interchangeable with NyxID's opaque `normalized_grant_digest`.

NyxID key creation performs the final current-state recomputation. A stale route, narrowed permission, changed node set, or changed digest is rejected before any key is returned.

## Failure Semantics

- Authentication failures invalidate the catalog and return `AccessDenied`.
- Malformed inventory, malformed scope plans, owner/contract/set mismatch, and unresolved selected routes invalidate the catalog with a stable catalog failure code.
- Transport, rate-limit, and server failures record refresh failure without extending freshness.
- Issuance scope-plan mismatch returns `authorization_plan_changed` and does not call key creation.
- Issuance scope-plan provider failures return a stable sanitized error and do not expose bearer tokens or raw secrets.
- Cancellation remains cancellation and is not converted into provider failure JSON.

## DI Boundary

NyxID API access registration is independently reusable by scheduled dispatch, Studio, and the full NyxID tool package. Singleton consumers depend on `INyxIdApiClientFactory` and create a client per operation; they do not capture a transient typed client.

REST calls prefer `Aevatar:NyxId:ApiBaseUrl`, then the configured authority aliases, then the existing NyxID default.

## Verification Contract

Tests must lock:

- user-service filtering and canonical service selection;
- exact personal owner, contract, freshness, completeness, service, and node mapping;
- actor/projection protobuf round trip without bearer fields;
- deterministic unique local authorization plans;
- targeted scope-plan comparison before key creation;
- `scope_plan_digest` and exact returned allowlists in the create request;
- no create effect after any response mismatch or provider error;
- removal of stale topology terminology from the changed authorization surface;
- independent GAgentService and Studio composition.
