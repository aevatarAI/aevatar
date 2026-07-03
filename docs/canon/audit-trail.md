---
title: "Audit Trail Query Capability"
status: active
owner: eanzhao
---

# Audit Trail Query Capability

`Aevatar.Audit.Hosting` owns the audit read surface. Host projects may compose it as
a capability bundle, but endpoint handlers must not read projection stores or document
readers directly. Audit records are queried through `IAuditTrailQueryPort`.

## HTTP Surface

| Route | Method | Authorization | Semantics |
|---|---|---|---|
| `/api/audit/trail` | `GET` | Authenticated caller; platform admin only when `scope` targets another scope | Query materialized audit records. Missing `scope` means caller scope. |
| `/api/audit/actor-resolutions` | `POST` | Platform admin | Resolve an external actor identity to `auditActorId`. |

The resolver accepts raw external identity only in the JSON request body. It must never
put that identity in path or query parameters, must not log it, and must not return it.
The only returned identity is the server-computed `auditActorId` from
`IAuditActorIdentityHasher`.

## Permission Matrix

Default audit queries resolve to the caller's `scope_id` claim and do not call the
platform-admin authorizer. Any cross-scope query must resolve the caller through
`IPlatformAdminAuthorizer` before `IAuditTrailQueryPort` is invoked. Resolver calls are
always platform-admin reads.

If `IAuditTrailQueryPort` is not configured, `/api/audit/trail` returns
`503 AUDIT_QUERY_UNAVAILABLE`; it must not fall back to projection store access or
event replay. If platform-admin authorization is unavailable for an admin-only path,
the endpoint returns `503 AUDIT_ADMIN_AUTH_UNAVAILABLE`.

## Read Honesty

Audit query responses expose `readTimestampUtc` and `queryWatermark`. Each record also
exposes `occurredAtUtc` and `recordedAtUtc`. These fields describe the materialized
read model freshness and must not imply strong consistency with writes that may still
be in flight.

## Endpoint Audit Metadata

Admin-only resolver reads and cross-scope audit trail reads carry endpoint metadata with
`AccessLevel = ADMIN`. That metadata is for the host self-audit pipeline; it does not
replace the runtime admin gate.
