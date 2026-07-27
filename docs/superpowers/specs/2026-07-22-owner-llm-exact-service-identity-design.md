# Owner LLM Exact Service Identity Design

**Status:** Approved for implementation planning

**Date:** 2026-07-22

## Context

Studio workflow schedule authorization must issue a dedicated, constrained NyxID Agent Key before a schedule becomes active. A workflow revision containing an `llm_call` requires authorization evidence for the owner's selected LLM route.

The current product contract stores only `preferred_llm_route` in `UserConfigGAgent`. For a NyxID proxy route, that value identifies a route or slug, not a unique `UserService.id`. `ScheduledInvocationAuthorizationPlanner` correctly rejects this incomplete evidence with:

```text
owner_llm_exact_service_identity_unavailable
```

Production verification on 2026-07-22 reproduced this failure after workflow binding and authorization-catalog refresh had succeeded. The failure occurred before Agent Key creation, and all temporary resources were cleaned up.

PR #2912 was merged into `feature/integrate` as `7bc4fe399` while this design was being prepared. It resolves the missing ID inside authorization preflight by issuing a short-lived bearer, querying the live or cached LLM catalog, and matching the stored route. That can make the request succeed, but it leaves the selected service identity outside the authoritative UserConfig state and introduces an external lookup into a query/planning path. This design supersedes that resolver portion of the merged implementation.

## Semantic Mismatch

The product currently implies that an LLM route is the complete persisted selection, but durable authorization requires the exact NyxID user-service identity selected by the user.

This is an:

- **ownership mismatch:** the selected service identity belongs to the user-scoped UserConfig authority, not to an authorization preflight request;
- **contract mismatch:** a route string cannot also mean an exact `UserService.id`;
- **runtime mismatch:** an empty or Gateway preference can currently fall through to a host default NyxID proxy route;
- **query-boundary mismatch:** preflight must consume committed read models and must not discover or repair authoritative identity.

## Goals

- Make the user's LLM selection a strong, actor-owned business fact.
- Give Gateway and NyxID user-service routes distinct, explicit semantics.
- Keep schedule preflight and authorization planning read-only.
- Preserve exact `UserService.id` identity without inferring it from a slug or route.
- Keep the existing constrained Agent Key policy:
  - `allow_all_services = false`;
  - `allow_all_nodes = false`;
  - exact service and node allowlists come from the authorization plan;
  - credential lifetime remains 90 days.
- Provide an honest migration path for legacy route-only UserConfig state.
- Keep the protobuf state, committed events, and current-state read model aligned.

## Non-Goals

- Do not change NyxID API contracts.
- Do not store NyxID bearer or refresh tokens in UserConfig or schedule state.
- Do not add query-time replay, projection priming, live catalog lookup, or slug-to-ID inference.
- Do not include the independent generic `scheduled_agent_creator` missing-`AuthorizationFact` issue.
- Do not redesign unrelated provider settings, workflow authoring, or generic `/api/schedules` semantics.
- Do not revert PR #2912 wholesale; replace only its non-compliant query-time identity resolution while retaining independently valid binding propagation.

## Semantic Owner

Each `UserConfigGAgent` is the single authoritative owner of the LLM selection for one typed resource key. Studio schedule authorization uses only the authenticated owner-scope resource:

```text
UserConfigResourceKey.ForOwnerScope(scopeId) -> user-config-{scopeId}
```

Channel `/model` preferences are a distinct binding-scoped resource:

```text
UserConfigResourceKey.ForChannelBinding(bindingId) -> channel-user-config-{bindingId}
```

The top-level actor ID prefixes are structurally disjoint, so opaque values cannot collide across resource kinds. For example, owner scope `binding-alpha` maps to `user-config-binding-alpha`, while binding ID `alpha` maps to `channel-user-config-alpha`. The owner mapping intentionally remains `user-config-{scopeId}` so existing production owner state requires no key migration. Historical channel preferences written under `user-config-{bindingId}` are not read as a fallback because they can collide with owner scope; channel users explicitly reselect after deployment if needed.

The two keys are not aliases and cannot be passed as bare interchangeable strings. `ProjectionScheduledInvocationOwnerLLMQueryPort` accepts only an owner `scopeId` and reads only `ForOwnerScope(scopeId)`. Channel writes may reuse the selection schema and actor behavior, but a binding-scoped selection is never schedule-owner authorization evidence. No binding-to-owner propagation is added in this change; that would require a separate verified ownership contract.

