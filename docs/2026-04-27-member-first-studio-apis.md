# Member-First Studio APIs

## Scope

This slice closes the minimum backend contract gap for Studio member-first Bind / Invoke / Observe flows.

The current authoritative resolver maps each normalized `memberId` to a stable published service with the same id:

| Field | Meaning |
|---|---|
| `scopeId` | Team scope that owns the member and published service. |
| `memberId` | Studio member address used by frontend routes. |
| `publishedServiceId` | Stable service id used by backend service runtime for that member. |
| `publishedServiceKey` | Internal service identity key for diagnostics and contract inspection. |

The resolver is exposed through `IMemberPublishedServiceResolver`, so a later actor-owned member catalog can replace the deterministic mapping without changing HTTP routes. Until that catalog is authoritative, member binding writes are restricted to scope administrators; member reads and invokes require either the matching authenticated member claim or a scope administrator role.

## Routes

| Route | Purpose |
|---|---|
| `GET /api/scopes/{scopeId}/members/{memberId}/published-service` | Resolve the member-owned published service id. |
| `GET /api/scopes/{scopeId}/members/{memberId}/binding` | Read current binding status for the member-owned published service. |
| `PUT /api/scopes/{scopeId}/members/{memberId}/binding` | Publish workflow/script/GAgent implementation to the member-owned published service. |
| `POST /api/scopes/{scopeId}/members/{memberId}/invoke/{endpointId}` | Invoke a typed endpoint by member id. |
| `POST /api/scopes/{scopeId}/members/{memberId}/invoke/{endpointId}:stream` | Invoke an SSE endpoint by member id. |
| `GET /api/scopes/{scopeId}/members/{memberId}/runs` | List read-model-backed runs for the member-owned published service. |
| `GET /api/scopes/{scopeId}/members/{memberId}/runs/{runId}` | Read a run summary for the member-owned published service. |
| `GET /api/scopes/{scopeId}/members/{memberId}/runs/{runId}/audit` | Read a run audit report for the member-owned published service. |
| `POST /api/scopes/{scopeId}/members/{memberId}/runs/{runId}:resume` | Resume a member-owned published service run. |
| `POST /api/scopes/{scopeId}/members/{memberId}/runs/{runId}:signal` | Signal a member-owned published service run. |
| `POST /api/scopes/{scopeId}/members/{memberId}/runs/{runId}:stop` | Stop a member-owned published service run. |

## Semantics

- Member routes for Bind / Invoke / Observe-read / run lifecycle control do not require frontend callers to know or pass `serviceId`.
- Binding and invoke still use the existing service command/runtime path after the resolver has produced `publishedServiceId`.
- Runs and run detail still read workflow run read models; they do not query actor state or replay events.
- Responses use `publishedServiceId` instead of overloading `serviceId` in member-centric DTOs.
- The member-first public contract does not accept an `appId` override or expose the fixed service namespace.
