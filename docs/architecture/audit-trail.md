---
title: "Endpoint Audit Capture"
status: active
owner: eanzhao
---

# Endpoint Audit Capture

Endpoint audit capture records request-plane governance artifacts for annotated
HTTP endpoints. It is a boundary capture plane for security-relevant endpoint
attempts and outcomes; it is not a business read model, not request logging, and
not a committed fact source.

The endpoint owner declares audit intent with strongly typed
`EndpointAuditMetadata`:

| Field | Meaning |
|---|---|
| `operation_name` | Stable allowlisted operation name. |
| `sensitivity_level` | Audit sensitivity of the endpoint surface. |
| `target_kind` | Safe target resource family. |
| `target resolver` | Safe target id/display resolver from route or other allowlisted values. |
| `request/result sanitizer` | Allowlist summary builder; it must not copy request bodies, headers, tokens, cookies, prompts, credentials, or raw subjects. |

`EndpointAuditFilter` runs only for annotated endpoints. It captures safe
request/result summaries and target resolution for handler executions. The
filter does not append audit records and does not try to define terminal
authorization outcomes.

`EndpointAuditCaptureMiddleware` is registered by `Aevatar.Bootstrap` after
`UseAuthentication()` and before `UseAuthorization()`. That placement lets the
host record authenticated attempts and the terminal outcome even when
authorization middleware short-circuits with `403` before endpoint filters or
handlers run. Unauthenticated `401` challenges are not recorded because there is
no authenticated actor to hash.

The middleware is glue only:

1. Reads `EndpointAuditMetadata` from the selected endpoint.
2. Resolves the authenticated caller through `IAuditActorIdentityHasher`.
3. Appends `operation_name.attempted` plus exactly one terminal
   `operation_name` record through `IAuditTrailAppender`.
4. Fails open for business responses if audit append fails, while logging the
   operational failure.

Bootstrap must not define audit record schema, storage, query, retention,
identity hashing implementation, business endpoint inventory, or concrete
business sanitizer catalogs. Those remain in audit contracts, audit core, or
endpoint-owning modules.

Allowed endpoint summaries are intentionally narrow: route template, safe route
ids, status/outcome class, trace/request correlation, and sanitized target
identity. Token-shaped or secret-key-shaped values are redacted before append.