NyxID remains the authority for the user's service inventory and service-grant facts. The UserConfig selection records which exact inventory item the user selected. The authorization catalog read model records whether that selected item remains active and grantable.

The resulting responsibilities are:

| Concern | Authority |
| --- | --- |
| Studio owner selected Gateway or a NyxID service | owner-scoped `UserConfigGAgent` |
| Channel binding selected a local conversation route | binding-scoped `UserConfigGAgent` |
| Exact selected `UserService.id` and route snapshot | the corresponding `UserConfigGAgent` |
| Current NyxID inventory, resource owner, and node grants | NyxID authorization catalog actor/read model |
| Workflow requires owner LLM routing | Published workflow revision authorization evidence |
| Schedule authorization decision | `ScheduledInvocationAuthorizationPlanner` over read models |
| Dedicated Agent Key material | NyxID plus Aevatar secret vault |

## Typed Selection Contract

Add a typed sub-message to the UserConfig protobuf contract:

```proto
enum UserLlmRouteKind {
  USER_LLM_ROUTE_KIND_UNSPECIFIED = 0;
  USER_LLM_ROUTE_KIND_GATEWAY = 1;
  USER_LLM_ROUTE_KIND_NYX_ID_USER_SERVICE = 2;
}

message UserLlmSelection {
  UserLlmRouteKind route_kind = 1;
  string route_value = 2;
  string nyx_id_user_service_id = 3;
  string service_slug_snapshot = 4;
}
```

Add `UserLlmSelection llm_selection` to:

- `UserConfigGAgentState`;
- `UserConfigUpdatedEvent`;
- the Application-layer `UserConfig` read contract and `UserConfigUpdate` value;
- `UserConfigCurrentStateDocument`.

The sub-message is the authoritative selection. Existing `preferred_llm_route` fields remain temporarily readable for historical protobuf state and wire compatibility, but new writes derive the public route view from `llm_selection`. They are not a second authority for exact identity.

The old field must not be used to reconstruct a missing `nyx_id_user_service_id` in query or planner code. It can only be consumed by an explicit, authenticated write-side migration or reselection flow.

UserConfig mutation uses a typed delta command rather than dispatching a domain event directly:

```proto
message UpdateUserConfigCommand {
  optional string default_model = 1;
  UserLlmSelection llm_selection = 2;
  optional string runtime_mode = 3;
  optional string local_runtime_base_url = 4;
  optional string remote_runtime_base_url = 5;
  optional int32 max_tool_rounds = 6;
  optional string github_username = 7;
}
```

Scalar presence means "update this field"; absence means "preserve the actor-owned value." Message presence on `llm_selection` has the same meaning. There is no clear-to-unspecified command: Reset writes an explicit Gateway selection. `UserConfigUpdatedEvent` remains the committed full-state event produced by the actor after it validates and merges the delta.

When `llm_selection` is present, the actor writes both the typed selection and the compatibility `preferred_llm_route` derived from `selection.route_value` into the committed event. When it is absent, the actor preserves both current fields. Callers cannot provide `preferred_llm_route` independently, so the two fields cannot diverge on new writes.

Application abstractions expose an equivalent typed `UserConfigUpdate` value and `IUserConfigCommandService.UpdateAsync(UserConfigResourceKey, UserConfigUpdate, CancellationToken)`. The Projection command adapter maps that value one-to-one into the generated protobuf command. Application code does not depend directly on the actor implementation assembly, and the adapter does not read or merge state.

## Invariants

### Gateway

A Gateway selection has:

- `route_kind = GATEWAY`;
- canonical `route_value = /api/v1/llm/gateway/v1`;
- empty `nyx_id_user_service_id`;
- empty `service_slug_snapshot`.

Gateway does not require a NyxID user-service grant in the scheduled authorization plan. `UserConfigLlmRouteDefaults.Gateway` and all new Gateway read models use the canonical route above; an empty string remains only an accepted write-boundary alias.

An empty, `auto`, or `gateway` user preference normalizes to this explicit Gateway selection. Runtime routing must not silently replace it with a proxy route.

### NyxID User Service

A NyxID user-service selection has:

- `route_kind = NYX_ID_USER_SERVICE`;
- a canonical proxy `route_value`;
- a non-empty exact `nyx_id_user_service_id`;
- a non-empty `service_slug_snapshot`.

