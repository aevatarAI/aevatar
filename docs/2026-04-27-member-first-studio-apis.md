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

The resolver is exposed through `IMemberPublishedServiceResolver`, so a later actor-owned member catalog can replace the deterministic mapping without changing HTTP routes. Member-first routes are authorized at the scope boundary; `memberId` identifies the target member-owned resource, not the authenticated principal. Binding write authority remains owned by the Studio member async protocol.

## Routes

| Route | Purpose |
|---|---|
| `POST /api/scopes/{scopeId}/members` | Create a shell Studio member only. The request must omit `implementationRef`. |
| `GET /api/scopes/{scopeId}/members/{memberId}/published-service` | Resolve the member-owned published service id. |
| `GET /api/scopes/{scopeId}/members/{memberId}/binding` | Read the member authority's last successful binding and current async binding run. |
| `PUT /api/scopes/{scopeId}/members/{memberId}/binding` | Start an async workflow/script/GAgent binding run for the member-owned published service. Returns `202 Accepted`. |
| `GET /api/scopes/{scopeId}/members/{memberId}/binding-runs/{bindingRunId}` | Read the eventually-consistent status read model for one binding run. |
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
- Member create is shell-only. `POST /api/scopes/{scopeId}/members` creates the member authority with lifecycle `created` and no `implementationRef`; workflow/script/GAgent implementation attachment is only accepted through post-create binding.
- A create request that includes `implementationRef` returns `400 STUDIO_MEMBER_CREATE_IMPLEMENTATION_REF_NOT_ALLOWED` with `field = "implementationRef"`. Clients should omit `implementationRef`, create the shell member, then call `PUT /api/scopes/{scopeId}/members/{memberId}/binding`.
- Binding writes are asynchronous. The `PUT /binding` response only means the command was accepted for dispatch and returns a stable `bindingRunId`; completion is observed through `GET /binding-runs/{bindingRunId}` or `GET /binding`.
- Workflow binding requests must carry `workflow.workflowId` as the stable workflow identity. The first YAML document's `name` remains the runtime/display `workflowName` and may differ from `workflowId`; successful member binding returns the stable id in `implementationRef.workflow.workflowId`.
- The binding-run `Location` is read-model backed and can be briefly unavailable immediately after `202 Accepted`. Clients should treat a short-lived `404` for the accepted run id as pending/read-model lag, not as terminal failure. Only explicit `failed` or `rejected` run status should surface as a binding error.
- Binding execution still publishes through the existing service command/runtime path after the member actor has admitted the request and resolved its `publishedServiceId`.
- Runs and run detail still read workflow run read models; they do not query actor state or replay events.
- Responses use `publishedServiceId` instead of overloading `serviceId` in member-centric DTOs.
- The member-first public contract does not accept an `appId` override or expose the fixed service namespace.
- The legacy scope-service member binding routes under `Aevatar.GAgentService.Hosting` are intentionally removed; member binding uses the StudioMember async protocol so the member actor remains the single authority for `LastBinding`, active run status, and `BindReady`.
