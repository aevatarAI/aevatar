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
| `POST /api/scopes/{scopeId}/workflow/draft-run` | Test an inline or server-owned draft workflow. This is not a member invocation route. |

## Semantics

- `draft-run` and `member invoke` are separate command endpoints with separate business meanings. `draft-run` is for unsaved YAML preview or explicit draft testing. `member invoke` is the correct path for real execution of an existing member/service binding.
- A saved or published workflow member must not be run by sending its workflow definition back through `draft-run`. The frontend should call `POST /api/scopes/{scopeId}/members/{memberId}/invoke/{endpointId}` or `POST /api/scopes/{scopeId}/members/{memberId}/invoke/{endpointId}:stream`; the backend resolves workflow identity, active revision, prepared artifact, and service binding from `memberId` plus `endpointId`.
- Team entry execution routes through the Team invoke target and then resolves an entry member. It does not mix with `draft-run`.
- `draft-run` accepts workflow-level input as `startInput`; the existing `prompt` field remains a wire-compatible alias for current callers. New workflow Studio surfaces should prefer `startInput` unless the workflow contract is specifically an LLM prompt. If both `prompt` and `startInput` are supplied, they must be the same value.
- Inline draft preview must provide `workflowYamls`. A future server-owned draft identity may be added as a typed draft field, but `draft-run` must not accept `memberId`, `serviceId`, `bindingId`, or a generic source selector to run a published member.
- `member invoke` payloads are minimal: target identity comes from the path (`memberId` and `endpointId`), and the body carries only endpoint input, optional revision where supported, and tracing ids. It does not require `workflowYamls` or a workflow definition.
- Member routes for Bind / Invoke / Observe-read / run lifecycle control do not require frontend callers to know or pass `serviceId`.
- Member create is shell-only. `POST /api/scopes/{scopeId}/members` creates the member authority with lifecycle `created` and no `implementationRef`; workflow/script/GAgent implementation attachment is only accepted through post-create binding.
- A create request that includes `implementationRef` returns `400 STUDIO_MEMBER_CREATE_IMPLEMENTATION_REF_NOT_ALLOWED` with `field = "implementationRef"`. Clients should omit `implementationRef`, create the shell member, then call `PUT /api/scopes/{scopeId}/members/{memberId}/binding`.
- Binding writes are asynchronous. The `PUT /binding` response only means the command was accepted for dispatch and returns a stable `bindingRunId`; completion is observed through `GET /binding-runs/{bindingRunId}` or `GET /binding`.
- Workflow binding requests must carry `workflow.workflowId` as the stable workflow identity. The first YAML document's `name` remains the runtime/display `workflowName` and may differ from `workflowId`; successful member binding returns the stable id in `implementationRef.workflow.workflowId`.
- The binding-run `Location` is read-model backed and can be briefly unavailable immediately after `202 Accepted`. Clients should treat a short-lived `404` for the accepted run id as pending/read-model lag, not as terminal failure. Only explicit `failed` or `rejected` run status should surface as a binding error.
- Binding execution still publishes through the existing service command/runtime path after the member actor has admitted the request and resolved its `publishedServiceId`.
- Runs and run detail still read workflow run read models; they do not query actor state or replay events.
- The Runs tab for Workflow Studio is member-scoped. It should use `GET /api/scopes/{scopeId}/members/{memberId}/runs` and `GET /api/scopes/{scopeId}/members/{memberId}/runs/{runId}`, not a global execution list filtered by guessed `workflowId`, `serviceId`, or YAML content.
- Run summaries expose a compatible observation shape for shared rendering: `runId`, `runKind`, `completionStatus`, `status`, `createdAt`, `lastUpdatedAt`, `lastOutput`, `lastError`, and stream/read-model references such as `actorId`, `targetActorId`, `stateVersion`, and `lastEventId`.
- `runKind` is a display and observation marker sourced from the canonical workflow run origin vocabulary: `draft`, `member-invoke`, `team-invoke`, `default-invoke`, `service-invoke`, `ad-hoc-chat`, or `provisioned`. Member-scoped run endpoints return `member-invoke` for the member view even when the underlying service registry stores the broader service invocation source.
- `runId` is the stable business identity for frontend tracking. `commandId` and `correlationId` are tracing and idempotency identifiers only; clients must not treat them as the business run identity. For legacy reads, the backend may still resolve `/runs/{runId}` by `commandId` as a compatibility lookup, but the canonical tracking value remains `runId`.
- Async invoke `202 Accepted` only means the command was accepted for dispatch and a status URL is known. It does not mean the run has completed or that the read model has already observed the result. Use the returned `statusUrl` or the member run read endpoints for final status/output unless the SSE stream has already delivered terminal state.
- Responses use `publishedServiceId` instead of overloading `serviceId` in member-centric DTOs.
- The member-first public contract does not accept an `appId` override or expose the fixed service namespace.
- The legacy scope-service member binding routes under `Aevatar.GAgentService.Hosting` are intentionally removed; member binding uses the StudioMember async protocol so the member actor remains the single authority for `LastBinding`, active run status, and `BindReady`.