The ID is selected from a typed identity whose authority is the published NyxID `GET /api/v1/user-services` inventory:

```csharp
public enum UserLlmIdentityAuthority
{
    Unspecified = 0,
    NyxIdUserServicesInventory = 1,
}

public sealed record UserLlmServiceIdentity(
    UserLlmIdentityAuthority Authority,
    string NyxIdUserServiceId);
```

`UserLlmOption` carries `UserLlmServiceIdentity? Identity`; it does not carry a generic `ServiceId`. Only an active, allowed `NyxIdUserService` parsed by the existing strict `NyxIdApiAccessResponseParser.ParseUserServices` can create an identity with `NyxIdUserServicesInventory` authority. The inventory's `id` is the exact `UserService.id`, and its `slug` supplies the route and slug snapshot.

The LLM services, unified keys, and proxy-services responses remain presentation and route-discovery inputs. Their string `source` values and IDs are not identity provenance. They may enrich an inventory-backed option with labels and models, but they cannot create or replace `UserLlmServiceIdentity`. The current `/api/v1/keys` fallback that substitutes a key ID or slug therefore never produces an exact identity.

Route diagnostics expose no ambiguous `serviceId`. Inventory-backed options expose `userServiceId`; catalog-only diagnostics either omit an ID or use a separately named internal `catalogServiceId` that is not accepted by a selection command. Neither the actor, projector, query port, planner, nor UI may derive `UserService.id` from a route, slug, display name, source string, key ID, or catalog ID.

### Unspecified Or Legacy

An unspecified selection or a proxy-shaped legacy route with no exact inventory identity is not valid durable authorization evidence. Interactive runtime behavior may continue to expose legacy settings for repair, but schedule preflight fails closed with a stable authorization error.

## Catalog Composition

Both Studio and channel catalog adapters add `GET /api/v1/user-services` to their authenticated write-side discovery flow and parse it with `NyxIdApiAccessResponseParser.ParseUserServices`. Eligibility matches the authorization catalog: the inventory item is active and its personal or allowed organization credential source is usable.

The adapter creates one identity-bearing option per eligible inventory ID. It never deduplicates these options by slug, route, display name, or catalog ID. Two `UserService` records with the same slug remain two distinct choices. LLM services, unified keys, and proxy-services candidates may be joined by normalized route/slug only to add models, labels, readiness diagnostics, and descriptions to each inventory choice; that join cannot alter or synthesize its identity.

Catalog-only route candidates remain non-selectable diagnostics. A response field named `userServiceId` is populated only from `UserLlmServiceIdentity` with `NyxIdUserServicesInventory` authority.

## Write Path

The existing `UserLlmPreferenceWriter` is the correct application boundary for materializing an exact selection because it receives:

- the authenticated bearer for the settings request;
- the typed LLM catalog options;
- an explicit `userServiceId` or Gateway reset command.

The write flow is:

1. Resolve the typed owner-scope or binding-scope resource key at the authenticated application boundary.
2. Resolve the user's explicit settings command without reading UserConfig for write-side merge.
3. For Gateway, construct the canonical Gateway selection without catalog lookup.
4. For a service selection, load `/api/v1/user-services` plus presentation catalog data and select exactly one option whose `Identity.Authority` is `NyxIdUserServicesInventory` and whose `Identity.NyxIdUserServiceId` exactly equals `userServiceId`.
5. Build `UserLlmSelection` from that inventory-backed option.
6. Dispatch `UpdateUserConfigCommand` containing only the fields changed by this request.
7. Let `UserConfigGAgent` validate the command, merge it against actor-owned current state, and persist one complete `UserConfigUpdatedEvent`.
8. Publish committed state through the normal projection pipeline.

Proxy writes require an explicit `userServiceId`. A proxy `routeValue` without `userServiceId` is rejected even when a catalog contains one matching route or slug. An ID present only in LLM services, unified keys, or proxy services is also rejected. The persisted event always contains the exact inventory `UserService.id` selected by the caller.

Exact-ID selection uses a narrow helper dedicated to inventory identity. It must not call `UserLlmPreferenceWriteCore.FindOption`, because that helper intentionally performs broad ID, slug, display-name, route, and related-option matching for interactive discovery. Broad matching remains a presentation/discovery concern and cannot enter the authoritative write path.

Application services never read a UserConfig read model in order to merge a write. Model-only, runtime, GitHub, and selection updates omit unrelated command fields; actor serialization preserves the latest committed values. This prevents an eventually consistent read model from overwriting a newer actor state.

Model-only writes preserve the current typed selection by omitting `llm_selection`. A route-prefixed model without `userServiceId` is rejected rather than selecting a service by slug. Presets that choose an existing service must resolve their declared `userServiceId` to exactly one inventory-backed option. Provisioning must refresh `/api/v1/user-services` and select the resulting inventory item before dispatch; a provisioning response alone cannot mint identity provenance.

The generic `PUT /api/user-config` endpoint remains available for non-LLM settings and bare model-only updates, but `preferredLlmRoute` is removed from its write contract. All route selection goes through `PUT /api/user-config/llm`, so the generic endpoint cannot create route-only proxy state.

## Read And Authorization Path

`ProjectionScheduledInvocationOwnerLLMQueryPort` reads only `UserConfigCurrentStateDocument`.

It maps:

- Gateway selection to `AuthorizationGrantRequirement.NotRequired`;
- valid NyxID user-service selection to exact `NyxIdServiceId`, slug snapshot, route snapshot, and `AuthorizationGrantRequirement.Required`;
- missing or invalid typed selection to fail-closed evidence.

The query port performs no external calls and no writes.

`ScheduledInvocationAuthorizationPlanner` then:

1. reads member and published workflow revision evidence;
2. reads owner LLM selection evidence from the UserConfig read model;
3. reads the owner-scoped NyxID authorization catalog read model;
4. joins the exact selected `UserService.id` against catalog evidence;
5. rejects missing, stale, inaccessible, ambiguous, or owner-mismatched facts;
6. emits the constrained service grants and source stamps.

The UserConfig `StateVersion` remains the source stamp for the selection. The NyxID authorization catalog actor version remains the authority stamp for inventory and grant facts.

No `IScheduledInvocationOwnerLLMServiceIdentityResolver` is required in the planner.

## Gateway Runtime Semantics

The product must not describe an empty preference as Gateway while runtime silently applies a proxy default.

This change makes Gateway explicit:

- UserConfig persists `route_kind = GATEWAY`.
- The owner LLM runtime configuration selects `/api/v1/llm/gateway/v1`.
- `UserConfigLlmRouteDefaults.Gateway` resolves to `/api/v1/llm/gateway/v1`, and empty/`auto`/`gateway` settings inputs normalize to that value at write boundaries.
- `NyxIdLLMProvider` treats the canonical route and explicit `gateway` alias as Gateway even when `DefaultRoute` is `chrono-llm-public`; only an absent request preference may use the host default.
- An absent typed selection remains `UNSPECIFIED`. Interactive runtime may use its configured host default, but schedule authorization cannot treat that default as an owner selection.
- A host default proxy route cannot act as an implicit per-user selection because it has no authoritative `UserService.id`.

If a deployment requires `chrono-llm-public`, the user must select that exact NyxID service and persist its `UserService.id`.

## Legacy State And Repair

Normal query and preflight paths do not repair legacy state.

For legacy users with only `preferred_llm_route`:

- Gateway-shaped values materialize as Gateway on the next explicit settings write.
- Proxy-shaped values remain visible as legacy settings but are not sufficient for schedule authorization.
- The user must explicitly reselect the service through the typed settings command, or an operator may run a separately named authenticated write-side repair.

The initial implementation does not add a general background repair actor. This keeps the change scoped and avoids assigning exact identity without an explicit user or operator decision.

For the production verification scope, the existing `chrono-llm-public` option will be reselected by its exact `UserService.id` after deployment. The operation preserves the existing model and route semantics while committing the previously missing identity fact.

## API And UI Contract

`GET /api/user-config` exposes the typed `llmSelection` alongside the legacy `preferredLlmRoute` route view. `GET /api/user-config/llm` adds `savedRouteKind`, `savedUserServiceId`, and `savedServiceSlug` to its settings view. `preferredLlmRoute` and `savedRoute` remain route views, never IDs.

`PUT /api/user-config/llm` accepts an explicit `userServiceId` or Gateway selection:

- `SaveUserLlmSettingsRequest` adds `userServiceId`; the existing `routeValue` and `model` fields do not carry identity;
- `userServiceId` selects one exact inventory-backed option;
- an empty or canonical Gateway `routeValue` selects Gateway;
- any non-Gateway `routeValue` without `userServiceId` is rejected;
- `userServiceId` and Gateway route in the same command are rejected as conflicting meanings;
- unknown user-service IDs fail without committing state.

The route option response uses `userServiceId` only for inventory-backed identities. It does not expose a generic `serviceId`; catalog-only diagnostics therefore cannot place a catalog or key ID into a field consumed by selection writes.

The UI does not invent or display backend identity rules. It uses `savedRouteKind` plus `savedUserServiceId` as draft identity, keys user-service choices by `userServiceId`, and sends the selected option's exact ID. Route strings remain display/routing data and are not used to deduplicate choices or decide which service is selected. Gateway is a dedicated choice with no user-service ID. Options without an inventory-backed `userServiceId` may appear in provider health details but are excluded from the selectable route control.

## Merged PR #2912 Integration Boundary

Merge commit `7bc4fe399` contains adjacent work that must be reviewed independently:

- verified binding propagation into workflow tool context can be retained;
- removal of fabricated binding handles can be retained;
- low-level schedule authorization changes can be retained only if they preserve the same typed authorization contract;
- query-time `StudioOwnerLLMServiceIdentityResolver`, `IScheduledInvocationOwnerLLMServiceIdentityResolver`, their DI registration and test doubles, and planner live-catalog lookup must be removed.

The final branch starts from the merged PR and replaces its non-compliant resolver. It must not layer the actor-owned selection beside the query-time resolver as a fallback or second authority.

The retained binding path must be verified end to end, not only from a pre-populated workflow credential fixture. Credential source and caller identity remain separate: `ScheduledServiceInvocationAuth` carries its existing credential-source oneof plus a typed `ScheduledCallerNyxIdAuthority` field. Studio builds that authority from `AuthenticatedAuthorizationOwnerContext`, including the exact `VerifiedBindingId`, and persists it in scheduled state even when the credential source is `scheduled_invocation_agent_key`.

Scheduled dispatch copies this committed caller authority into workflow invocation, then the existing workflow chain copies it into `WorkflowCallerNyxIdAuthority.BindingId` and tool context. Missing binding fails closed; no subject-derived or `nyxid:{user}` handle is fabricated. The binding fact remains distinct from credential kind, owner scope, and the selected LLM `UserService.id`.

## Error Handling

Stable failures remain sanitized and contain no credentials:

| Condition | Result |
| --- | --- |
| Legacy proxy route has no exact selection | `owner_llm_exact_service_identity_unavailable` |
| Requested ID lacks inventory provenance | write rejected before dispatch |
| Selected ID absent from current catalog | `nyxid_service_not_found:{id}` |
| Selected service access denied | `nyxid_service_access_denied:{id}` |
| Slug snapshot changed for the same ID | stale authorization evidence |
| UserConfig projection missing | owner LLM authorization evidence not found |
| Catalog projection behind | typed projection-pending response |

Bearer tokens, Agent Keys, secret references, and vault ciphertext never appear in errors, read models, logs, tests, or verification output.

## Tests

### Contract And Actor

- Protobuf round-trip covers Gateway and NyxID service selections.
- Actor receives `UpdateUserConfigCommand`, merges only present fields, and emits one full `UserConfigUpdatedEvent`.
- Two commands based on different client snapshots cannot overwrite each other's omitted fields.
- Actor rejects invalid combinations of route kind, route, ID, and slug.
- Actor transition preserves exact selection across unrelated UserConfig updates.
- Historical route-only state remains readable but does not synthesize an ID.

### Write Path

- `/api/v1/user-services` is the only adapter input that mints `UserLlmServiceIdentity`.
- `/api/v1/keys`, `/api/v1/llm/services`, and `/api/v1/proxy/services` cannot mint identity even when they contain an ID-shaped value or `source = user_service`.
- Selecting by distinct `userServiceId` persists the matching inventory ID, route, and slug.
- Catalog-only options cannot be persisted as `NYX_ID_USER_SERVICE`.
- Two inventory services with the same slug are never resolved by slug when `userServiceId` is provided.
- Route-only proxy selection is rejected even when the typed catalog has one matching option.
- Gateway selection persists no service identity.
- Model-only updates preserve the current typed selection.
- A route-prefixed model without `userServiceId` is rejected.
- Reset writes the explicit Gateway selection.
- Owner-scope and binding-scope resource keys cannot be interchanged, and binding state is never returned as owner authorization evidence.
- Opaque owner scope `binding-alpha` and binding ID `alpha` resolve to different actor IDs; the owner key remains compatible with existing production state.

### Projection And Query

- Projector copies the selection with the authoritative actor state version.
- Older state versions cannot overwrite newer read models.
- Query port returns exact service evidence without external calls.
- Gateway maps to `NotRequired`.
- Proxy selection without an inventory-backed user-service ID fails closed.

### Planner

- Exact selected ID joins against the authorization catalog and produces one constrained grant.
- Slug-only evidence remains rejected.
- Missing, denied, owner-mismatched, and stale catalog evidence remains rejected.
- UserConfig and catalog source versions are preserved in the plan.

### Hosted And Integration

- `GET` and `PUT /api/user-config/llm` preserve distinct route and ID semantics.
- Generic `PUT /api/user-config` cannot create a route-only proxy selection.
- Console settings keys selectable services by exact ID, sends `userServiceId`, and excludes options without inventory provenance from selection.
- With `DefaultRoute = chrono-llm-public`, an explicit typed Gateway selection still routes `/api/v1/llm/gateway/v1`.
- A schedule created with verified binding `bnd-owner-alpha` carries that exact binding through scheduled dispatch into workflow tool context.
- `simple_qa` schedule preflight succeeds after explicit exact selection.
- Preflight performs no token exchange or live catalog call.
- Create provisions a dedicated constrained Agent Key.
- `run-now` completes using `scheduled_invocation_agent_key`.
- Delete revokes both NyxID key and vault secret.

## Verification

At minimum, run:

```bash
dotnet build aevatar.slnx --nologo
dotnet test test/Aevatar.AI.Tests/Aevatar.AI.Tests.csproj --nologo
dotnet test test/Aevatar.Studio.Tests/Aevatar.Studio.Tests.csproj --nologo
dotnet test test/Aevatar.Capabilities.Tests/Aevatar.Capabilities.Tests.csproj --nologo
dotnet test test/Aevatar.GAgents.ChannelRuntime.Tests/Aevatar.GAgents.ChannelRuntime.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Tests/Aevatar.GAgentService.Tests.csproj --nologo
dotnet test test/Aevatar.GAgentService.Integration.Tests/Aevatar.GAgentService.Integration.Tests.csproj --nologo
pnpm --dir apps/aevatar-console-web tsc
pnpm --dir apps/aevatar-console-web test --runInBand
pnpm --dir apps/aevatar-console-web build
bash tools/ci/test_stability_guards.sh
bash tools/ci/query_projection_priming_guard.sh
bash tools/ci/projection_state_version_guard.sh
bash tools/ci/projection_state_mirror_current_state_guard.sh
bash tools/ci/workflow_binding_boundary_guard.sh
bash tools/ci/architecture_guards.sh
git diff --check
```

## Production Acceptance

After the verified changes reach production:

1. Snapshot the current UserConfig model and route using non-secret fields only.
2. Explicitly reselect the existing `chrono-llm-public` service by exact `UserService.id`.
3. Confirm the projected UserConfig selection exposes the same route and the exact ID.
4. Create a temporary Team, workflow member, and `simple_qa` binding with distinct IDs.
5. Refresh the NyxID authorization catalog.
6. Confirm preflight returns one or more exact service grants and both `allow_all_*` flags are false.
7. Create the automation with dedicated Agent Key provisioning.
8. Confirm the automation is active and the new key is constrained.
9. Run now and confirm a completed successful workflow run.
10. Delete the automation and confirm the exact key is inactive.
11. Retire the revision, delete the member, archive the Team, and verify no temporary resources remain.

Kubernetes log access is currently blocked by an nginx 403. API, read-model, CLI, and key-lifecycle evidence remain the acceptance sources unless log access is restored.

## Acceptance Criteria

- UserConfig authoritative state contains a typed LLM selection.
- Every mutation is a typed delta command that the actor merges into its authoritative state before emitting a committed event.
- A proxy selection always contains an exact NyxID `UserService.id` minted only from `/api/v1/user-services` provenance.
- Owner-scope and binding-scope UserConfig resources are distinct; schedule authorization reads only owner scope.
- Gateway has explicit runtime semantics and no user-service ID.
- Preflight and planner make no live catalog or token-exchange calls.
- Schedule authorization joins only exact IDs from committed read models.
- Legacy route-only proxy state fails closed until an explicit write-side repair.
- Dedicated scheduled Agent Key creation, execution, revocation, and cleanup pass in production.
- All focused tests, build, and required architecture guards pass.
